namespace PiUsbAudio.Control;

public sealed record KeyboardLedStatus(bool Enabled, bool Connected, string? Color, string Message, string? Error);

// Independent worker: USB enumeration/writes never delay a microphone press.
// Read the latest router configuration, rather than queued state events that
// may have been superseded by a web/API/serial mute.
public sealed class KeyboardLedService(
    ConfigStore configStore,
    LinuxInputDeviceCatalog catalog,
    ControlEventBus eventBus,
    ILogger<KeyboardLedService> logger) : BackgroundService
{
    private sealed class Connection(MiniKeyboardDevice device, MiniKeyboardHid keyboard) : IDisposable
    {
        public MiniKeyboardDevice Device { get; } = device;
        public MiniKeyboardHid Keyboard { get; } = keyboard;
        public (int Layer, bool Red)? LastColor { get; set; }
        public void Dispose() => Keyboard.Dispose();
    }

    private KeyboardLedStatus status = new(false, false, null, "Keyboard LED feedback is disabled.", null);
    public KeyboardLedStatus Status => Volatile.Read(ref status);

    public static bool ShouldShowRed(RouterConfiguration configuration) =>
        configuration.Devices.Any(device => device.Number is >= 1 and <= 4 && device.InputEnabled);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connections = new Dictionary<string, Connection>(StringComparer.Ordinal);
        long nextDiscovery = 0;
        string? selection = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = 100;
                try
                {
                    var configuration = await configStore.LoadAsync(stoppingToken);
                    var keyboard = configuration.KeyboardControl;
                    if (!keyboard.Enabled || !keyboard.LedFeedbackEnabled)
                    {
                        CloseAll(connections);
                        nextDiscovery = 0;
                        Update(new(false, false, null, "Keyboard LED feedback is disabled.", null));
                    }
                    else if (!OperatingSystem.IsLinux())
                    {
                        Update(new(true, false, null, "Keyboard LEDs require Linux hidraw.", null));
                    }
                    else
                    {
                        var selected = string.Join("\n", keyboard.DeviceIds);
                        if (selection != selected || Environment.TickCount64 >= nextDiscovery)
                        {
                            selection = selected;
                            nextDiscovery = Environment.TickCount64 + 1000;
                            var usbPaths = catalog.Discover()
                                .Where(input => keyboard.DeviceIds.Contains(input.Id, StringComparer.Ordinal))
                                .Select(input => MiniKeyboardHid.FindUsbParent(
                                    $"/sys/class/input/{Path.GetFileName(input.Path)}/device"))
                                .Where(path => path is not null).ToHashSet(StringComparer.Ordinal);
                            var devices = MiniKeyboardHid.Enumerate()
                                .Where(device => usbPaths.Contains(device.UsbPath)).ToList();
                            foreach (var path in connections.Keys.Where(path =>
                                !devices.Any(device => device == connections[path].Device)).ToArray())
                            {
                                connections[path].Dispose();
                                connections.Remove(path);
                            }
                            foreach (var device in devices)
                                if (!connections.ContainsKey(device.Path))
                                    connections.Add(device.Path, new(device, new MiniKeyboardHid(device)));
                        }
                        if (connections.Count == 0)
                        {
                            Update(new(true, false, null, "Waiting for a selected, supported mini-keyboard.", null));
                        }
                        else
                        {
                            var red = ShouldShowRed(configuration);
                            var desired = (keyboard.LedLayer, red);
                            foreach (var connection in connections.Values)
                            {
                                if (connection.LastColor == desired)
                                    continue;
                                connection.Keyboard.SetColor(keyboard.LedLayer, red, commit: false);
                                connection.LastColor = desired;
                            }
                            Update(new(true, true, red ? "red" : "green",
                                red ? "Red: one or more microphone channels are unmuted."
                                    : "Green: all four microphone channels are muted.", null));
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    if (Status.Error != exception.Message)
                        logger.LogWarning(exception, "Keyboard LED feedback unavailable");
                    CloseAll(connections);
                    nextDiscovery = 0;
                    delay = 1000;
                    Update(new(true, false, null, "Keyboard LED feedback unavailable.", exception.Message));
                }
                await Task.Delay(delay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { CloseAll(connections); }
    }

    private static void CloseAll(Dictionary<string, Connection> connections)
    {
        foreach (var connection in connections.Values)
            connection.Dispose();
        connections.Clear();
    }

    private void Update(KeyboardLedStatus value)
    {
        if (value == Status)
            return;
        Volatile.Write(ref status, value);
        eventBus.Publish("keyboard-led", value);
    }
}
