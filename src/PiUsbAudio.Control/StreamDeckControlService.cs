using System.Threading.Channels;

namespace PiUsbAudio.Control;

public sealed class StreamDeckConfiguration
{
    public bool Enabled { get; set; } = true;
    public int Brightness { get; set; } = 30;

    public IReadOnlyList<string> Validate() => Brightness is < 0 or > 100
        ? ["streamDeck.brightness must be between 0 and 100"] : [];
}

public sealed record StreamDeckStatus(
    bool Enabled, bool Connected, string? Path, string Message, string? Error,
    long ButtonPresses, int? LastButton, DateTimeOffset? LastPress,
    IReadOnlyList<StreamDeckKeyDisplay> Keys);

// Reports are complete six-key snapshots. Detect individual down transitions;
// releases, held-key snapshots, and the unused fifth key have no action.
public sealed class StreamDeckButtons
{
    private readonly bool[] down = new bool[6];

    public IReadOnlyList<int> Handle(ReadOnlySpan<byte> report, bool prime = false)
    {
        if (report.Length < 7 || report[0] != 1)
            return [];
        for (var key = 0; key < 6; key++)
            if (report[key + 1] > 1)
                return [];
        var pressed = new List<int>();
        for (var key = 0; key < 6; key++)
        {
            var next = report[key + 1] == 1;
            if (!prime && key != 4 && next && !down[key])
                pressed.Add(key + 1);
            down[key] = next;
        }
        // PANIC wins over any mic toggles arriving in the same snapshot.
        return pressed.Contains(6) ? [6] : pressed;
    }
}

