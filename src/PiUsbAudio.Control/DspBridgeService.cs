using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace PiUsbAudio.Control;

public sealed record DspApplyResult(bool Success, string Error)
{
    public static DspApplyResult Ok { get; } = new(true, string.Empty);
}

public sealed class DspBridgeService(
    ConfigStore configStore,
    ControlEventBus eventBus,
    ILogger<DspBridgeService> logger) : BackgroundService
{
    private const string SocketFileName = "tractus-audio-dsp.sock";
    private readonly string serverPath = GetServerPath();

    public Task<DspApplyResult> ApplyConfigurationAsync(
        RouterConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!File.Exists(serverPath))
                return Task.FromResult(new DspApplyResult(false, $"DSP socket {serverPath} is unavailable"));

            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            var payload = Encoding.ASCII.GetBytes(BuildSetCommand(configuration));
            socket.SendTo(payload, SocketFlags.None, new UnixDomainSocketEndPoint(serverPath));
            return Task.FromResult(DspApplyResult.Ok);
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            return Task.FromResult(new DspApplyResult(false, exception.Message));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var clientPath = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp",
            $"tractus-audio-meter-{Environment.ProcessId}.sock");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(clientPath))
                    File.Delete(clientPath);
                using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
                socket.Bind(new UnixDomainSocketEndPoint(clientPath));
                await SubscribeAsync(socket, stoppingToken);
                logger.LogInformation("DSP meter event stream subscribed on {SocketPath}", serverPath);

                var configuration = await configStore.LoadAsync(stoppingToken);
                var applyResult = await ApplyConfigurationAsync(configuration, stoppingToken);
                if (!applyResult.Success)
                    logger.LogWarning("Initial DSP configuration failed: {Error}", applyResult.Error);

                var buffer = new byte[2048];
                while (!stoppingToken.IsCancellationRequested)
                {
                    using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    receiveTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        var length = await socket.ReceiveAsync(buffer, SocketFlags.None, receiveTimeout.Token);
                        if (length <= 0)
                            continue;
                        var message = Encoding.ASCII.GetString(buffer, 0, length);
                        if (TryParseMeter(message, out var meters))
                            eventBus.Publish("meters", meters);
                        else if (message.StartsWith("ERROR", StringComparison.Ordinal))
                            logger.LogWarning("DSP rejected a control message: {Response}", message);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Re-subscribe periodically so a restarted DSP process resumes
                        // push events without requiring a router restart.
                        await SubscribeAsync(socket, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or SocketException)
            {
                logger.LogWarning("DSP event connection unavailable: {Message}", exception.Message);
                try
                {
                    await Task.Delay(1000, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(clientPath))
                        File.Delete(clientPath);
                }
                catch (IOException)
                {
                    // A stale client socket is removed on the next connection attempt.
                }
            }
        }
    }

    private async Task SubscribeAsync(Socket socket, CancellationToken cancellationToken)
    {
        var payload = "SUBSCRIBE"u8.ToArray();
        await socket.SendToAsync(
            payload,
            SocketFlags.None,
            new UnixDomainSocketEndPoint(serverPath),
            cancellationToken);
    }

    private static string BuildSetCommand(RouterConfiguration configuration)
    {
        var ducking = configuration.Ducking;
        var builder = new StringBuilder("SET");
        Append(builder, configuration.PhysicalPlaybackGain);
        Append(builder, ducking.Enabled ? 1 : 0);
        var triggerMask = (ducking.TriggerSources ?? [ducking.PriorityDevice])
            .Aggregate(0, (mask, source) => mask | (1 << source));
        Append(builder, triggerMask);
        Append(builder, ducking.ThresholdDbfs);
        Append(builder, ducking.DepthDb);
        Append(builder, ducking.AttackMilliseconds);
        Append(builder, ducking.HoldMilliseconds);
        Append(builder, ducking.ReleaseMilliseconds);
        foreach (var device in configuration.Devices.OrderBy(device => device.Number))
        {
            Append(builder, device.OutputEnabled ? 1 : 0);
            Append(builder, device.OutputGain);
            Append(builder, device.OutputSolo ? 1 : 0);
        }
        Append(builder, configuration.Sidetone.Enabled ? 1 : 0);
        Append(builder, configuration.Sidetone.Gain);
        Append(builder, configuration.NdiReceiver.Enabled ? 1 : 0);
        Append(builder, configuration.NdiReceiver.Gain);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, double value) =>
        builder.Append(' ').Append(value.ToString("0.########", CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, int value) =>
        builder.Append(' ').Append(value.ToString(CultureInfo.InvariantCulture));

    private static bool TryParseMeter(string message, out DspMeterState meters)
    {
        meters = default!;
        var words = message.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length != 18 || !string.Equals(words[0], "METER", StringComparison.Ordinal))
            return false;
        if (!long.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) ||
            !int.TryParse(words[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var active) ||
            !TryDouble(words[3], out var reduction))
            return false;

        var devices = new List<DspDeviceMeter>(4);
        for (var device = 0; device < 4; device++)
        {
            if (!TryDouble(words[4 + device * 2], out var peak) ||
                !TryDouble(words[5 + device * 2], out var rms))
                return false;
            devices.Add(new DspDeviceMeter(device + 1, peak, rms));
        }
        if (!TryDouble(words[12], out var sidetonePeak) ||
            !TryDouble(words[13], out var sidetoneRms) ||
            !TryDouble(words[14], out var ndiReceiverPeak) ||
            !TryDouble(words[15], out var ndiReceiverRms) ||
            !TryDouble(words[16], out var mixPeak) ||
            !TryDouble(words[17], out var mixRms))
            return false;

        meters = new DspMeterState(
            DateTimeOffset.UtcNow,
            sequence,
            active != 0,
            reduction,
            devices,
            new DspMixMeter(sidetonePeak, sidetoneRms),
            new DspMixMeter(ndiReceiverPeak, ndiReceiverRms),
            new DspMixMeter(mixPeak, mixRms));
        return true;
    }

    private static bool TryDouble(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static string GetServerPath()
    {
        var configured = Environment.GetEnvironmentVariable("TRACTUS_DSP_SOCKET");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/run/user/1000";
        return Path.Combine(runtimeDirectory, SocketFileName);
    }
}
