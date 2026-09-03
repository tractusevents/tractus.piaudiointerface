namespace PiUsbAudio.Control;

public sealed record ApplyResult(
    DateTimeOffset Timestamp,
    int GadgetCount,
    int LinksCreated,
    int LinksRemoved,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0;
}

public sealed class AudioRouter(
    PipeWireGraph graph,
    ConfigStore configStore,
    DspBridgeService? dspBridge = null,
    NdiBridgeService? ndiBridge = null)
{
    private readonly SemaphoreSlim applyLock = new(1, 1);
    private string? appliedGainFingerprint;
    public ApplyResult? LastResult { get; private set; }

    public async Task<ApplyResult> ApplyAsync(CancellationToken cancellationToken = default)
    {
        await applyLock.WaitAsync(cancellationToken);
        try
        {
            var configuration = await configStore.LoadAsync(cancellationToken);
            var validationErrors = configuration.Validate();
            if (validationErrors.Count > 0)
                return SetLast(new ApplyResult(DateTimeOffset.UtcNow, 0, 0, 0, [], validationErrors));

            PipeWireSnapshot snapshot;
            try
            {
                snapshot = await graph.GetSnapshotAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                return SetLast(new ApplyResult(
                    DateTimeOffset.UtcNow, 0, 0, 0, [], [$"PipeWire is unavailable: {exception.Message}"]));
            }

            var warnings = new List<string>();
            var errors = new List<string>();
            if (ndiBridge is not null)
            {
                var ndiResult = await ndiBridge.ApplyConfigurationAsync(configuration, cancellationToken);
                if (!ndiResult.Success)
                {
                    var message = $"Could not configure NDI audio service: {ndiResult.Error}";
                    if (configuration.NdiAudio.Enabled || configuration.NdiReceiver.Enabled)
                        errors.Add(message);
                    else
                        warnings.Add(message);
                }
            }
            var gadgets = snapshot.FindGadgets(configuration.GadgetMatch);
            if (gadgets.Count != 4)
            {
                errors.Add($"Expected four duplex gadget cards matching '{configuration.GadgetMatch}', found {gadgets.Count}.");
                return SetLast(new ApplyResult(DateTimeOffset.UtcNow, gadgets.Count, 0, 0, warnings, errors));
            }

            var physicalCapture = snapshot.FindNode(configuration.PhysicalCaptureNode, "Audio/Source");
            var physicalPlayback = snapshot.FindNode(configuration.PhysicalPlaybackNode, "Audio/Sink");
            var dsp = snapshot.FindNodeByName("tractus_audio_dsp");
            var ndi = snapshot.FindNodeByName("tractus_ndi_audio");
            var manageInputs = !string.IsNullOrWhiteSpace(configuration.PhysicalCaptureNode) && physicalCapture is not null;
            var manageOutputs = !string.IsNullOrWhiteSpace(configuration.PhysicalPlaybackNode) &&
                physicalPlayback is not null && dsp is not null;

            if (string.IsNullOrWhiteSpace(configuration.PhysicalCaptureNode))
                warnings.Add("No physical capture node is selected; microphone links were left unchanged.");
            else if (physicalCapture is null)
                warnings.Add($"Physical capture node '{configuration.PhysicalCaptureNode}' is not currently available.");
            if (string.IsNullOrWhiteSpace(configuration.PhysicalPlaybackNode))
                warnings.Add("No physical playback node is selected; output links were left unchanged.");
            else if (physicalPlayback is null)
                warnings.Add($"Physical playback node '{configuration.PhysicalPlaybackNode}' is not currently available.");
            else if (dsp is null)
                errors.Add("The Tractus real-time DSP mixer is not currently available.");

            var desiredLinks = new HashSet<(int OutputPort, int InputPort)>();
            var managedInputPorts = new HashSet<int>();
            var managedOutputPorts = new HashSet<int>();

            PipeWirePort? physicalCapturePort = null;
            PipeWirePort? physicalLeftPort = null;
            PipeWirePort? physicalRightPort = null;
            PipeWirePort? dspOutputLeft = null;
            PipeWirePort? dspOutputRight = null;
            PipeWirePort? dspSidetoneInput = null;
            PipeWirePort? dspNdiLeftInput = null;
            PipeWirePort? dspNdiRightInput = null;
            var dspInputs = new Dictionary<(int Device, string Channel), PipeWirePort>();
            if (manageInputs)
            {
                physicalCapturePort = SelectPort(
                    snapshot.PortsFor(physicalCapture!, "out"),
                    configuration.PhysicalCaptureChannel);
                if (physicalCapturePort is null)
                {
                    errors.Add($"Capture channel '{configuration.PhysicalCaptureChannel}' was not found on {physicalCapture!.Description}.");
                    manageInputs = false;
                }
            }

            if (ndi is not null)
            {
                var ndiInput = SelectPort(snapshot.PortsFor(ndi, "in"), "input_MONO");
                if (ndiInput is null)
                {
                    if (configuration.NdiAudio.Enabled)
                        errors.Add("The Tractus NDI sender does not expose its mono input port.");
                }
                else
                {
                    managedInputPorts.Add(ndiInput.Id);
                    if (configuration.NdiAudio.Enabled)
                    {
                        if (manageInputs)
                            desiredLinks.Add((physicalCapturePort!.Id, ndiInput.Id));
                        else
                            warnings.Add("NDI audio is enabled but no available physical microphone channel is selected.");
                    }
                }
            }
            else if (configuration.NdiAudio.Enabled)
            {
                errors.Add("The Tractus NDI audio sender is not currently available.");
            }
            if (manageOutputs)
            {
                var playbackPorts = snapshot.PortsFor(physicalPlayback!, "in");
                physicalLeftPort = SelectPort(playbackPorts, configuration.PhysicalPlaybackLeftChannel);
                physicalRightPort = SelectPort(playbackPorts, configuration.PhysicalPlaybackRightChannel);
                var dspInputPorts = snapshot.PortsFor(dsp!, "in");
                var dspOutputPorts = snapshot.PortsFor(dsp!, "out");
                dspOutputLeft = SelectPort(dspOutputPorts, "output_FL");
                dspOutputRight = SelectPort(dspOutputPorts, "output_FR");
                dspSidetoneInput = SelectPort(dspInputPorts, "input_sidetone_MONO");
                dspNdiLeftInput = SelectPort(dspInputPorts, "input_ndi_FL");
                dspNdiRightInput = SelectPort(dspInputPorts, "input_ndi_FR");
                foreach (var device in configuration.Devices)
                {
                    var left = SelectPort(dspInputPorts, $"input_{device.Number}_FL");
                    var right = SelectPort(dspInputPorts, $"input_{device.Number}_FR");
                    if (left is not null)
                        dspInputs[(device.Number, "FL")] = left;
                    if (right is not null)
                        dspInputs[(device.Number, "FR")] = right;
                }
                if (physicalLeftPort is null || physicalRightPort is null)
                {
                    errors.Add($"Playback channels '{configuration.PhysicalPlaybackLeftChannel}'/'{configuration.PhysicalPlaybackRightChannel}' were not found on {physicalPlayback!.Description}.");
                    manageOutputs = false;
                }
                else if (dspOutputLeft is null || dspOutputRight is null ||
                    dspSidetoneInput is null || dspNdiLeftInput is null ||
                    dspNdiRightInput is null || dspInputs.Count != 8)
                {
                    errors.Add("The Tractus DSP mixer does not expose the expected USB, sidetone, NDI, and stereo output ports.");
                    manageOutputs = false;
                }
            }

            var gainFingerprint = BuildGainFingerprint(
                configuration, gadgets, physicalPlayback, manageInputs, manageOutputs);
            var applyGains = !string.Equals(
                appliedGainFingerprint, gainFingerprint, StringComparison.Ordinal);
            var gainUpdateFailed = false;
            if (manageOutputs && applyGains)
            {
                var masterVolumeResult = await graph.SetVolumeAsync(
                    physicalPlayback!.Id,
                    1.0,
                    1.5,
                    cancellationToken);
                if (!masterVolumeResult.Success)
                {
                    gainUpdateFailed = true;
                    errors.Add($"Could not normalize physical output gain: {masterVolumeResult.StandardError.Trim()}");
                }
            }

            if (manageOutputs && dspBridge is not null)
            {
                var dspResult = await dspBridge.ApplyConfigurationAsync(configuration, cancellationToken);
                if (!dspResult.Success)
                    errors.Add($"Could not configure real-time DSP: {dspResult.Error}");
            }

            foreach (var device in configuration.Devices.OrderBy(device => device.Number))
            {
                var gadget = gadgets[device.Number - 1];
                var gadgetInput = snapshot.PortsFor(gadget.InputSink, "in").FirstOrDefault();
                var gadgetOutputs = snapshot.PortsFor(gadget.OutputSource, "out");
                var gadgetLeft = SelectPort(gadgetOutputs, "FL") ?? gadgetOutputs.FirstOrDefault();
                var gadgetRight = SelectPort(gadgetOutputs, "FR") ?? gadgetOutputs.Skip(1).FirstOrDefault();

                if (manageInputs)
                {
                    if (gadgetInput is null)
                        errors.Add($"Device {device.Number} has no PipeWire microphone input port.");
                    else
                    {
                        managedInputPorts.Add(gadgetInput.Id);
                        if (device.InputEnabled)
                            desiredLinks.Add((physicalCapturePort!.Id, gadgetInput.Id));
                    }

                    if (applyGains)
                    {
                        var inputVolumeResult = await graph.SetVolumeAsync(
                            gadget.InputSink.Id,
                            device.InputGain,
                            1.5,
                            cancellationToken);
                        if (!inputVolumeResult.Success)
                        {
                            gainUpdateFailed = true;
                            errors.Add($"Could not set device {device.Number} microphone gain: {inputVolumeResult.StandardError.Trim()}");
                        }
                    }
                }

                if (manageOutputs)
                {
                    if (gadgetLeft is null || gadgetRight is null)
                        errors.Add($"Device {device.Number} does not expose two PipeWire output ports.");
                    else
                    {
                        managedOutputPorts.Add(gadgetLeft.Id);
                        managedOutputPorts.Add(gadgetRight.Id);
                        desiredLinks.Add((gadgetLeft.Id, dspInputs[(device.Number, "FL")].Id));
                        desiredLinks.Add((gadgetRight.Id, dspInputs[(device.Number, "FR")].Id));
                    }

                    if (applyGains)
                    {
                        var volumeResult = await graph.SetVolumeAsync(
                            gadget.OutputSource.Id,
                            1.0,
                            1.5,
                            cancellationToken);
                        if (!volumeResult.Success)
                        {
                            gainUpdateFailed = true;
                            errors.Add($"Could not normalize device {device.Number} DSP input gain: {volumeResult.StandardError.Trim()}");
                        }
                    }
                }
            }


            if (manageOutputs)
            {
                if (manageInputs)
                {
                    managedInputPorts.Add(dspSidetoneInput!.Id);
                    desiredLinks.Add((physicalCapturePort!.Id, dspSidetoneInput.Id));
                }
                else if (configuration.Sidetone.Enabled ||
                    (configuration.Ducking.Enabled && configuration.Ducking.IsTrigger(0)))
                {
                    warnings.Add("Sidetone/self-ducking needs an available physical microphone channel.");
                }

                if (ndi is not null)
                {
                    var ndiOutputs = snapshot.PortsFor(ndi, "out");
                    var ndiLeft = SelectPort(ndiOutputs, "output_FL");
                    var ndiRight = SelectPort(ndiOutputs, "output_FR");
                    if (ndiLeft is null || ndiRight is null)
                    {
                        if (configuration.NdiReceiver.Enabled)
                            errors.Add("The Tractus NDI receiver does not expose its stereo output ports.");
                    }
                    else
                    {
                        managedOutputPorts.Add(ndiLeft.Id);
                        managedOutputPorts.Add(ndiRight.Id);
                        desiredLinks.Add((ndiLeft.Id, dspNdiLeftInput!.Id));
                        desiredLinks.Add((ndiRight.Id, dspNdiRightInput!.Id));
                    }
                }
                else if (configuration.NdiReceiver.Enabled)
                {
                    errors.Add("The Tractus NDI audio receiver is not currently available.");
                }

                managedOutputPorts.Add(dspOutputLeft!.Id);
                managedOutputPorts.Add(dspOutputRight!.Id);
                desiredLinks.Add((dspOutputLeft.Id, physicalLeftPort!.Id));
                desiredLinks.Add((dspOutputRight.Id, physicalRightPort!.Id));
            }

            if (applyGains && !gainUpdateFailed)
                appliedGainFingerprint = gainFingerprint;

            var created = 0;
            var removed = 0;
            var existingLinks = snapshot.Links
                .Select(link => (link.OutputPortId, link.InputPortId))
                .ToHashSet();

            foreach (var link in snapshot.Links)
            {
                var edge = (link.OutputPortId, link.InputPortId);
                var isManaged = managedInputPorts.Contains(link.InputPortId) ||
                    managedOutputPorts.Contains(link.OutputPortId);
                if (!isManaged || desiredLinks.Contains(edge))
                    continue;

                var result = await graph.RemoveLinkAsync(
                    link.OutputPortId,
                    link.InputPortId,
                    cancellationToken);
                if (result.Success)
                    removed++;
                else
                    errors.Add($"Could not remove link {link.OutputPortId}->{link.InputPortId}: {result.StandardError.Trim()}");
            }

            foreach (var edge in desiredLinks.Where(edge => !existingLinks.Contains(edge)))
            {
                var result = await graph.CreateLinkAsync(edge.OutputPort, edge.InputPort, cancellationToken);
                if (result.Success)
                    created++;
                else
                    errors.Add($"Could not create link {edge.OutputPort}->{edge.InputPort}: {result.StandardError.Trim()}");
            }

            return SetLast(new ApplyResult(
                DateTimeOffset.UtcNow, gadgets.Count, created, removed, warnings, errors));
        }
        finally
        {
            applyLock.Release();
        }
    }

    private ApplyResult SetLast(ApplyResult result)
    {
        LastResult = result;
        return result;
    }

    private static string BuildGainFingerprint(
        RouterConfiguration configuration,
        IReadOnlyList<GadgetPair> gadgets,
        PipeWireNode? physicalPlayback,
        bool manageInputs,
        bool manageOutputs)
    {
        var values = new List<string>
        {
            manageInputs ? "inputs:on" : "inputs:off",
            manageOutputs ? $"outputs:{physicalPlayback!.Id}:unity" : "outputs:off"
        };
        foreach (var device in configuration.Devices.OrderBy(device => device.Number))
        {
            var gadget = gadgets[device.Number - 1];
            if (manageInputs)
                values.Add($"mic:{gadget.InputSink.Id}:{device.InputGain:R}");
            if (manageOutputs)
                values.Add($"output:{gadget.OutputSource.Id}:unity");
        }
        return string.Join('|', values);
    }

    private static PipeWirePort? SelectPort(IEnumerable<PipeWirePort> ports, string selector)
    {
        var candidates = ports.ToList();
        var exact = candidates.FirstOrDefault(port =>
            string.Equals(port.Channel, selector, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(port.Name, selector, StringComparison.OrdinalIgnoreCase));
        return exact ?? candidates.FirstOrDefault(port =>
            port.Name.Contains(selector, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class RouterReconciler(
    AudioRouter router,
    ConfigStore configStore,
    ControlEventBus eventBus,
    ILogger<RouterReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await router.ApplyAsync(stoppingToken);
                eventBus.Publish(
                    "state",
                    new ControlState(await configStore.LoadAsync(stoppingToken), result));
                if (!result.Success)
                    logger.LogWarning("Audio route reconcile failed: {Errors}", string.Join("; ", result.Errors));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unexpected audio route reconcile error");
            }

            var delay = 2;
            try
            {
                delay = Math.Clamp((await configStore.LoadAsync(stoppingToken)).ReconcileIntervalSeconds, 1, 60);
            }
            catch (Exception)
            {
                // Retry a malformed or temporarily unavailable file promptly.
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
