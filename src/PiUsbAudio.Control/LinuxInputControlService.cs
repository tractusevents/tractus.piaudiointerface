using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PiUsbAudio.Control;

public sealed class LinuxInputDeviceCatalog
{
    private readonly string inputRoot =
        Environment.GetEnvironmentVariable("PI_USB_AUDIO_INPUT_ROOT") ?? "/dev/input";
    private readonly string sysInputRoot =
        Environment.GetEnvironmentVariable("PI_USB_AUDIO_SYS_INPUT_ROOT") ?? "/sys/class/input";

    public IReadOnlyList<LinuxInputDevice> Discover()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(inputRoot))
            return [];

        var aliases = FindAliases();
        var devices = new List<LinuxInputDevice>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(inputRoot, "event*"))
            {
                var eventName = Path.GetFileName(path);
                var capabilityText = ReadText(Path.Combine(
                    sysInputRoot, eventName, "device", "capabilities", "ev"));
                var supportsKeys = HasEventCapability(capabilityText, InputEventTypes.Key);
                var supportsRelativeAxes = HasEventCapability(capabilityText, InputEventTypes.Relative);
                if (!supportsKeys && !supportsRelativeAxes)
                    continue;

                var fullPath = Path.GetFullPath(path);
                var alias = aliases.TryGetValue(fullPath, out var candidates)
                    ? candidates.OrderBy(AliasScore).ThenBy(value => value, StringComparer.Ordinal).FirstOrDefault()
                    : null;
                var deviceRoot = Path.Combine(sysInputRoot, eventName, "device");
                devices.Add(new LinuxInputDevice(
                    alias ?? fullPath,
                    fullPath,
                    ReadText(Path.Combine(deviceRoot, "name")) ?? eventName,
                    ReadText(Path.Combine(deviceRoot, "id", "vendor")) ?? string.Empty,
                    ReadText(Path.Combine(deviceRoot, "id", "product")) ?? string.Empty,
                    supportsKeys,
                    supportsRelativeAxes,
                    alias is not null));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return devices
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Id, StringComparer.Ordinal)
            .ToList();
    }

    private Dictionary<string, List<string>> FindAliases()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var directoryName in new[] { "by-id", "by-path" })
        {
            var directory = Path.Combine(inputRoot, directoryName);
            if (!Directory.Exists(directory))
                continue;
            try
            {
                foreach (var alias in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (!Path.GetFileName(alias).Contains("-event", StringComparison.Ordinal))
                        continue;
                    var target = new FileInfo(alias).ResolveLinkTarget(returnFinalTarget: true);
                    if (target is null)
                        continue;
                    var fullTarget = Path.GetFullPath(target.FullName);
                    if (!result.TryGetValue(fullTarget, out var values))
                    {
                        values = [];
                        result[fullTarget] = values;
                    }
                    values.Add(Path.GetFullPath(alias));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Direct event paths remain available as an explicitly unstable fallback.
            }
        }
        return result;
    }

    private static int AliasScore(string path)
    {
        var byId = path.Contains("/by-id/", StringComparison.Ordinal);
        var keyboard = path.EndsWith("-event-kbd", StringComparison.Ordinal);
        if (byId && keyboard)
            return 0;
        if (keyboard)
            return 1;
        if (byId)
            return 2;
        return 3;
    }

    private static bool HasEventCapability(string? capabilityText, int eventType)
    {
        var finalWord = capabilityText?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return ulong.TryParse(
            finalWord,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out var capabilities) &&
            (capabilities & (1UL << eventType)) != 0;
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public sealed class LinuxInputControlService(
    ConfigStore configStore,
    RouterControl control,
    ControlEventBus eventBus,
    LinuxInputDeviceCatalog catalog,
    ILogger<LinuxInputControlService> logger) : BackgroundService
{
    private const short PollIn = 0x0001;
    private const short PollError = 0x0008;
    private const short PollHangUp = 0x0010;
    private const short PollInvalid = 0x0020;
    private const int InterruptedSystemCall = 4;
    private readonly SemaphoreSlim reloadSignal = new(0, 1);
    private readonly SemaphoreSlim actionGate = new(1, 1);
    private readonly object statusGate = new();
    private KeyboardControlStatus status = new(
        OperatingSystem.IsLinux(), false, [], "microphone", null, null,
        "Keyboard control is disabled.", null);

    public KeyboardControlStatus Status => Volatile.Read(ref status);

    public IReadOnlyList<LinuxInputDevice> DiscoverDevices() => catalog.Discover();

    public void NotifyConfigurationChanged()
    {
        if (reloadSignal.CurrentCount == 0)
            reloadSignal.Release();
    }

    public bool TryBeginLearning(string target, out string error)
    {
        if (!KeyboardMappingTargets.IsValid(target))
        {
            error = "Unknown mapping target.";
            return false;
        }
        var currentStatus = Status;
        if (!currentStatus.Enabled || currentStatus.ConnectedDeviceIds.Count == 0)
        {
            error = "Enable the controls, select the input interface, and save before learning a mapping.";
            return false;
        }
        lock (statusGate)
        {
            UpdateStatus(status with
            {
                LearningTarget = target,
                Message = $"Waiting for {LearningLabel(target)}…",
                Error = null
            });
        }
        error = string.Empty;
        return true;
    }

    public void CancelLearning()
    {
        lock (statusGate)
        {
            UpdateStatus(status with
            {
                LearningTarget = null,
                Message = "Mapping capture cancelled."
            });
        }
    }

    public async Task<KeyboardControlConfiguration> SetConfigurationAsync(
        KeyboardControlConfiguration value,
        CancellationToken cancellationToken = default)
    {
        value.Normalize();
        var configuration = await configStore.UpdateAsync(
            current => current.KeyboardControl = value,
            cancellationToken);
        eventBus.Publish("control-configuration", configuration.KeyboardControl);
        NotifyConfigurationChanged();
        return configuration.KeyboardControl;
    }

    public async Task<KeyboardControlConfiguration> ClearMappingAsync(
        string target,
        CancellationToken cancellationToken = default)
    {
        if (!KeyboardMappingTargets.IsValid(target))
            throw new ArgumentException("Unknown mapping target.", nameof(target));
        var configuration = await configStore.UpdateAsync(current =>
        {
            SetBinding(current.KeyboardControl, target, null);
        }, cancellationToken);
        eventBus.Publish("control-configuration", configuration.KeyboardControl);
        NotifyConfigurationChanged();
        return configuration.KeyboardControl;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            UpdateStatus(status with
            {
                Supported = false,
                Message = "Linux input controls are not supported on this platform."
            });
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            KeyboardControlConfiguration configuration;
            try
            {
                configuration = (await configStore.LoadAsync(stoppingToken)).KeyboardControl;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                UpdateStatus(status with { Error = exception.Message, Message = "Could not read keyboard control configuration." });
                await WaitForReloadOrDelayAsync(stoppingToken);
                continue;
            }

            if (!configuration.Enabled)
            {
                UpdateStatus(status with
                {
                    Enabled = false,
                    ConnectedDeviceIds = [],
                    DialMode = "microphone",
                    LearningTarget = null,
                    Message = "Keyboard control is disabled.",
                    Error = null
                });
                await WaitForReloadOrDelayAsync(stoppingToken);
                continue;
            }

            try
            {
                await ApplyReleasedStatesAsync(configuration, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not apply keyboard-control released states");
                UpdateStatus(status with { Error = exception.Message, Message = "Could not apply released button states." });
            }

            var selectedDevices = catalog.Discover()
                .Where(device => configuration.DeviceIds.Contains(device.Id, StringComparer.Ordinal))
                .ToList();
            if (configuration.DeviceIds.Count == 0 || selectedDevices.Count != configuration.DeviceIds.Count)
            {
                var missingCount = configuration.DeviceIds.Count - selectedDevices.Count;
                UpdateStatus(status with
                {
                    Enabled = true,
                    ConnectedDeviceIds = [],
                    Message = configuration.DeviceIds.Count == 0
                        ? "Select at least one input interface."
                        : $"Waiting for {missingCount} selected input interface{(missingCount == 1 ? string.Empty : "s")}.",
                    Error = null
                });
                await WaitForReloadOrDelayAsync(stoppingToken);
                continue;
            }

            using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var readers = selectedDevices
                .Select(device => ReadDeviceAsync(device, configuration, sessionCancellation.Token))
                .ToList();
            UpdateStatus(status with
            {
                Enabled = true,
                ConnectedDeviceIds = selectedDevices.Select(device => device.Id).ToList(),
                Message = $"Listening to {selectedDevices.Count} input interface{(selectedDevices.Count == 1 ? string.Empty : "s")}." ,
                Error = null
            });

            var retryAfterSession = false;
            try
            {
                var readerEnded = Task.WhenAny(readers);
                var reloadRequested = reloadSignal.WaitAsync(sessionCancellation.Token);
                var completed = await Task.WhenAny(readerEnded, reloadRequested);
                retryAfterSession = ReferenceEquals(completed, readerEnded);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal service shutdown.
            }
            finally
            {
                sessionCancellation.Cancel();
                try
                {
                    await Task.WhenAll(readers);
                }
                catch (OperationCanceledException)
                {
                    // Normal session replacement or shutdown.
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    logger.LogDebug("Input control interface disconnected: {Message}", exception.Message);
                    UpdateStatus(status with
                    {
                        ConnectedDeviceIds = [],
                        Message = "The selected input interface is unavailable.",
                        Error = exception is UnauthorizedAccessException
                            ? "Permission denied. Reboot after adding the service user to the input group."
                            : exception.Message
                    });
                    retryAfterSession = true;
                }
                try
                {
                    if (!stoppingToken.IsCancellationRequested)
                        await ApplyReleasedStatesAsync(configuration, stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Could not restore keyboard-control released states");
                }
            }
            if (retryAfterSession && Status.ConnectedDeviceIds.Count > 0)
            {
                UpdateStatus(status with
                {
                    ConnectedDeviceIds = [],
                    Message = "The selected input interface disconnected. Waiting for it to return."
                });
            }
            if (retryAfterSession && !stoppingToken.IsCancellationRequested)
                await WaitForReloadOrDelayAsync(stoppingToken);
        }
    }

    private async Task ReadDeviceAsync(
        LinuxInputDevice device,
        KeyboardControlConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        await using var stream = new FileStream(
            device.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            FileOptions.None);
        var pollDescriptor = new PollDescriptor
        {
            FileDescriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt32(),
            Events = PollIn
        };
        var eventHeaderSize = IntPtr.Size == 8 ? 16 : 8;
        var eventSize = eventHeaderSize + 8;
        var buffer = new byte[eventSize];

        while (!cancellationToken.IsCancellationRequested)
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
                return;

            var offset = 0;
            while (offset < eventSize)
            {
                var read = stream.Read(buffer, offset, eventSize - offset);
                if (read == 0)
                    return;
                offset += read;
            }
            var type = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(eventHeaderSize, 2));
            var code = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(eventHeaderSize + 2, 2));
            var value = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(eventHeaderSize + 4, 4));
            if (type is not InputEventTypes.Key and not InputEventTypes.Relative)
                continue;
            await HandleEventAsync(device.Id, type, code, value, configuration, cancellationToken);
        }
    }

    private async Task HandleEventAsync(
        string deviceId,
        ushort type,
        ushort code,
        int value,
        KeyboardControlConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var direction = Math.Sign(value);
        var inputEvent = new LinuxInputEvent(
            DateTimeOffset.UtcNow,
            deviceId,
            type,
            code,
            value,
            LinuxInputEventNames.Describe(type, code, direction == 0 ? 1 : direction));
        string? learningTarget;
        lock (statusGate)
        {
            learningTarget = status.LearningTarget;
            UpdateStatus(status with { LastEvent = inputEvent });
        }

        if (learningTarget is not null && IsLearnable(learningTarget, type, value))
        {
            var claimed = false;
            lock (statusGate)
            {
                if (string.Equals(status.LearningTarget, learningTarget, StringComparison.Ordinal))
                {
                    claimed = true;
                    UpdateStatus(status with
                    {
                        LearningTarget = null,
                        Message = "Saving learned mapping…"
                    });
                }
            }
            if (claimed)
            {
                await LearnBindingAsync(learningTarget, inputEvent, cancellationToken);
                return;
            }
        }

        await actionGate.WaitAsync(cancellationToken);
        try
        {
            if (type == InputEventTypes.Key && value is 0 or 1)
            {
                foreach (var channel in configuration.Channels)
                {
                    if (!Matches(channel.Button, deviceId, type, code, 1))
                        continue;
                    if (!KeyboardChannelActions.TryGetEnabled(channel.Action, value == 1, out var enabled))
                        continue;
                    await control.SetMicrophoneEnabledAsync(
                        channel.Number,
                        enabled,
                        cancellationToken);
                }
                if (value == 1 && Matches(configuration.DialClick, deviceId, type, code, 1))
                {
                    lock (statusGate)
                    {
                        var mode = status.DialMode == "microphone" ? "output" : "microphone";
                        UpdateStatus(status with
                        {
                            DialMode = mode,
                            Message = $"Dial now controls {mode} volume."
                        });
                    }
                }
            }

            if (IsDialActivation(configuration.DialDecrease, deviceId, type, code, value))
                await AdjustGainAsync(-configuration.GainStepPercent, configuration, cancellationToken);
            if (IsDialActivation(configuration.DialIncrease, deviceId, type, code, value))
                await AdjustGainAsync(configuration.GainStepPercent, configuration, cancellationToken);
        }
        finally
        {
            actionGate.Release();
        }
    }

    private async Task LearnBindingAsync(
        string target,
        LinuxInputEvent inputEvent,
        CancellationToken cancellationToken)
    {
        var binding = new InputControlBinding
        {
            DeviceId = inputEvent.DeviceId,
            EventType = inputEvent.EventType,
            Code = inputEvent.Code,
            Direction = inputEvent.EventType == InputEventTypes.Relative ? Math.Sign(inputEvent.Value) : 1
        };
        var configuration = await configStore.UpdateAsync(current =>
        {
            SetBinding(current.KeyboardControl, target, binding);
        }, cancellationToken);
        lock (statusGate)
        {
            UpdateStatus(status with
            {
                LearningTarget = null,
                Message = $"Mapped {LearningLabel(target)} to {binding.DisplayName}.",
                Error = null
            });
        }
        eventBus.Publish("control-configuration", configuration.KeyboardControl);
        NotifyConfigurationChanged();
    }

    private async Task AdjustGainAsync(
        int changePercent,
        KeyboardControlConfiguration keyboard,
        CancellationToken cancellationToken)
    {
        var state = await control.GetStateAsync(cancellationToken);
        var outputMode = Status.DialMode == "output";
        var currentGain = outputMode
            ? state.Configuration.PhysicalPlaybackGain
            : state.Configuration.Devices.Single(device => device.Number == keyboard.MicrophoneGainDevice).InputGain;
        var nextPercent = Math.Clamp(currentGain * 100.0 + changePercent, 0.0, 150.0);
        if (outputMode)
            await control.SetPhysicalOutputGainAsync(nextPercent / 100.0, cancellationToken);
        else
            await control.SetMicrophoneGainAsync(keyboard.MicrophoneGainDevice, nextPercent / 100.0, cancellationToken);
        lock (statusGate)
        {
            UpdateStatus(status with
            {
                Message = outputMode
                    ? $"Physical output {nextPercent:0}%"
                    : $"Microphone {keyboard.MicrophoneGainDevice} send {nextPercent:0}%"
            });
        }
    }

    private async Task ApplyReleasedStatesAsync(
        KeyboardControlConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var states = new Dictionary<int, bool>();
        foreach (var channel in configuration.Channels)
        {
            if (KeyboardChannelActions.TryGetEnabled(channel.Action, pressed: false, out var enabled))
                states[channel.Number] = enabled;
        }
        if (states.Count == 0)
            return;
        var current = await control.GetStateAsync(cancellationToken);
        if (current.Configuration.Devices
            .Where(device => states.ContainsKey(device.Number))
            .All(device => device.InputEnabled == states[device.Number]))
            return;
        await control.SetMicrophoneEnabledStatesAsync(states, cancellationToken);
    }

    private async Task WaitForReloadOrDelayAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(1000);
        try
        {
            await reloadSignal.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Periodically retry discovery and devices which were unavailable.
        }
    }

    private void UpdateStatus(KeyboardControlStatus value)
    {
        Volatile.Write(ref status, value);
        eventBus.Publish("controls", value);
    }

    private static bool IsLearnable(string target, ushort type, int value) =>
        KeyboardMappingTargets.RequiresKey(target)
            ? type == InputEventTypes.Key && value == 1
            : (type == InputEventTypes.Key && value == 1) ||
                (type == InputEventTypes.Relative && value != 0);

    private static bool Matches(
        InputControlBinding? binding,
        string deviceId,
        ushort type,
        ushort code,
        int direction) =>
        binding is not null &&
        string.Equals(binding.DeviceId, deviceId, StringComparison.Ordinal) &&
        binding.EventType == type && binding.Code == code && binding.Direction == direction;

    private static bool IsDialActivation(
        InputControlBinding? binding,
        string deviceId,
        ushort type,
        ushort code,
        int value)
    {
        if (binding is null || !string.Equals(binding.DeviceId, deviceId, StringComparison.Ordinal) ||
            binding.EventType != type || binding.Code != code)
            return false;
        return type == InputEventTypes.Key
            ? value == 1
            : value != 0 && Math.Sign(value) == binding.Direction;
    }

    private static void SetBinding(
        KeyboardControlConfiguration configuration,
        string target,
        InputControlBinding? binding)
    {
        if (target.StartsWith("channel-", StringComparison.Ordinal) &&
            int.TryParse(target.AsSpan("channel-".Length), out var number))
        {
            configuration.Channels.Single(channel => channel.Number == number).Button = binding;
            return;
        }
        if (target == KeyboardMappingTargets.DialDecrease)
            configuration.DialDecrease = binding;
        else if (target == KeyboardMappingTargets.DialIncrease)
            configuration.DialIncrease = binding;
        else if (target == KeyboardMappingTargets.DialClick)
            configuration.DialClick = binding;
        else
            throw new ArgumentException("Unknown mapping target.", nameof(target));
    }

    private static string LearningLabel(string target) => target switch
    {
        "channel-1" => "channel 1 button",
        "channel-2" => "channel 2 button",
        "channel-3" => "channel 3 button",
        "channel-4" => "channel 4 button",
        KeyboardMappingTargets.DialDecrease => "dial decrease",
        KeyboardMappingTargets.DialIncrease => "dial increase",
        KeyboardMappingTargets.DialClick => "dial click",
        _ => target
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int Poll(ref PollDescriptor fileDescriptors, nuint descriptorCount, int timeoutMilliseconds);
}
