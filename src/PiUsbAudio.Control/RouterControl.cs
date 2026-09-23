namespace PiUsbAudio.Control;

public sealed record ControlState(RouterConfiguration Configuration, ApplyResult? Routing);

public sealed class RouterControl(
    ConfigStore configStore,
    AudioRouter router,
    DspBridgeService dspBridge,
    NdiBridgeService ndiBridge,
    ControlEventBus eventBus)
{
    public async Task<ControlState> GetStateAsync(CancellationToken cancellationToken = default) =>
        new(await configStore.LoadAsync(cancellationToken), router.LastResult);

    public Task<ControlState> SetMicrophoneEnabledAsync(
        int? number,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(configuration =>
        {
            foreach (var device in SelectDevices(configuration, number))
                device.InputEnabled = enabled;
        }, cancellationToken);

    public Task<ControlState> SetDeviceFriendlyNameAsync(
        int number,
        string friendlyName,
        CancellationToken cancellationToken = default) =>
        UpdateNdiAsync(configuration =>
            SelectDevice(configuration, number).FriendlyName = friendlyName.Trim(), cancellationToken);

    public Task<ControlState> SetNdiOutputEnabledAsync(
        int number,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(configuration =>
            SelectDevice(configuration, number).NdiOutputEnabled = enabled, cancellationToken);

    public Task<ControlState> SetMicrophoneEnabledStatesAsync(
        IReadOnlyDictionary<int, bool> enabledStates,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(configuration =>
        {
            foreach (var device in configuration.Devices)
            {
                if (enabledStates.TryGetValue(device.Number, out var enabled))
                    device.InputEnabled = enabled;
            }
        }, cancellationToken);

    public Task<ControlState> SetOutputEnabledAsync(
        int? number,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        UpdateDspAsync(configuration =>
        {
            foreach (var device in SelectDevices(configuration, number))
                device.OutputEnabled = enabled;
        }, cancellationToken);

    public Task<ControlState> SetOutputSoloAsync(
        int number,
        bool solo,
        bool exclusive,
        CancellationToken cancellationToken = default) =>
        UpdateDspAsync(configuration =>
        {
            if (exclusive)
            {
                foreach (var device in configuration.Devices)
                    device.OutputSolo = false;
            }
            SelectDevice(configuration, number).OutputSolo = solo;
        }, cancellationToken);

    public Task<ControlState> ClearOutputSoloAsync(CancellationToken cancellationToken = default) =>
        UpdateDspAsync(configuration =>
        {
            foreach (var device in configuration.Devices)
                device.OutputSolo = false;
        }, cancellationToken);

    public Task<ControlState> SetMicrophoneGainAsync(
        int number,
        double gain,
        CancellationToken cancellationToken = default) =>
        UpdateMicrophoneGainAsync(number, gain, cancellationToken);

    public Task<ControlState> SetOutputGainAsync(
        int number,
        double gain,
        CancellationToken cancellationToken = default) =>
        UpdateDspAsync(configuration => SelectDevice(configuration, number).OutputGain = gain, cancellationToken);

    public Task<ControlState> SetPhysicalOutputGainAsync(
        double gain,
        CancellationToken cancellationToken = default) =>
        UpdateDspAsync(configuration => configuration.PhysicalPlaybackGain = gain, cancellationToken);

    public Task<ControlState> SetSidetoneEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        UpdateDspAsync(configuration => configuration.Sidetone.Enabled = enabled, cancellationToken);

    public Task<ControlState> SetSidetoneGainAsync(
        double gain,
        CancellationToken cancellationToken = default) =>
        UpdateDspAsync(configuration => configuration.Sidetone.Gain = gain, cancellationToken);

    public Task<ControlState> SetDuckingEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        UpdateDspAsync(configuration => configuration.Ducking.Enabled = enabled, cancellationToken);

    public Task<ControlState> SetDuckingPriorityAsync(
        int number,
        CancellationToken cancellationToken = default) =>
        UpdateDspAsync(configuration => configuration.Ducking.SetSingleTrigger(number), cancellationToken);

    public Task<ControlState> SetDuckingTriggerAsync(
        int source,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        UpdateDspAsync(configuration => configuration.Ducking.SetTrigger(source, enabled), cancellationToken);

    public Task<ControlState> SetDuckingTriggersAsync(
        IReadOnlyCollection<int> sources,
        CancellationToken cancellationToken = default) =>
        UpdateDspAsync(configuration =>
        {
            configuration.Ducking.TriggerSources = sources.Distinct().Order().ToList();
            if (configuration.Ducking.TriggerSources.Count > 0)
                configuration.Ducking.PriorityDevice = configuration.Ducking.TriggerSources[0];
        }, cancellationToken);

    public Task<ControlState> SetDuckingThresholdAsync(
        double dbfs,
        CancellationToken cancellationToken = default) =>
        UpdateDspAsync(configuration => configuration.Ducking.ThresholdDbfs = dbfs, cancellationToken);

    public Task<ControlState> SetDuckingDepthAsync(
        double db,
        CancellationToken cancellationToken = default) =>
        UpdateDspAsync(configuration => configuration.Ducking.DepthDb = db, cancellationToken);

    public Task<ControlState> SetDuckingAttackAsync(
        double milliseconds,
        CancellationToken cancellationToken = default) =>
        UpdateDspAsync(configuration => configuration.Ducking.AttackMilliseconds = milliseconds, cancellationToken);

    public Task<ControlState> SetDuckingHoldAsync(
        double milliseconds,
        CancellationToken cancellationToken = default) =>
        UpdateDspAsync(configuration => configuration.Ducking.HoldMilliseconds = milliseconds, cancellationToken);

    public Task<ControlState> SetDuckingReleaseAsync(
        double milliseconds,
        CancellationToken cancellationToken = default) =>
        UpdateDspAsync(configuration => configuration.Ducking.ReleaseMilliseconds = milliseconds, cancellationToken);

    public Task<ControlState> SetNdiEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(configuration => configuration.NdiAudio.Enabled = enabled, cancellationToken);

    public Task<ControlState> SetNdiSourceNameAsync(
        string sourceName,
        CancellationToken cancellationToken = default) =>
        UpdateNdiAsync(configuration => configuration.NdiAudio.SourceName = sourceName.Trim(), cancellationToken);

    public Task<ControlState> SetNdiConfigurationAsync(
        NdiAudioConfiguration ndiAudio,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(configuration => configuration.NdiAudio = new NdiAudioConfiguration
        {
            Enabled = ndiAudio.Enabled,
            SourceName = ndiAudio.SourceName.Trim()
        }, cancellationToken);

    public Task<ControlState> SetNdiReceiverEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        SetNdiReceiverEnabledAsync(1, enabled, cancellationToken);

    public Task<ControlState> SetNdiReceiverEnabledAsync(
        int number,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        UpdateNdiAndDspAsync(configuration =>
            SelectNdiReceiver(configuration, number).Enabled = enabled, cancellationToken);

    public Task<ControlState> SetNdiReceiverSourceAsync(
        string sourceName,
        CancellationToken cancellationToken = default) =>
        SetNdiReceiverSourceAsync(1, sourceName, cancellationToken);

    public Task<ControlState> SetNdiReceiverSourceAsync(
        int number,
        string sourceName,
        CancellationToken cancellationToken = default) =>
        UpdateNdiAsync(configuration =>
            SelectNdiReceiver(configuration, number).SourceName = sourceName.Trim(), cancellationToken);

    public Task<ControlState> SetNdiReceiverGainAsync(
        double gain,
        CancellationToken cancellationToken = default) =>
        SetNdiReceiverGainAsync(1, gain, cancellationToken);

    public Task<ControlState> SetNdiReceiverGainAsync(
        int number,
        double gain,
        CancellationToken cancellationToken = default) =>
        UpdateDspAsync(configuration =>
            SelectNdiReceiver(configuration, number).Gain = gain, cancellationToken);

    public Task<ControlState> SetNdiReceiverConfigurationAsync(
        NdiReceiverConfiguration receiver,
        CancellationToken cancellationToken = default) =>
        UpdateNdiAndDspAsync(configuration => configuration.NdiReceivers[0] = new NdiReceiverConfiguration
        {
            Number = 1,
            Enabled = receiver.Enabled,
            SourceName = receiver.SourceName.Trim(),
            Gain = receiver.Gain
        }, cancellationToken);

    public Task<ControlState> SetNdiReceiverConfigurationAsync(
        int number,
        NdiReceiverConfiguration receiver,
        CancellationToken cancellationToken = default) =>
        UpdateNdiAndDspAsync(configuration =>
        {
            var index = configuration.NdiReceivers.FindIndex(value => value.Number == number);
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(number));
            configuration.NdiReceivers[index] = new NdiReceiverConfiguration
            {
                Number = number,
                Enabled = receiver.Enabled,
                SourceName = receiver.SourceName.Trim(),
                Gain = receiver.Gain
            };
        }, cancellationToken);

    public Task<ControlState> SetMappingsAsync(
        AudioDeviceMappings mappings,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(configuration =>
        {
            configuration.PhysicalCaptureNode = mappings.PhysicalCaptureNode;
            configuration.PhysicalCaptureChannel = mappings.PhysicalCaptureChannel;
            configuration.PhysicalPlaybackNode = mappings.PhysicalPlaybackNode;
            configuration.PhysicalPlaybackLeftChannel = mappings.PhysicalPlaybackLeftChannel;
            configuration.PhysicalPlaybackRightChannel = mappings.PhysicalPlaybackRightChannel;
        }, cancellationToken);

    private async Task<ControlState> UpdateAsync(
        Action<RouterConfiguration> update,
        CancellationToken cancellationToken)
    {
        var configuration = await configStore.UpdateAsync(update, cancellationToken);
        var result = await router.ApplyAsync(cancellationToken);
        var state = new ControlState(configuration, result);
        eventBus.Publish("state", state);
        return state;
    }

    private async Task<ControlState> UpdateDspAsync(
        Action<RouterConfiguration> update,
        CancellationToken cancellationToken)
    {
        var configuration = await configStore.UpdateAsync(update, cancellationToken);
        var dspResult = await dspBridge.ApplyConfigurationAsync(configuration, cancellationToken);
        var routing = router.LastResult;
        if (!dspResult.Success)
            routing = await router.ApplyAsync(cancellationToken);
        var state = new ControlState(configuration, routing);
        eventBus.Publish("state", state);
        return state;
    }

    private async Task<ControlState> UpdateMicrophoneGainAsync(
        int number,
        double gain,
        CancellationToken cancellationToken)
    {
        var configuration = await configStore.UpdateAsync(
            value => SelectDevice(value, number).InputGain = gain,
            cancellationToken);
        var applied = await router.ApplyMicrophoneGainAsync(number, gain, cancellationToken);
        var routing = applied ? router.LastResult : await router.ApplyAsync(cancellationToken);
        var state = new ControlState(configuration, routing);
        eventBus.Publish("state", state);
        return state;
    }

    private async Task<ControlState> UpdateMetadataAsync(
        Action<RouterConfiguration> update,
        CancellationToken cancellationToken)
    {
        var configuration = await configStore.UpdateAsync(update, cancellationToken);
        var state = new ControlState(configuration, router.LastResult);
        eventBus.Publish("state", state);
        return state;
    }

    private async Task<ControlState> UpdateNdiAsync(
        Action<RouterConfiguration> update,
        CancellationToken cancellationToken)
    {
        var configuration = await configStore.UpdateAsync(update, cancellationToken);
        var ndiResult = await ndiBridge.ApplyConfigurationAsync(configuration, cancellationToken);
        var routing = router.LastResult;
        if (!ndiResult.Success)
            routing = await router.ApplyAsync(cancellationToken);
        var state = new ControlState(configuration, routing);
        eventBus.Publish("state", state);
        return state;
    }

    private async Task<ControlState> UpdateNdiAndDspAsync(
        Action<RouterConfiguration> update,
        CancellationToken cancellationToken)
    {
        var configuration = await configStore.UpdateAsync(update, cancellationToken);
        var ndiResult = await ndiBridge.ApplyConfigurationAsync(configuration, cancellationToken);
        var dspResult = await dspBridge.ApplyConfigurationAsync(configuration, cancellationToken);
        var routing = router.LastResult;
        if (!ndiResult.Success || !dspResult.Success)
            routing = await router.ApplyAsync(cancellationToken);
        var state = new ControlState(configuration, routing);
        eventBus.Publish("state", state);
        return state;
    }

    private static IEnumerable<VirtualDeviceConfiguration> SelectDevices(
        RouterConfiguration configuration,
        int? number) =>
        number is null ? configuration.Devices : [SelectDevice(configuration, number.Value)];

    private static VirtualDeviceConfiguration SelectDevice(
        RouterConfiguration configuration,
        int number)
    {
        if (number is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(number), "Device number must be between 1 and 4.");
        return configuration.Devices.Single(device => device.Number == number);
    }

    private static NdiReceiverConfiguration SelectNdiReceiver(
        RouterConfiguration configuration,
        int number)
    {
        if (number is < 1 or > RouterConfiguration.NdiReceiverCount)
            throw new ArgumentOutOfRangeException(nameof(number),
                $"NDI receiver number must be between 1 and {RouterConfiguration.NdiReceiverCount}.");
        return configuration.NdiReceivers.Single(receiver => receiver.Number == number);
    }
}
