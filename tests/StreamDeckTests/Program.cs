using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using PiUsbAudio.Control;

static void Check(bool condition, string label)
{
    if (!condition)
        throw new Exception(label);
}

static byte[] Report(params int[] keys) =>
    new byte[] { 1 }.Concat(Enumerable.Range(1, 6).Select(key => (byte)(keys.Contains(key) ? 1 : 0))).ToArray();

static async Task Until(Func<Task<bool>> ready, string label)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try
    {
        while (!await ready())
            await Task.Delay(20, timeout.Token);
    }
    catch (OperationCanceledException) { throw new Exception($"Timed out: {label}"); }
}

static async Task<ControlState> PressPanic(FakeConnection connection, ControlEventBus events)
{
    await using var subscription = events.Subscribe();
    connection.Reports.Add(Report(6));
    connection.Reports.Add(Report(6));
    connection.Reports.Add(Report());
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await foreach (var item in subscription.Reader.ReadAllAsync(timeout.Token))
        if (item.Name == "state" && item.Data is ControlState state)
            return state;
    throw new Exception("PANIC did not complete its routing update.");
}

var buttons = new StreamDeckButtons();
Check(buttons.Handle(Report(1)).SequenceEqual([1]), "first press toggles mic 1");
Check(buttons.Handle(Report(1)).Count == 0, "held key does not toggle twice");
Check(buttons.Handle(Report(1, 3, 5)).SequenceEqual([3]), "snapshots toggle only newly pressed mic keys");
Check(buttons.Handle([1, 0]).Count == 0, "truncated report ignored");
Check(buttons.Handle([2, 0, 0, 0, 0, 0, 0]).Count == 0, "wrong report ID ignored");
Check(buttons.Handle([1, 0, 0, 0, 0, 0, 255]).Count == 0, "invalid key bytes ignored atomically");
Check(buttons.Handle(Report(1, 3)).Count == 0, "invalid reports do not reset held keys");
Check(buttons.Handle(Report()).Count == 0, "release never toggles");
Check(buttons.Handle(Report(1)).SequenceEqual([1]), "second tap toggles back");
Check(buttons.Handle(Report(5)).Count == 0, "key 5 has no action");
Check(buttons.Handle(Report(6)).SequenceEqual([6]), "key 6 invokes panic");
Check(buttons.Handle(Report(6)).Count == 0, "holding panic does not repeat");
Check(buttons.Handle(Report()).Count == 0, "panic release has no action");
Check(buttons.Handle(Report(1, 2, 3, 4, 6)).SequenceEqual([6]), "panic suppresses simultaneous mic toggles");
buttons = new StreamDeckButtons();
Check(buttons.Handle(Report(2), prime: true).Count == 0, "connection baseline never toggles");
Check(buttons.Handle(Report(2)).Count == 0, "initially held key ignored");
buttons.Handle(Report());
Check(buttons.Handle(Report(2, 4)).SequenceEqual([2, 4]), "independent simultaneous presses after reconnect");

foreach (var state in new[] { "LIVE", "MUTED", "OFFLINE", "ERROR", "UNASSIGNED" })
{
    var image = StreamDeckImages.Render(new StreamDeckKeyDisplay(1, "To Teams/Zoom é", state));
    Check(image.Length == 19254 && image[0] == 'B' && image[1] == 'M', "valid BMP size/signature");
    Check(BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(18)) == 80 &&
        BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(22)) == 80 &&
        BinaryPrimitives.ReadInt16LittleEndian(image.AsSpan(28)) == 24, "Mini BMP dimensions and depth");
    var reports = StreamDeckHid.ImageReports(3, image).ToArray();
    Check(reports.Length == 20 && reports.All(report => report.Length == 1024), "full-sized image chunks");
    Check(reports.All(report => report[0] == 2 && report[1] == 1 && report[5] == 4), "one-based image key");
    Check(reports.Select(report => (int)report[2]).SequenceEqual(Enumerable.Range(0, 20)), "image chunk ordering");
    Check(reports.Count(report => report[4] == 1) == 1 && reports[^1][4] == 1, "only final image chunk commits");
    Check(reports.SelectMany(report => report.Skip(16)).Take(image.Length).SequenceEqual(image), "lossless image transmission");
}
Check(StreamDeckImages.Render(new StreamDeckKeyDisplay(6, "PANIC", "MUTE ALL")).Length == 19254,
    "panic label renders as a full Mini bitmap");
