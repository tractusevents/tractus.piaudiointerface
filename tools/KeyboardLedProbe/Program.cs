using PiUsbAudio.Control;
using var interrupted = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    interrupted.Cancel();
};

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: KeyboardLedProbe CONFIG_PATH HIDRAW_PATH");
    return 2;
}
var store = new ConfigStore(args[0]);
var initial = await store.LoadAsync();
if (!initial.KeyboardControl.LedFeedbackEnabled)
    throw new InvalidOperationException("Start with normal LED feedback enabled so it can be restored.");
var selectedUsb = new LinuxInputDeviceCatalog().Discover()
    .Where(input => initial.KeyboardControl.DeviceIds.Contains(input.Id))
    .Select(input => MiniKeyboardHid.FindUsbParent($"/sys/class/input/{Path.GetFileName(input.Path)}/device"))
    .Where(path => path is not null).ToHashSet();
var device = MiniKeyboardHid.Enumerate().Single(device =>
    device.Path == args[1] && device.VendorId == 0x514c && device.ProductId == 0x8851 &&
    selectedUsb.Contains(device.UsbPath));
MiniKeyboardHid? hid = null;
var exitCode = 0;
try
{
    // Change only the feedback setting, without the input-service reload that
    // the general configuration API performs. Its in-memory latches stay intact.
    await store.UpdateAsync(configuration => configuration.KeyboardControl.LedFeedbackEnabled = false);
    for (var attempt = 0; attempt < 30; attempt++)
    {
        try
        {
            hid = new MiniKeyboardHid(device);
            break;
        }
        catch (IOException) when (attempt < 29) { await Task.Delay(100); }
    }
    if (hid is null)
        throw new IOException("Could not acquire the keyboard configuration interface.");
    var baseline = await store.LoadAsync();
    hid.SetColor(baseline.KeyboardControl.LedLayer, KeyboardLedService.ShouldShowRed(baseline), false);
    Console.WriteLine("Lighting paused; input service and latches remain running.");
    Console.WriteLine("WARNING: related-model packets are unverified on this keyboard and may require a USB replug to recover.");
    Console.WriteLine("Commands: model, pattern0, pattern1, pattern2, reverse0, reverse1, reverse2, legacy-white, legacy-green, baseline, quit.");
    Console.WriteLine("Automatic restoration after 180 seconds without a command.");
    while (true)
    {
        var input = Console.In.ReadLineAsync(interrupted.Token).AsTask();
        if (await Task.WhenAny(input, Task.Delay(TimeSpan.FromSeconds(180))) != input)
        {
            Console.WriteLine("Probe timeout; restoring feedback.");
            break;
        }
        var command = await input;
        if (command is null or "quit")
            break;
        if (command == "model")
        {
            var report = hid.QueryModel();
            Console.WriteLine(report.Length == 0 ? "No model response received; identity remains unverified."
                : $"Model response: {Convert.ToHexString(report)}");
        }
        else if (command == "baseline")
        {
            var current = await store.LoadAsync();
            hid.SetColor(current.KeyboardControl.LedLayer, KeyboardLedService.ShouldShowRed(current), false);
            Console.WriteLine("Restored baseline color.");
        }
        else if (command is "pattern0" or "pattern1" or "pattern2" or "reverse0" or "reverse1" or "reverse2")
        {
            var layer = command[^1] - '0';
            var reverse = command.StartsWith("reverse", StringComparison.Ordinal);
            var report = MiniKeyboardHid.PerKeyProbeReport(layer, reverse);
            hid.SetPerKeyProbePattern(layer, reverse);
            Console.WriteLine($"Sent {command}: {Convert.ToHexString(report)} (no commit)");
        }
        else if (command is "legacy-white" or "legacy-green")
        {
            var current = await store.LoadAsync();
            var green = command == "legacy-green";
            var report = MiniKeyboardHid.LegacyLedProbeReport(current.KeyboardControl.LedLayer, green);
            hid.SetLegacyLedProbe(current.KeyboardControl.LedLayer, green);
            Console.WriteLine($"Sent {command}: {Convert.ToHexString(report)} (no commit)");
        }
        else
            Console.WriteLine("Unrecognized command; nothing sent.");
    }
}
catch (OperationCanceledException) when (interrupted.IsCancellationRequested)
{
    Console.WriteLine("Probe interrupted; restoring feedback.");
}
catch (IOException exception)
{
    exitCode = 1;
    Console.Error.WriteLine($"Probe stopped: {exception.Message}");
}
finally
{
    try
    {
        if (hid is not null)
        {
            var current = await store.LoadAsync();
            hid.SetColor(current.KeyboardControl.LedLayer, KeyboardLedService.ShouldShowRed(current), false);
            Console.WriteLine("Baseline color command sent; visual confirmation is still required.");
        }
    }
    catch (IOException exception)
    {
        exitCode = 1;
        Console.Error.WriteLine($"Could not restore baseline color: {exception.Message}");
        Console.Error.WriteLine("Do not continue experimental packets until normal LED writes work again.");
    }
    finally
    {
        hid?.Dispose();
        await store.UpdateAsync(configuration =>
            configuration.KeyboardControl.LedFeedbackEnabled = initial.KeyboardControl.LedFeedbackEnabled);
        Console.WriteLine("Automatic feedback setting re-enabled; this does not confirm the keyboard accepted its color.");
    }
}
return exitCode;
