namespace PiUsbAudio.Control;

public sealed class RouterConfiguration
{
    public string GadgetMatch { get; set; } = "UAC2 Gadget";
    public string PhysicalCaptureNode { get; set; } = string.Empty;
    public string PhysicalPlaybackNode { get; set; } = string.Empty;
    public string PhysicalCaptureChannel { get; set; } = "FL";
    public string PhysicalPlaybackLeftChannel { get; set; } = "FL";
    public string PhysicalPlaybackRightChannel { get; set; } = "FR";
    public double PhysicalPlaybackGain { get; set; } = 1.0;
    public SidetoneConfiguration Sidetone { get; set; } = new();
    public DuckingConfiguration Ducking { get; set; } = new();
    public NdiAudioConfiguration NdiAudio { get; set; } = new();
    public NdiReceiverConfiguration NdiReceiver { get; set; } = new();
    public int ReconcileIntervalSeconds { get; set; } = 10;
    public List<VirtualDeviceConfiguration> Devices { get; set; } =
    [
        new() { Number = 1 },
        new() { Number = 2 },
        new() { Number = 3 },
        new() { Number = 4 }
    ];

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(GadgetMatch))
            errors.Add("gadgetMatch must not be empty");
        if (ReconcileIntervalSeconds is < 1 or > 60)
            errors.Add("reconcileIntervalSeconds must be between 1 and 60");
        if (!double.IsFinite(PhysicalPlaybackGain) || PhysicalPlaybackGain is < 0 or > 1.5)
            errors.Add("physicalPlaybackGain must be between 0.0 and 1.5");
        errors.AddRange(Sidetone.Validate());
        errors.AddRange(Ducking.Validate());
        errors.AddRange(NdiAudio.Validate());
        errors.AddRange(NdiReceiver.Validate());
        if (Devices.Count != 4)
            errors.Add("exactly four virtual devices must be configured");
        if (Devices.Select(device => device.Number).Distinct().Count() != Devices.Count)
            errors.Add("virtual device numbers must be unique");
        foreach (var device in Devices)
        {
            if (device.Number is < 1 or > 4)
                errors.Add($"virtual device number {device.Number} is outside 1-4");
            if (!double.IsFinite(device.OutputGain) || device.OutputGain is < 0 or > 1.5)
                errors.Add($"device {device.Number} outputGain must be between 0.0 and 1.5");
            if (!double.IsFinite(device.InputGain) || device.InputGain is < 0 or > 1.5)
                errors.Add($"device {device.Number} inputGain must be between 0.0 and 1.5");
        }
        return errors;
    }

    public void Normalize()
    {
        Ducking.TriggerSources ??= [];
        if (Ducking.TriggerSources.Count == 0)
            Ducking.TriggerSources.Add(Ducking.PriorityDevice);
        Ducking.TriggerSources = Ducking.TriggerSources.Distinct().Order().ToList();
        Ducking.PriorityDevice = Ducking.TriggerSources[0];
    }
}

public sealed class SidetoneConfiguration
{
    public bool Enabled { get; set; }
    public double Gain { get; set; } = 1.0;

    public IReadOnlyList<string> Validate() =>
        !double.IsFinite(Gain) || Gain is < 0 or > 1.5
            ? ["sidetone.gain must be between 0.0 and 1.5"]
            : [];
}

public sealed class NdiAudioConfiguration
{
    public bool Enabled { get; set; }
    public string SourceName { get; set; } = "Tractus USB Audio Microphone";

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(SourceName))
            errors.Add("ndiAudio.sourceName must not be empty");
        else if (System.Text.Encoding.UTF8.GetByteCount(SourceName) > 127)
            errors.Add("ndiAudio.sourceName must be at most 127 UTF-8 bytes");
        else if (SourceName.Any(character => char.IsControl(character)))
            errors.Add("ndiAudio.sourceName must not contain control characters");
        return errors;
    }
}

public sealed class NdiReceiverConfiguration
{
    public bool Enabled { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public double Gain { get; set; } = 1.0;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!double.IsFinite(Gain) || Gain is < 0 or > 1.5)
            errors.Add("ndiReceiver.gain must be between 0.0 and 1.5");
        if (Enabled && string.IsNullOrWhiteSpace(SourceName))
            errors.Add("ndiReceiver.sourceName must be selected when the receiver is enabled");
        else if (System.Text.Encoding.UTF8.GetByteCount(SourceName) > 511)
            errors.Add("ndiReceiver.sourceName must be at most 511 UTF-8 bytes");
        else if (SourceName.Any(character => char.IsControl(character)))
            errors.Add("ndiReceiver.sourceName must not contain control characters");
        return errors;
    }
}