public sealed class StreamDeckControlService(
    RouterControl control,
    IStreamDeckDeviceProvider devices,
    ControlEventBus eventBus,
    ILogger<StreamDeckControlService> logger) : BackgroundService
{
    private sealed record Press(long Session, int Number);
    private readonly Channel<Press> presses = Channel.CreateBounded<Press>(new BoundedChannelOptions(32)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly object statusGate = new();
    private StreamDeckStatus status = new(true, false, null, "Waiting for a Stream Deck Mini.", null, 0, null, null, []);
    private long nextSession;
    private long activeSession;

    public StreamDeckStatus Status => Volatile.Read(ref status);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Keep blocking HID input/image writes independent of router actions
        // and PipeWire's audio threads. A slow screen upload cannot hold up a
        // mic action already received from the device.
        try
        {
            await Task.WhenAll(
                Task.Run(() => DeviceLoopAsync(stoppingToken), stoppingToken),
                ProcessPressesAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task DeviceLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            IStreamDeckConnection? connection = null;
            StreamDeckKeyDisplay[] displayed = [];
            try
            {
                var state = await control.GetStateAsync(stoppingToken);
                if (!state.Configuration.StreamDeck.Enabled)
                {
                    Update(current => current with
                    {
                        Enabled = false, Connected = false, Path = null, Keys = [],
                        Message = "Stream Deck control is disabled.", Error = null
                    });
                }
                else if (devices.Enumerate().FirstOrDefault() is not { } path)
                {
                    Update(current => current with
                    {
                        Enabled = true, Connected = false, Path = null, Keys = [],
                        Message = "Waiting for a Stream Deck Mini.", Error = null
                    });
                }
                else
                {
                    connection = devices.Open(path);
                    var brightness = state.Configuration.StreamDeck.Brightness;
                    connection.SetBrightness(brightness);
                    displayed = RefreshImages(connection, state, displayed, stoppingToken);
                    var buttons = new StreamDeckButtons();
                    // Establish a baseline from queued reports while connecting.
                    // A key already held at connection time must first be released.
                    for (var count = 0; count < 32; count++)
                    {
                        var initial = connection.ReadReport(0);
                        if (initial.Length == 0)
                            break;
                        buttons.Handle(initial, prime: true);
                    }
                    var session = Interlocked.Increment(ref nextSession);
                    Interlocked.Exchange(ref activeSession, session);
                    Update(current => current with
                    {
                        Enabled = true, Connected = true, Path = path, Keys = displayed,
                        Message = "Buttons 1–4 toggle their microphone feeds; button 6 mutes all four.", Error = null
                    });
                    logger.LogInformation("Stream Deck Mini connected at {Path}", path);
                    var refreshAt = Environment.TickCount64 + 200;
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        var report = connection.ReadReport(50);
                        foreach (var number in buttons.Handle(report))
                        {
                            if (!presses.Writer.TryWrite(new Press(session, number)))
                                throw new IOException("Stream Deck action queue is full; reconnecting.");
                            Update(current => current with
                            {
                                ButtonPresses = current.ButtonPresses + 1,
                                LastButton = number, LastPress = DateTimeOffset.UtcNow
                            });
                        }
                        if (Environment.TickCount64 < refreshAt)
                            continue;
                        state = await control.GetStateAsync(stoppingToken);
                        if (!state.Configuration.StreamDeck.Enabled)
                            break;
                        if (brightness != state.Configuration.StreamDeck.Brightness)
                        {
                            brightness = state.Configuration.StreamDeck.Brightness;
                            connection.SetBrightness(brightness);
                        }
                        var next = RefreshImages(connection, state, displayed, stoppingToken);
                        if (!next.SequenceEqual(displayed))
                        {
                            displayed = next;
                            Update(current => current with { Keys = next });
                        }
                        refreshAt = Environment.TickCount64 + 200;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or DllNotFoundException or EntryPointNotFoundException)
            {
                if (Status.Error != exception.Message)
                    logger.LogWarning(exception, "Stream Deck unavailable; retrying");
                Update(current => current with
                {
                    Connected = false, Path = null, Keys = [],
                    Message = "Stream Deck unavailable; retrying.", Error = exception.Message
                });
            }
            finally
            {
                Interlocked.Exchange(ref activeSession, 0);
                if (connection is not null)
                {
                    try
                    {
                        // Clear authoritative-looking LIVE/MUTED labels on a
                        // normal stop/disable. No mic state changes on detach.
                        foreach (var key in displayed.Where(key => key.Button != 5))
                            connection.SetKeyImage(key.Button - 1,
                                StreamDeckImages.Render(key with { State = "OFFLINE" }));
                    }
                    catch (IOException) { /* Expected when physically unplugged. */ }
                    connection.Dispose();
                }
                Update(current => current with { Connected = false, Path = null, Keys = [] });
            }
            await Task.Delay(1000, stoppingToken);
        }
    }

    private static StreamDeckKeyDisplay[] RefreshImages(
        IStreamDeckConnection connection, ControlState state,
        StreamDeckKeyDisplay[] previous, CancellationToken cancellationToken)
    {
        var next = Enumerable.Range(1, 6).Select(number =>
        {
            if (number == 6)
                return new StreamDeckKeyDisplay(6, "PANIC", "MUTE ALL");
            var device = state.Configuration.Devices.SingleOrDefault(device => device.Number == number);
            return device is null
                ? new StreamDeckKeyDisplay(number, "", "UNASSIGNED")
                : new StreamDeckKeyDisplay(number, device.FriendlyName,
                    state.Routing?.Success != true ? "ERROR" : device.InputEnabled ? "LIVE" : "MUTED");
        }).ToArray();
        for (var key = 0; key < next.Length; key++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key >= previous.Length || next[key] != previous[key])
                connection.SetKeyImage(key, StreamDeckImages.Render(next[key]));
        }
        return next;
    }

    private async Task ProcessPressesAsync(CancellationToken stoppingToken)
    {
        await foreach (var press in presses.Reader.ReadAllAsync(stoppingToken))
        {
            if (press.Session != Interlocked.Read(ref activeSession))
                continue;
            try
            {
                var state = press.Number == 6
                    ? await control.SetMicrophoneEnabledAsync(null, false, stoppingToken)
                    : await control.ToggleMicrophoneEnabledAsync(press.Number, stoppingToken);
                var error = state.Routing?.Success == true ? null : "Microphone routing failed; check audio diagnostics.";
                Update(current => current with { Error = error });
                if (press.Number == 6)
                    logger.LogInformation("Stream Deck PANIC: all four microphone feeds muted");
                else
                    logger.LogInformation("Stream Deck button {Number}: mic {State}", press.Number,
                        state.Configuration.Devices.Single(device => device.Number == press.Number).InputEnabled ? "live" : "muted");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // Never retry a toggle automatically: it might already have
                // been persisted before applying the routing failed.
                logger.LogWarning(exception, "Stream Deck button {Number} action failed", press.Number);
                Update(current => current with { Error = exception.Message });
            }
        }
    }

    private void Update(Func<StreamDeckStatus, StreamDeckStatus> change)
    {
        lock (statusGate)
        {
            var next = change(status);
            if (next with { Keys = status.Keys } == status && next.Keys.SequenceEqual(status.Keys))
                return;
            Volatile.Write(ref status, next);
            eventBus.Publish("stream-deck", next);
        }
    }
}
