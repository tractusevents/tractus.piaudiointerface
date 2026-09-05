namespace PiUsbAudio.Control;

using System.Text.Json.Serialization;

public static class InputEventTypes
{
    public const ushort Key = 1;
    public const ushort Relative = 2;
}

public sealed class InputControlBinding
{
    public string DeviceId { get; set; } = string.Empty;
    public ushort EventType { get; set; }
    public ushort Code { get; set; }
    public int Direction { get; set; } = 1;
    public string DisplayName => LinuxInputEventNames.Describe(EventType, Code, Direction);

    public IReadOnlyList<string> Validate(string path, bool keyOnly)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(DeviceId))
            errors.Add($"{path}.deviceId must identify an input interface");
        else if (DeviceId.Length > 512 || DeviceId.Any(char.IsControl))
            errors.Add($"{path}.deviceId is invalid");
        if (keyOnly && EventType != InputEventTypes.Key)
            errors.Add($"{path} must use a key event");
        else if (!keyOnly && EventType is not InputEventTypes.Key and not InputEventTypes.Relative)
            errors.Add($"{path} must use a key or relative-axis event");
        if (EventType == InputEventTypes.Key && Direction != 1)
            errors.Add($"{path}.direction must be 1 for a key event");
        else if (Direction is not -1 and not 1)
            errors.Add($"{path}.direction must be -1 or 1");
        return errors;
    }
}

public sealed class KeyboardChannelControl
{
    public int Number { get; set; }
    public string? Action { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DefaultEnabled { get; set; }
    public InputControlBinding? Button { get; set; }
}

public static class KeyboardChannelActions
{
    public const string None = "none";
    public const string MuteWhileHeld = "muteWhileHeld";
    public const string UnmuteWhileHeld = "unmuteWhileHeld";

    public static bool IsValid(string? action) =>
        action is None or MuteWhileHeld or UnmuteWhileHeld;

    public static bool TryGetEnabled(string? action, bool pressed, out bool enabled)
    {
        switch (action)
        {
            case MuteWhileHeld:
                enabled = !pressed;
                return true;
            case UnmuteWhileHeld:
                enabled = pressed;
                return true;
            default:
                enabled = false;
                return false;
        }
    }
}

public sealed class KeyboardControlConfiguration
{
    public bool Enabled { get; set; }
    public List<string> DeviceIds { get; set; } = [];
    public int GainStepPercent { get; set; } = 2;
    public int MicrophoneGainDevice { get; set; } = 1;
    public List<KeyboardChannelControl> Channels { get; set; } =
    [
        new() { Number = 1, Action = KeyboardChannelActions.None },
        new() { Number = 2, Action = KeyboardChannelActions.UnmuteWhileHeld },
        new() { Number = 3, Action = KeyboardChannelActions.UnmuteWhileHeld },
        new() { Number = 4, Action = KeyboardChannelActions.UnmuteWhileHeld }
    ];
    public InputControlBinding? DialDecrease { get; set; }
    public InputControlBinding? DialIncrease { get; set; }
    public InputControlBinding? DialClick { get; set; }