public sealed class VirtualDeviceConfiguration
{
    public int Number { get; set; }
    public bool InputEnabled { get; set; } = true;
    public double InputGain { get; set; } = 1.0;
    public bool OutputEnabled { get; set; } = true;
    public double OutputGain { get; set; } = 1.0;
    public bool OutputSolo { get; set; }
}

public sealed class DuckingConfiguration
{
    public bool Enabled { get; set; }
    // Retained for API/config compatibility. It mirrors the first trigger and
    // old configurations are migrated from it when triggerSources is absent.
    public int PriorityDevice { get; set; } = 1;
    public List<int>? TriggerSources { get; set; } = [];
    public double ThresholdDbfs { get; set; } = -30.0;
    public double DepthDb { get; set; } = 18.0;
    public double AttackMilliseconds { get; set; } = 10.0;
    public double HoldMilliseconds { get; set; } = 150.0;
    public double ReleaseMilliseconds { get; set; } = 400.0;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (PriorityDevice is < 0 or > 4)
            errors.Add("ducking.priorityDevice must be 0 (self) or between 1 and 4");
        if (TriggerSources is null || TriggerSources.Count == 0)
            errors.Add("ducking.triggerSources must contain at least one source");
        else
        {
            if (TriggerSources.Any(source => source is < 0 or > 4))
                errors.Add("ducking.triggerSources entries must be 0 (self) or between 1 and 4");
            if (TriggerSources.Distinct().Count() != TriggerSources.Count)
                errors.Add("ducking.triggerSources entries must be unique");
        }
        if (ThresholdDbfs is < -90 or > 0)
            errors.Add("ducking.thresholdDbfs must be between -90 and 0");
        if (DepthDb is < 0 or > 60)
            errors.Add("ducking.depthDb must be between 0 and 60");
        if (AttackMilliseconds is < 1 or > 2000)
            errors.Add("ducking.attackMilliseconds must be between 1 and 2000");
        if (HoldMilliseconds is < 0 or > 5000)
            errors.Add("ducking.holdMilliseconds must be between 0 and 5000");
        if (ReleaseMilliseconds is < 1 or > 10000)
            errors.Add("ducking.releaseMilliseconds must be between 1 and 10000");
        return errors;
    }

    public void SetSingleTrigger(int source)
    {
        TriggerSources = [source];
        PriorityDevice = source;
    }

    public void SetTrigger(int source, bool enabled)
    {
        TriggerSources ??= [];
        if (!enabled && TriggerSources.Count == 1 && TriggerSources.Contains(source))
            throw new InvalidDataException("At least one ducking trigger must remain selected.");
        if (enabled && !TriggerSources.Contains(source))
            TriggerSources.Add(source);
        else if (!enabled)
            TriggerSources.Remove(source);
        TriggerSources.Sort();
        if (TriggerSources.Count > 0)
            PriorityDevice = TriggerSources[0];
    }

    public bool IsTrigger(int source) => TriggerSources?.Contains(source) ?? PriorityDevice == source;
}

public sealed class DuckingTriggerSelection
{
    public List<int> Sources { get; set; } = [];
}

public sealed class AudioDeviceMappings
{
    public string PhysicalCaptureNode { get; set; } = string.Empty;
    public string PhysicalCaptureChannel { get; set; } = "FL";
    public string PhysicalPlaybackNode { get; set; } = string.Empty;
    public string PhysicalPlaybackLeftChannel { get; set; } = "FL";
    public string PhysicalPlaybackRightChannel { get; set; } = "FR";

    public static AudioDeviceMappings FromConfiguration(RouterConfiguration configuration) => new()
    {
        PhysicalCaptureNode = configuration.PhysicalCaptureNode,
        PhysicalCaptureChannel = configuration.PhysicalCaptureChannel,
        PhysicalPlaybackNode = configuration.PhysicalPlaybackNode,
        PhysicalPlaybackLeftChannel = configuration.PhysicalPlaybackLeftChannel,
        PhysicalPlaybackRightChannel = configuration.PhysicalPlaybackRightChannel
    };
}
