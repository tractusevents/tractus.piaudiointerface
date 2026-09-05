using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PiUsbAudio.Control;

public sealed class SerialControlService(
    RouterControl control,
    ControlEventBus eventBus,
    ILogger<SerialControlService> logger) : BackgroundService
{
    private const short PollIn = 0x0001;
    private const short PollError = 0x0008;
    private const short PollHangUp = 0x0010;
    private const short PollInvalid = 0x0020;
    private const int InterruptedSystemCall = 4;
    private const int OpenReadWrite = 0x0002;
    private const int OpenNoControllingTerminal = 0x0100;
    private const int OpenCloseOnExec = 0x80000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private volatile bool pauseRequested;
    private readonly string devicePath =
        Environment.GetEnvironmentVariable("PI_USB_AUDIO_SERIAL") ?? "/dev/ttyGS0";

    public async Task<IDisposable> PauseAsync(CancellationToken cancellationToken = default)
    {
        pauseRequested = true;
        try
        {
            await connectionLock.WaitAsync(cancellationToken);
            return new PauseScope(this);
        }
        catch
        {
            pauseRequested = false;
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            var connectionLockHeld = false;
            try
            {
                if (pauseRequested)
                {
                    await Task.Delay(100, stoppingToken);
                    continue;
                }
                if (!File.Exists(devicePath))
                {
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                await connectionLock.WaitAsync(stoppingToken);
                connectionLockHeld = true;
                if (pauseRequested)
                    continue;

                await using var stream = OpenSerialDevice();
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\r\n"
                };
                var pollDescriptor = new PollDescriptor
                {
                    FileDescriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt32(),
                    Events = PollIn
                };
                var readBuffer = new byte[1024];
                var commandBuffer = new List<byte>(256);

                logger.LogInformation("USB serial control is ready on {DevicePath}", devicePath);
                while (!stoppingToken.IsCancellationRequested && !pauseRequested)
                {
                    pollDescriptor.ReturnedEvents = 0;
                    var pollResult = Poll(ref pollDescriptor, 1, 250);
                    if (pollResult < 0)
                    {
                        if (Marshal.GetLastPInvokeError() == InterruptedSystemCall)
                            continue;
                        throw new IOException($"poll failed with errno {Marshal.GetLastPInvokeError()}");
                    }
                    if (pollResult == 0)
                        continue;
                    if ((pollDescriptor.ReturnedEvents & (PollError | PollHangUp | PollInvalid)) != 0)
                        break;
                    if (pauseRequested)
                        break;

                    var bytesRead = stream.Read(readBuffer, 0, readBuffer.Length);
                    if (bytesRead == 0)
                        break;

                    for (var offset = 0; offset < bytesRead; offset++)
                    {
                        var value = readBuffer[offset];
                        if (value == (byte)'\r')
                            continue;
                        if (value != (byte)'\n')
                        {
                            if (commandBuffer.Count < 16 * 1024)
                                commandBuffer.Add(value);
                            continue;
                        }

                        var command = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(commandBuffer));
                        commandBuffer.Clear();
                        var response = await ExecuteCommandAsync(command, stoppingToken);
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug("USB serial control disconnected: {Message}", exception.Message);
                if (connectionLockHeld)
                {
                    connectionLock.Release();
                    connectionLockHeld = false;
                }
                await Task.Delay(1000, stoppingToken);
            }
            finally
            {
                if (connectionLockHeld)
                    connectionLock.Release();
            }
        }
    }

    private sealed class PauseScope(SerialControlService owner) : IDisposable
    {
        private SerialControlService? owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is null)
                return;
            current.pauseRequested = false;
            current.connectionLock.Release();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int Poll(ref PollDescriptor fileDescriptors, nuint descriptorCount, int timeoutMilliseconds);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    private FileStream OpenSerialDevice()
    {
        // systemd services are session leaders. Opening a terminal without
        // O_NOCTTY would make ttyGS0 the service's controlling terminal, so a
        // USB gadget rebind would send SIGHUP to the complete web/router
        // process. O_CLOEXEC also prevents leaking the device to child tools.
        var descriptor = Open(
            devicePath,
            OpenReadWrite | OpenNoControllingTerminal | OpenCloseOnExec);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"Could not open {devicePath} (errno {error}).");
        }

        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            return new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private async Task<object> ExecuteCommandAsync(string command, CancellationToken cancellationToken)
    {
        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            if (words.Length == 0)
                return Failure("Empty command. Send HELP for syntax.");

            if (Is(words[0], "HELP"))
                return new
                {
                    ok = true,
                    commands = new[]
                    {
                        "STATUS", "MUTE MIC <1-4|ALL>", "UNMUTE MIC <1-4|ALL>",
                        "MUTE OUTPUT <1-4|ALL>", "UNMUTE OUTPUT <1-4|ALL>",
                        "SOLO OUTPUT <1-4>", "SOLO EXCLUSIVE <1-4>",
                        "UNSOLO OUTPUT <1-4|ALL>",
                        "GAIN MIC <1-4> <0-150>", "GAIN OUTPUT <1-4> <0-150>",
                        "GAIN MASTER <0-150>", "SIDETONE <ON|OFF>",
                        "GAIN SIDETONE <0-150>", "GAIN NDI <0-150>",
                        "DUCK <ON|OFF>", "DUCK TRIGGERS",
                        "DUCK TRIGGER <SELF|1-4> <ON|OFF>",
                        "DUCK PRIORITY <SELF|1-4>",
                        "DUCK THRESHOLD <-90..0>", "DUCK DEPTH <0..60>",
                        "DUCK ATTACK <1..2000>", "DUCK HOLD <0..5000>",
                        "DUCK RELEASE <1..10000>", "METERS",
                        "NDI <ON|OFF>", "NDI NAME <source name>", "NDI STATUS",
                        "NDI SOURCES", "NDI RECEIVE <ON|OFF>",
                        "NDI RECEIVE SOURCE <source name>"
                    }
                };
            if (Is(words[0], "STATUS"))
                return Success("Current state", await control.GetStateAsync(cancellationToken));
            if (Is(words[0], "METERS"))
                return eventBus.LastMeters is { } meters
                    ? new { ok = true, message = "Current meters", meters }
                    : Failure("Meter data is not available yet.");

            if (Is(words[0], "NDI") && words.Length == 2 && Is(words[1], "STATUS"))
                return eventBus.LastNdi is { } ndi
                    ? new { ok = true, message = "Current NDI status", ndi }
                    : Failure("NDI status is not available yet.");

            if (Is(words[0], "NDI") && words.Length == 2 && Is(words[1], "SOURCES"))
                return eventBus.LastNdiSources is { } sources
                    ? new { ok = true, message = "Discovered NDI sources", sources }
                    : Failure("NDI source discovery is not available yet.");

            if (Is(words[0], "NDI") && words.Length == 3 && Is(words[1], "RECEIVE") &&
                (Is(words[2], "ON") || Is(words[2], "OFF")))
            {
                var enabled = Is(words[2], "ON");
                var state = await control.SetNdiReceiverEnabledAsync(enabled, cancellationToken);
                return Success($"NDI receiver {(enabled ? "enabled" : "disabled")}", state);
            }

            if (Is(words[0], "NDI") && words.Length >= 4 &&
                Is(words[1], "RECEIVE") && Is(words[2], "SOURCE"))
            {
                var sourceName = string.Join(' ', words.Skip(3));
                var state = await control.SetNdiReceiverSourceAsync(sourceName, cancellationToken);
                return Success("NDI receiver source updated", state);
            }

            if (Is(words[0], "NDI") && words.Length == 2 &&
                (Is(words[1], "ON") || Is(words[1], "OFF")))
            {
                var enabled = Is(words[1], "ON");
                var state = await control.SetNdiEnabledAsync(enabled, cancellationToken);
                return Success($"NDI audio {(enabled ? "enabled" : "disabled")}", state);
            }

            if (Is(words[0], "NDI") && words.Length >= 3 && Is(words[1], "NAME"))
            {
                var sourceName = string.Join(' ', words.Skip(2));
                var state = await control.SetNdiSourceNameAsync(sourceName, cancellationToken);
                return Success("NDI source name updated", state);
            }

            if (Is(words[0], "MUTE") || Is(words[0], "UNMUTE"))
            {
                var enabled = Is(words[0], "UNMUTE");
                var targetOffset = 1;
                var target = "MIC";
                if (words.Length > 1 && (Is(words[1], "MIC") || Is(words[1], "OUTPUT") || Is(words[1], "OUT")))
                {
                    target = words[1];
                    targetOffset = 2;
                }
                if (words.Length <= targetOffset)
                    return Failure("Missing device number or ALL.");
                var number = ParseDevice(words[targetOffset]);
                var state = Is(target, "MIC")
                    ? await control.SetMicrophoneEnabledAsync(number, enabled, cancellationToken)
                    : await control.SetOutputEnabledAsync(number, enabled, cancellationToken);
                return Success($"{target} {(enabled ? "unmuted" : "muted")}", state);
            }

            if (Is(words[0], "SIDETONE") && words.Length == 2 &&
                (Is(words[1], "ON") || Is(words[1], "OFF")))
            {
                var enabled = Is(words[1], "ON");
                var state = await control.SetSidetoneEnabledAsync(enabled, cancellationToken);
                return Success($"Sidetone {(enabled ? "enabled" : "muted")}", state);
            }

            if (Is(words[0], "GAIN") && words.Length == 4)
            {
                var percent = ParsePercent(words[3]);
                var number = ParseRequiredDevice(words[2]);
                var state = Is(words[1], "MIC")
                    ? await control.SetMicrophoneGainAsync(number, percent / 100.0, cancellationToken)
                    : Is(words[1], "OUTPUT") || Is(words[1], "OUT")
                        ? await control.SetOutputGainAsync(number, percent / 100.0, cancellationToken)
                        : throw new InvalidDataException("GAIN target must be MIC or OUTPUT.");
                return Success("Gain updated", state);
            }

            if (Is(words[0], "GAIN") && words.Length == 3 && Is(words[1], "MASTER"))
            {
                var state = await control.SetPhysicalOutputGainAsync(
                    ParsePercent(words[2]) / 100.0,
                    cancellationToken);
                return Success("Master gain updated", state);
            }

            if (Is(words[0], "GAIN") && words.Length == 3 && Is(words[1], "SIDETONE"))
            {
                var state = await control.SetSidetoneGainAsync(
                    ParsePercent(words[2]) / 100.0,
                    cancellationToken);
                return Success("Sidetone gain updated", state);
            }

            if (Is(words[0], "GAIN") && words.Length == 3 && Is(words[1], "NDI"))
            {
                var state = await control.SetNdiReceiverGainAsync(
                    ParsePercent(words[2]) / 100.0,
                    cancellationToken);
                return Success("NDI receiver gain updated", state);
            }

            if (Is(words[0], "SOLO") && words.Length == 3 && Is(words[1], "OUTPUT"))
            {
                var state = await control.SetOutputSoloAsync(
                    ParseRequiredDevice(words[2]), true, false, cancellationToken);
                return Success("Output solo enabled", state);
            }

            if (Is(words[0], "SOLO") && words.Length == 3 && Is(words[1], "EXCLUSIVE"))
            {
                var state = await control.SetOutputSoloAsync(
                    ParseRequiredDevice(words[2]), true, true, cancellationToken);
                return Success("Exclusive output solo enabled", state);
            }

            if (Is(words[0], "UNSOLO") && words.Length == 3 && Is(words[1], "OUTPUT"))
            {
                var number = ParseDevice(words[2]);
                var state = number is null
                    ? await control.ClearOutputSoloAsync(cancellationToken)
                    : await control.SetOutputSoloAsync(number.Value, false, false, cancellationToken);
                return Success("Output solo cleared", state);
            }

            if (Is(words[0], "DUCK") && words.Length == 2 &&
                (Is(words[1], "ON") || Is(words[1], "OFF") || Is(words[1], "BYPASS")))
            {
                var state = await control.SetDuckingEnabledAsync(Is(words[1], "ON"), cancellationToken);
                return Success($"Ducking {(Is(words[1], "ON") ? "enabled" : "bypassed")}", state);
            }

            if (Is(words[0], "DUCK") && words.Length == 2 && Is(words[1], "TRIGGERS"))
            {
                var state = await control.GetStateAsync(cancellationToken);
                return new
                {
                    ok = true,
                    message = "Current ducking triggers",
                    triggers = state.Configuration.Ducking.TriggerSources
                };
            }

            if (Is(words[0], "DUCK") && words.Length == 4 && Is(words[1], "TRIGGER") &&
                (Is(words[3], "ON") || Is(words[3], "OFF")))
            {
                var source = ParseDuckingPriority(words[2]);
                var enabled = Is(words[3], "ON");
                var state = await control.SetDuckingTriggerAsync(source, enabled, cancellationToken);
                return Success($"Ducking trigger {(enabled ? "enabled" : "disabled")}", state);
            }

            if (Is(words[0], "DUCK") && words.Length == 3)
            {
                ControlState state;
                if (Is(words[1], "PRIORITY"))
                    state = await control.SetDuckingPriorityAsync(ParseDuckingPriority(words[2]), cancellationToken);
                else if (Is(words[1], "THRESHOLD"))
                    state = await control.SetDuckingThresholdAsync(
                        ParseValue(words[2], -90, 0, "Threshold"), cancellationToken);
                else if (Is(words[1], "DEPTH"))
                    state = await control.SetDuckingDepthAsync(
                        ParseValue(words[2], 0, 60, "Depth"), cancellationToken);
                else if (Is(words[1], "ATTACK"))
                    state = await control.SetDuckingAttackAsync(
                        ParseValue(words[2], 1, 2000, "Attack"), cancellationToken);
                else if (Is(words[1], "HOLD"))
                    state = await control.SetDuckingHoldAsync(
                        ParseValue(words[2], 0, 5000, "Hold"), cancellationToken);
                else if (Is(words[1], "RELEASE"))
                    state = await control.SetDuckingReleaseAsync(
                        ParseValue(words[2], 1, 10000, "Release"), cancellationToken);
                else
                    return Failure("Unknown DUCK setting. Send HELP for syntax.");
                return Success("Ducking setting updated", state);
            }

            return Failure("Unknown command. Send HELP for syntax.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return Failure(exception.Message);
        }
    }

    private static int? ParseDevice(string value) =>
        Is(value, "ALL") ? null : ParseRequiredDevice(value);

    private static int ParseRequiredDevice(string value) =>
        int.TryParse(value, out var number) && number is >= 1 and <= 4
            ? number
            : throw new InvalidDataException("Device number must be 1-4.");

    private static int ParseDuckingPriority(string value) =>
        Is(value, "SELF") || Is(value, "LOCAL") || value == "0"
            ? 0
            : ParseRequiredDevice(value);

    private static double ParsePercent(string value) =>
        double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var percent) &&
        percent is >= 0 and <= 150
            ? percent
            : throw new InvalidDataException("Gain must be a percentage between 0 and 150.");

    private static double ParseValue(string value, double minimum, double maximum, string label) =>
        double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
        parsed >= minimum && parsed <= maximum
            ? parsed
            : throw new InvalidDataException($"{label} must be between {minimum} and {maximum}.");

    private static bool Is(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static object Success(string message, ControlState state) => new { ok = true, message, state };
    private static object Failure(string message) => new { ok = false, message };
}