    public void Normalize()
    {
        DeviceIds ??= [];
        DeviceIds = DeviceIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Channels ??= [];
        for (var number = 1; number <= 4; number++)
        {
            if (Channels.All(channel => channel.Number != number))
            {
                Channels.Add(new KeyboardChannelControl
                {
                    Number = number,
                    Action = number == 1
                        ? KeyboardChannelActions.None
                        : KeyboardChannelActions.UnmuteWhileHeld
                });
            }
        }
        Channels = Channels.OrderBy(channel => channel.Number).ToList();
        foreach (var channel in Channels)
        {
            // Migrate the former release/default state. A true default made the
            // old always-unmute press behavior a no-op; false was push-to-talk.
            channel.Action ??= channel.DefaultEnabled == false
                ? KeyboardChannelActions.UnmuteWhileHeld
                : KeyboardChannelActions.None;
            channel.Action = channel.Action.Trim();
            channel.DefaultEnabled = null;
        }
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (DeviceIds.Count > 8)
            errors.Add("keyboardControl.deviceIds supports at most eight input interfaces");
        if (DeviceIds.Any(value => value.Length > 512 || value.Any(char.IsControl)))
            errors.Add("keyboardControl.deviceIds contains an invalid identifier");
        if (GainStepPercent is < 1 or > 25)
            errors.Add("keyboardControl.gainStepPercent must be between 1 and 25");
        if (MicrophoneGainDevice is < 1 or > 4)
            errors.Add("keyboardControl.microphoneGainDevice must be between 1 and 4");
        if (Channels.Count != 4 || Channels.Select(channel => channel.Number).Distinct().Count() != 4 ||
            Channels.Any(channel => channel.Number is < 1 or > 4))
        {
            errors.Add("keyboardControl.channels must contain channels 1 through 4 exactly once");
        }
        foreach (var channel in Channels)
        {
            if (!KeyboardChannelActions.IsValid(channel.Action))
                errors.Add($"keyboardControl.channels[{channel.Number}].action is invalid");
            if (channel.Button is not null)
                errors.AddRange(channel.Button.Validate($"keyboardControl.channels[{channel.Number}].button", true));
        }
        if (DialDecrease is not null)
            errors.AddRange(DialDecrease.Validate("keyboardControl.dialDecrease", false));
        if (DialIncrease is not null)
            errors.AddRange(DialIncrease.Validate("keyboardControl.dialIncrease", false));
        if (DialClick is not null)
            errors.AddRange(DialClick.Validate("keyboardControl.dialClick", true));
        return errors;
    }
}

public sealed record LinuxInputDevice(
    string Id,
    string Path,
    string Name,
    string VendorId,
    string ProductId,
    bool SupportsKeys,
    bool SupportsRelativeAxes,
    bool StableId);

public sealed record LinuxInputEvent(
    DateTimeOffset Timestamp,
    string DeviceId,
    ushort EventType,
    ushort Code,
    int Value,
    string DisplayName);

public sealed record KeyboardControlStatus(
    bool Supported,
    bool Enabled,
    IReadOnlyList<string> ConnectedDeviceIds,
    string DialMode,
    string? LearningTarget,
    LinuxInputEvent? LastEvent,
    string Message,
    string? Error);

public static class KeyboardMappingTargets
{
    public const string DialDecrease = "dial-decrease";
    public const string DialIncrease = "dial-increase";
    public const string DialClick = "dial-click";

    public static bool IsValid(string target) =>
        target is DialDecrease or DialIncrease or DialClick or
        "channel-1" or "channel-2" or "channel-3" or "channel-4";

    public static bool RequiresKey(string target) =>
        target is DialClick or "channel-1" or "channel-2" or "channel-3" or "channel-4";
}

public static class LinuxInputEventNames
{
    public static string Describe(ushort type, ushort code, int direction = 1)
    {
        if (type == InputEventTypes.Key)
            return KeyName(code);
        if (type == InputEventTypes.Relative)
            return $"{RelativeName(code)} {(direction < 0 ? "−" : "+")}";
        return $"event {type}:{code}";
    }

    private static string KeyName(ushort code) => code switch
    {
        2 => "KEY_1", 3 => "KEY_2", 4 => "KEY_3", 5 => "KEY_4",
        16 => "KEY_Q", 17 => "KEY_W", 18 => "KEY_E", 19 => "KEY_R",
        28 => "KEY_ENTER", 30 => "KEY_A", 31 => "KEY_S", 32 => "KEY_D",
        33 => "KEY_F", 46 => "KEY_C", 48 => "KEY_B", 57 => "KEY_SPACE",
        103 => "KEY_UP", 105 => "KEY_LEFT", 106 => "KEY_RIGHT", 108 => "KEY_DOWN",
        113 => "KEY_MUTE", 114 => "KEY_VOLUMEDOWN", 115 => "KEY_VOLUMEUP",
        _ => $"KEY_{code}"
    };

    private static string RelativeName(ushort code) => code switch
    {
        0 => "REL_X", 1 => "REL_Y", 6 => "REL_HWHEEL", 8 => "REL_WHEEL",
        11 => "REL_WHEEL_HI_RES", 12 => "REL_HWHEEL_HI_RES",
        _ => $"REL_{code}"
    };
}
