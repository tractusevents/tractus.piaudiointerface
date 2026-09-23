using PiUsbAudio.Control;

static void Equal<T>(T actual, T expected, string label)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected))
        throw new Exception($"{label}: expected {expected}, got {actual}");
}

static bool? Key(KeyboardButtonState button, int value, long milliseconds, bool enabled = true) =>
    button.Handle(value, milliseconds * 1000, enabled, 300);

var button = new KeyboardButtonState();
Equal(Key(button, 1, 0), true, "press is immediate");
Equal(Key(button, 2, 20), null, "repeat ignored");
Equal(Key(button, 1, 30), null, "duplicate down ignored");
Equal(Key(button, 0, 50), false, "release is immediate");
Equal(Key(button, 0, 70), null, "duplicate release ignored");
Equal(Key(button, 1, 200), true, "second tap presses");
Equal(button.Latched, true, "double tap latches");
Equal(Key(button, 0, 220), null, "latched release keeps press state");
Equal(Key(button, 1, 250), false, "third press releases latch");
Equal(button.Latched, false, "latch cleared");
Equal(Key(button, 0, 270), null, "unlatching release consumed");
Equal(Key(button, 1, 290), true, "next press begins fresh gesture");
Equal(button.Latched, false, "unlatching click cannot seed another double click");
Equal(Key(button, 0, 900), false, "long hold releases");
Equal(Key(button, 1, 950), true, "press after long hold");
Equal(button.Latched, false, "long hold cannot seed double click");

button = new();
Key(button, 1, 0);
Key(button, 0, 20);
Key(button, 1, 301);
Equal(button.Latched, false, "slow double tap is ordinary hold");
button = new();
Key(button, 1, 1000);
Key(button, 0, 1020);
Key(button, 1, 900);
Equal(button.Latched, false, "clock reversal does not latch");
button = new();
Key(button, 1, 0, false);
Key(button, 0, 20, false);
Key(button, 1, 100, false);
Equal(button.Latched, false, "latching can be disabled");
Equal(Key(button, 0, 120, false), false, "disabled latch still releases");

button = new();
Key(button, 1, 0);
Key(button, 0, 20);
Key(button, 1, 300);
Equal(button.Latched, true, "window boundary included");
Equal(new KeyboardButtonState().Latched, false, "new session is unlatched");
foreach (var action in new[] { KeyboardChannelActions.MuteWhileHeld, KeyboardChannelActions.UnmuteWhileHeld })
{
    KeyboardChannelActions.TryGetEnabled(action, pressed: true, out var pressed);
    KeyboardChannelActions.TryGetEnabled(action, pressed: false, out var released);
    Equal(pressed, action == KeyboardChannelActions.UnmuteWhileHeld, $"{action} press state");
    Equal(released, !pressed, $"{action} release state");
}
Equal(new KeyboardControlConfiguration { DoubleClickMilliseconds = 99 }.Validate()
    .Any(error => error.Contains("doubleClickMilliseconds")), true, "timing validation");
Console.WriteLine("Keyboard gesture tests passed.");

foreach (var layer in new[] { 1, 2, 3 })
{
    foreach (var red in new[] { false, true })
    {
        var report = MiniKeyboardHid.RgbReport(layer, red);
        Equal(report.Length, 65, "HID report includes ID plus 64 bytes");
        Equal(Convert.ToHexString(report[..5]), $"03FEB00{layer}08", "RGB header");
        Equal(report[10], (byte)1, "RGB action count");
        Equal(report[12], red ? (byte)0x11 : (byte)0x41, "steady color encoding");
        Equal(report[13..].All(value => value == 0), true, "unused report bytes zero padded");
    }
}
Equal(MiniKeyboardHid.IsSupported(0x514c, 0x8851), true, "attached model supported");
Equal(MiniKeyboardHid.IsSupported(0x1189, 0x8851), false, "do not widen vendor/product allowlist");
Equal(MiniKeyboardHid.IsSupported(0x194f, 0x040b), false, "audio interface excluded");
Console.WriteLine("Keyboard LED framing tests passed.");
var configuration = new RouterConfiguration();
configuration.Normalize();
foreach (var device in configuration.Devices)
    device.InputEnabled = false;
Equal(KeyboardLedService.ShouldShowRed(configuration), false, "all muted is green");
foreach (var device in configuration.Devices)
{
    device.InputEnabled = true;
    Equal(KeyboardLedService.ShouldShowRed(configuration), true, $"channel {device.Number} alone makes red");
    device.InputEnabled = false;
}
Equal(new KeyboardControlConfiguration { LedLayer = 0 }.Validate().Any(error => error.Contains("ledLayer")),
    true, "invalid LED layer rejected");
Console.WriteLine("Keyboard LED state tests passed.");
var probe = MiniKeyboardHid.PerKeyProbeReport(0, false);
Equal(probe.Length, 65, "per-key probe retains HID report framing");
Equal(Convert.ToHexString(probe[..8]), "03FEB00001FF0000", "source-documented K8850 static LED header");
Equal(Convert.ToHexString(probe[8..20]), "FF000000FF000000FFFFFF00", "four distinct per-key RGB slots");
Equal(probe[56..].All(value => value == 0), true, "per-key probe padding");
Equal(MiniKeyboardHid.PerKeyProbeReport(1, true)[3], (byte)1, "alternate layer probe");
Console.WriteLine("Experimental per-key LED framing tests passed.");
var legacy = MiniKeyboardHid.LegacyLedProbeReport(1, true);
Equal(legacy.Length, 65, "legacy LED probe framing");
Equal(Convert.ToHexString(legacy[..12]), "03FEB0010800050100230034", "exact issue 180 legacy packet");
Equal(legacy[12..].All(value => value == 0), true, "legacy LED probe padding");