// Asymmetric corner markers verify rotation, reflection, bottom-up rows and BGR.
var rgb = new byte[80 * 80 * 3];
new byte[] { 255, 0, 0 }.CopyTo(rgb, 0);
new byte[] { 0, 255, 0 }.CopyTo(rgb, 79 * 3);
new byte[] { 0, 0, 255 }.CopyTo(rgb, 79 * 80 * 3);
var oriented = StreamDeckImages.EncodeBitmap(rgb);
Check(oriented.AsSpan(54, 3).SequenceEqual(new byte[] { 0, 255, 0 }), "native BMP starts at upright top right");
Check(oriented.AsSpan(54 + 79 * 80 * 3, 3).SequenceEqual(new byte[] { 0, 0, 255 }), "native BMP bottom left is upright top left");
Check(oriented.AsSpan(54 + (79 * 80 + 79) * 3, 3).SequenceEqual(new byte[] { 255, 0, 0 }), "native BMP bottom right is upright bottom left");
Check(new StreamDeckConfiguration { Brightness = -1 }.Validate().Count == 1, "negative brightness rejected");
Check(new StreamDeckConfiguration { Brightness = 101 }.Validate().Count == 1, "excess brightness rejected");

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var temporary = Path.Combine(Path.GetTempPath(), $"stream-deck-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(temporary);
var oldPath = Environment.GetEnvironmentVariable("PATH");
var oldLog = Environment.GetEnvironmentVariable("PI_USB_AUDIO_WPCTL_LOG");
var oldDsp = Environment.GetEnvironmentVariable("TRACTUS_DSP_SOCKET");
var oldNdi = Environment.GetEnvironmentVariable("TRACTUS_NDI_SOCKET");
Environment.SetEnvironmentVariable("PATH", Path.Combine(root, "tests/fake-bin") + ":" + oldPath);
Environment.SetEnvironmentVariable("PI_USB_AUDIO_WPCTL_LOG", Path.Combine(temporary, "wpctl.log"));
Environment.SetEnvironmentVariable("TRACTUS_DSP_SOCKET", Path.Combine(temporary, "no-dsp.sock"));
Environment.SetEnvironmentVariable("TRACTUS_NDI_SOCKET", Path.Combine(temporary, "no-ndi.sock"));
try
{
    var hidLibrary = Path.Combine(temporary, "fake-hidapi.so");
    var compile = new ProcessStartInfo("cc")
    {
        ArgumentList =
        {
            "-std=c11", "-Wall", "-Wextra", "-Werror", "-shared", "-fPIC",
            Path.Combine(root, "tests/StreamDeckTests/fake-hidapi.c"), "-o", hidLibrary
        }
    };
    using (var compiler = Process.Start(compile)!)
    {
        await compiler.WaitForExitAsync();
        Check(compiler.ExitCode == 0, "compile native HID regression fixture");
    }
    NativeLibrary.SetDllImportResolver(typeof(StreamDeckHid).Assembly,
        (name, assembly, searchPath) => name == "libhidapi-hidraw.so.0" ? NativeLibrary.Load(hidLibrary) : IntPtr.Zero);
    var fakePath = Path.Combine(temporary, "fake-hidraw");
    await File.WriteAllBytesAsync(fakePath, []);
    using (var nativeHid = new StreamDeckHid(fakePath))
    {
        Check(nativeHid.ReadReport(50).Length == 0, "interrupted native poll is an empty read, not a disconnect");
        Check(nativeHid.ReadReport(50).SequenceEqual(Report(1)), "input resumes after native poll interruption");
        try
        {
            nativeHid.ReadReport(50);
            throw new Exception("A real native I/O failure must disconnect.");
        }
        catch (IOException error)
        {
            Check(error.Message.Contains("Injected HID failure") && error.Message.Contains("errno 5"),
                "native I/O failures include actionable diagnostics");
        }
        Marshal.SetLastPInvokeError(4);
        try
        {
            nativeHid.ReadReport(50);
            throw new Exception("An errno-less device disconnect must not inherit stale EINTR.");
        }
        catch (IOException) { }
        nativeHid.SetBrightness(30);
        nativeHid.SetKeyImage(0, StreamDeckImages.Render(new StreamDeckKeyDisplay(1, "Test", "MUTED")));
    }
    Console.WriteLine("Interrupted native HID reads/writes recover; real disconnects still fail.");

    var configPath = Path.Combine(temporary, "router.json");
    File.Copy(Path.Combine(root, "tests/router.json"), configPath);
    var store = new ConfigStore(configPath);
    await store.UpdateAsync(configuration =>
    {
        foreach (var device in configuration.Devices)
            device.InputEnabled = false;
    });
    var events = new ControlEventBus();
    var graph = new PipeWireGraph(new CommandRunner());
    var router = new AudioRouter(graph, store);
    using var dsp = new DspBridgeService(store, events, NullLogger<DspBridgeService>.Instance);
    using var ndi = new NdiBridgeService(store, events, NullLogger<NdiBridgeService>.Instance);
    var control = new RouterControl(store, router, dsp, ndi, events);
    Check((await router.ApplyAsync()).Success, "fake routing healthy");
    // Concurrent taps must each invert persisted state, rather than racing on
    // a cached GET. Other microphone feeds must remain untouched.
    await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => control.ToggleMicrophoneEnabledAsync(1)));
    var configuration = (await control.GetStateAsync()).Configuration;
    Check(configuration.Devices[0].InputEnabled && configuration.Devices.Skip(1).All(device => !device.InputEnabled),
        "odd concurrent toggles affect only selected mic");
    await control.ToggleMicrophoneEnabledAsync(1);

    var provider = new FakeProvider();
    using var service = new StreamDeckControlService(control, provider, events, NullLogger<StreamDeckControlService>.Instance);
    await service.StartAsync(default);
    try
    {
        await Until(() => Task.FromResult(service.Status.Connected), "deck connects");
        Check(service.Status.Keys.Count == 6 && service.Status.Keys.Take(4).All(key => key.State == "MUTED"), "initial authoritative labels");
        Check(service.Status.Keys[5] == new StreamDeckKeyDisplay(6, "PANIC", "MUTE ALL"), "key 6 is labeled panic mute all");
        var first = provider.Current!;
        first.Reports.Add(Report(1));
        first.Reports.Add(Report(1));
        first.Reports.Add(Report());
        await Until(async () => (await store.LoadAsync()).Devices[0].InputEnabled, "physical tap enables mic");
        await Until(() => Task.FromResult(service.Status.Keys[0].State == "LIVE"), "live feedback");
        Check(service.Status.ButtonPresses == 1, "press/hold/release counted once");
        first.Reports.Add(Report(1));
        first.Reports.Add(Report());
        await Until(async () => !(await store.LoadAsync()).Devices[0].InputEnabled, "second tap disables mic");
        first.Reports.Add(Report(3, 4));
        first.Reports.Add(Report());
        await Until(async () => (await store.LoadAsync()).Devices.Skip(2).All(device => device.InputEnabled), "buttons 3 and 4 map independently");
        Check((await store.LoadAsync()).Devices.Take(2).All(device => !device.InputEnabled), "buttons 3 and 4 leave other feeds alone");
        first.Reports.Add(Report(3, 4));
        first.Reports.Add(Report());
        await Until(async () => (await store.LoadAsync()).Devices.All(device => !device.InputEnabled), "buttons 3 and 4 toggle back");
        await control.SetMicrophoneEnabledAsync(2, true);
        await Until(() => Task.FromResult(service.Status.Keys[1].State == "LIVE"), "external controller feedback");
        await control.SetDeviceFriendlyNameAsync(2, "TEST NAME");
        await Until(() => Task.FromResult(service.Status.Keys[1].Name == "TEST NAME"), "renamed channel feedback");
        first.Reports.Add(Report(5));
        first.Reports.Add(Report());
        await Task.Delay(120);
        Check(service.Status.ButtonPresses == 6, "unassigned buttons never issue actions");
        // One PANIC press mutes a mixed state. Repeat presses and releases
        // must never unmute anything or change playback settings.
        await control.SetMicrophoneEnabledAsync(3, true);
        var outputsBeforePanic = (await store.LoadAsync()).Devices.Select(device => device.OutputEnabled).ToArray();
        var panicState = await PressPanic(first, events);
        Check(panicState.Routing?.Success == true && panicState.Configuration.Devices.All(device => !device.InputEnabled),
            "panic mutes every mic and finishes routing");
        await Until(() => Task.FromResult(service.Status.Keys.Take(4).All(key => key.State == "MUTED")), "panic updates all mic labels");
        Check(service.Status.ButtonPresses == 7 && service.Status.LastButton == 6, "panic press/hold/release fires once");
        await control.SetMicrophoneEnabledAsync(null, true);
        panicState = await PressPanic(first, events);
        Check(panicState.Configuration.Devices.All(device => !device.InputEnabled), "panic also mutes all four live mics");
        panicState = await PressPanic(first, events);
        Check(panicState.Configuration.Devices.All(device => !device.InputEnabled) && service.Status.ButtonPresses == 9,
            "repeated panic cannot unmute");
        Check((await store.LoadAsync()).Devices.Select(device => device.OutputEnabled).SequenceEqual(outputsBeforePanic),
            "panic leaves playback states alone");
        await control.SetMicrophoneEnabledAsync(2, true);
        provider.Attached = false;
        await Until(() => Task.FromResult(!service.Status.Connected), "unplug detected");
        Check((await store.LoadAsync()).Devices[1].InputEnabled, "unplug preserves mic state");
        provider.InitialReport = Report(2);
        provider.Attached = true;
        await Until(() => Task.FromResult(service.Status.Connected), "automatic reconnect");
        var second = provider.Current!;
        second.Reports.Add(Report(2));
        second.Reports.Add(Report());
        await Task.Delay(120);
        Check(service.Status.ButtonPresses == 9 && (await store.LoadAsync()).Devices[1].InputEnabled,
            "held key across reconnect cannot unmute/mute unexpectedly");
        second.Reports.Add(Report(2));
        second.Reports.Add(Report());
        await Until(async () => !(await store.LoadAsync()).Devices[1].InputEnabled, "tap works after reconnect");
        await store.UpdateAsync(configuration => configuration.StreamDeck.Enabled = false);
        await Until(() => Task.FromResult(!service.Status.Enabled && !service.Status.Connected), "configuration disables device");
        Check((await store.LoadAsync()).Devices.All(device => !device.InputEnabled), "disable leaves mic states alone");
    }
    finally { await service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token); }
}
finally
{
    Environment.SetEnvironmentVariable("PATH", oldPath);
    Environment.SetEnvironmentVariable("PI_USB_AUDIO_WPCTL_LOG", oldLog);
    Environment.SetEnvironmentVariable("TRACTUS_DSP_SOCKET", oldDsp);
    Environment.SetEnvironmentVariable("TRACTUS_NDI_SOCKET", oldNdi);
    Directory.Delete(temporary, recursive: true);
}
Console.WriteLine("Stream Deck gesture, PANIC mute-all, image protocol, concurrent toggle, feedback and reconnect tests passed.");

sealed class FakeProvider : IStreamDeckDeviceProvider
{
    public volatile bool Attached = true;
    public byte[]? InitialReport;
    public FakeConnection? Current;
    public IReadOnlyList<string> Enumerate() => Attached ? ["fake-mini"] : [];
    public IStreamDeckConnection Open(string path)
    {
        var connection = new FakeConnection(this);
        if (InitialReport is { } initial)
            connection.Reports.Add(initial);
        Current = connection;
        return connection;
    }
}

sealed class FakeConnection(FakeProvider provider) : IStreamDeckConnection
{
    public BlockingCollection<byte[]> Reports { get; } = new();
    public byte[] ReadReport(int timeoutMilliseconds)
    {
        if (!provider.Attached)
            throw new IOException("Test device unplugged.");
        return Reports.TryTake(out var report, timeoutMilliseconds) ? report : [];
    }
    public void SetBrightness(int percent) { }
    public void SetKeyImage(int key, byte[] bitmap)
    {
        if (!provider.Attached)
            throw new IOException("Test device unplugged.");
    }
    public void Dispose() { Reports.Dispose(); }
}
