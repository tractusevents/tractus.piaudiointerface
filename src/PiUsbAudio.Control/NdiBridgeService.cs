using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace PiUsbAudio.Control;

public sealed record NdiApplyResult(bool Success, string Error)
{
    public static NdiApplyResult Ok { get; } = new(true, string.Empty);
}

public sealed class NdiBridgeService(
    ConfigStore configStore,
    ControlEventBus eventBus,
    ILogger<NdiBridgeService> logger) : BackgroundService
{
    private const string SocketFileName = "tractus-ndi-audio.sock";
    private readonly string serverPath = GetServerPath();

    public Task<NdiApplyResult> ApplyConfigurationAsync(
        RouterConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!File.Exists(serverPath))
                return Task.FromResult(new NdiApplyResult(false, $"NDI socket {serverPath} is unavailable"));

            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            var senderEnabled = configuration.NdiAudio.Enabled ? "1" : "0";
            var receiverEnabled = configuration.NdiReceiver.Enabled ? "1" : "0";
            var payload = Encoding.UTF8.GetBytes(
                $"SET2 {senderEnabled} {receiverEnabled}\n" +
                $"{configuration.NdiAudio.SourceName}\n{configuration.NdiReceiver.SourceName}");
            socket.SendTo(payload, SocketFlags.None, new UnixDomainSocketEndPoint(serverPath));
            return Task.FromResult(NdiApplyResult.Ok);
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            return Task.FromResult(new NdiApplyResult(false, exception.Message));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var clientPath = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp",
            $"tractus-ndi-status-{Environment.ProcessId}.sock");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(clientPath))
                    File.Delete(clientPath);
                using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
                socket.Bind(new UnixDomainSocketEndPoint(clientPath));
                await SubscribeAsync(socket, stoppingToken);
                logger.LogInformation("NDI status event stream subscribed on {SocketPath}", serverPath);

                var configuration = await configStore.LoadAsync(stoppingToken);
                var applyResult = await ApplyConfigurationAsync(configuration, stoppingToken);
                if (!applyResult.Success)
                    logger.LogWarning("Initial NDI configuration failed: {Error}", applyResult.Error);

                var buffer = new byte[32768];
                while (!stoppingToken.IsCancellationRequested)
                {
                    using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    receiveTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        var length = await socket.ReceiveAsync(buffer, SocketFlags.None, receiveTimeout.Token);
                        if (length <= 0)
                            continue;
                        var message = Encoding.UTF8.GetString(buffer, 0, length);
                        if (TryParseStatus(message, out var status))
                            eventBus.Publish("ndi", status);
                        else if (TryParseSources(message, out var sources))
                            eventBus.Publish("ndi-sources", sources);
                        else if (message.StartsWith("ERROR", StringComparison.Ordinal))
                            logger.LogWarning("NDI audio service rejected a control message: {Response}", message);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        await SubscribeAsync(socket, stoppingToken);
                        var current = await configStore.LoadAsync(stoppingToken);
                        await ApplyConfigurationAsync(current, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or SocketException)
            {
                logger.LogWarning("NDI event connection unavailable: {Message}", exception.Message);
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

    private static bool TryParseStatus(string message, out NdiAudioStatus status)
    {
        status = default!;
        var words = message.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length != 17 || !string.Equals(words[0], "NDI", StringComparison.Ordinal))
            return false;
        if (!ulong.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) ||
            !int.TryParse(words[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var enabled) ||
            !int.TryParse(words[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var online) ||
            !int.TryParse(words[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var connections) ||
            !TryDouble(words[5], out var peak) ||
            !TryDouble(words[6], out var rms) ||
            !TryDouble(words[7], out var queue) ||
            !ulong.TryParse(words[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var underruns) ||
            !ulong.TryParse(words[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var overruns) ||
            !int.TryParse(words[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out var receiverEnabled) ||
            !int.TryParse(words[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out var receiverConnected) ||
            !TryDouble(words[12], out var receiverPeak) ||
            !TryDouble(words[13], out var receiverRms) ||
            !TryDouble(words[14], out var receiverQueue) ||
            !ulong.TryParse(words[15], NumberStyles.Integer, CultureInfo.InvariantCulture, out var receiverUnderruns) ||
            !ulong.TryParse(words[16], NumberStyles.Integer, CultureInfo.InvariantCulture, out var receiverOverruns))
            return false;

        status = new NdiAudioStatus(
            DateTimeOffset.UtcNow,
            sequence,
            enabled != 0,
            online != 0,
            connections,
            peak,
            rms,
            queue,
            underruns,
            overruns,
            receiverEnabled != 0,
            receiverConnected != 0,
            receiverPeak,
            receiverRms,
            receiverQueue,
            receiverUnderruns,
            receiverOverruns);
        return true;
    }

    private static bool TryParseSources(string message, out NdiSourceList sources)
    {
        sources = default!;
        var lines = message.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var header = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (header.Length != 2 || !string.Equals(header[0], "SOURCES", StringComparison.Ordinal) ||
            !ulong.TryParse(header[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence))
            return false;

        sources = new NdiSourceList(
            DateTimeOffset.UtcNow,
            sequence,
            lines.Skip(1)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray());
        return true;
    }

    private static bool TryDouble(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static string GetServerPath()
    {
        var configured = Environment.GetEnvironmentVariable("TRACTUS_NDI_SOCKET");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/run/user/1000";
        return Path.Combine(runtimeDirectory, SocketFileName);
    }
}
