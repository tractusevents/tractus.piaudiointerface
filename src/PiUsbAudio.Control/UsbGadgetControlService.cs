namespace PiUsbAudio.Control;

public sealed record UsbGadgetRestartResult(
    bool Success,
    bool Busy,
    string Message,
    GadgetDiagnostics Gadget);

public sealed class UsbGadgetControlService(
    CommandRunner commandRunner,
    AudioRouter router,
    ConfigStore configStore,
    ControlEventBus eventBus,
    GadgetDiagnosticsService diagnostics,
    SerialControlService serialControl,
    ILogger<UsbGadgetControlService> logger)
{
    private const string GadgetUnit = "pi-usb-audio-gadget.service";
    private readonly SemaphoreSlim restartLock = new(1, 1);

    public async Task<UsbGadgetRestartResult> RestartAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await restartLock.WaitAsync(0, cancellationToken))
        {
            return new UsbGadgetRestartResult(
                false,
                true,
                "A USB gadget restart is already in progress.",
                diagnostics.Read());
        }

        try
        {
            var systemctl = Environment.GetEnvironmentVariable("PI_USB_AUDIO_SYSTEMCTL") ??
                "/usr/bin/systemctl";
            CommandResult command;
            using (await serialControl.PauseAsync(cancellationToken))
            {
                command = await commandRunner.RunAsync(
                    systemctl,
                    ["--no-ask-password", "restart", GadgetUnit],
                    cancellationToken);
            }
            if (!command.Success)
            {
                var detail = FirstNonEmpty(command.StandardError, command.StandardOutput);
                var failureMessage = string.IsNullOrEmpty(detail)
                    ? $"Could not restart {GadgetUnit} (systemctl exit {command.ExitCode})."
                    : $"Could not restart {GadgetUnit}: {detail}";
                logger.LogWarning("USB gadget restart failed: {Message}", failureMessage);
                return new UsbGadgetRestartResult(false, false, failureMessage, diagnostics.Read());
            }

            // ALSA/PipeWire cards appear shortly after ConfigFS has rebound the
            // gadget. Reapply promptly instead of waiting for the periodic
            // reconciler, while leaving that reconciler as the recovery path.
            ApplyResult? routing = null;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                routing = await router.ApplyAsync(cancellationToken);
                if (routing.GadgetCount == 4)
                    break;
            }

            eventBus.Publish(
                "state",
                new ControlState(await configStore.LoadAsync(cancellationToken), routing));

            var successMessage = routing?.Success == true
                ? "USB names applied. The gadget reconnected and audio routing was restored."
                : "USB names applied and the gadget reconnected. Audio routing is still recovering automatically.";
            return new UsbGadgetRestartResult(true, false, successMessage, diagnostics.Read());
        }
        finally
        {
            restartLock.Release();
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        var value = values.Select(value => value.Trim()).FirstOrDefault(value => value.Length > 0) ??
            string.Empty;
        return value.Length <= 1000 ? value : value[..1000] + "…";
    }
}
