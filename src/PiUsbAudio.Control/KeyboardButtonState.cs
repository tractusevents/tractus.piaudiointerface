namespace PiUsbAudio.Control;

// One instance per channel, owned by an input session. Timestamps come from
// evdev, not from the time at which an asynchronous routing operation finishes.
public sealed class KeyboardButtonState
{
    private bool down;
    private bool suppressRelease;
    private long pressedAt;
    private long? previousTap;
    public bool Latched { get; private set; }

    // null means no change to the effective press state (including auto-repeat).
    public bool? Handle(int value, long microseconds, bool latchEnabled, int windowMilliseconds)
    {
        var window = windowMilliseconds * 1000L;
        if (value == 1)
        {
            if (down)
                return null;
            down = true;
            pressedAt = microseconds;
            if (Latched)
            {
                Latched = false;
                previousTap = null;
                suppressRelease = true;
                return false;
            }
            Latched = latchEnabled && previousTap is { } first &&
                microseconds >= first && microseconds - first <= window;
            previousTap = null;
            return true;
        }
        if (value != 0 || !down)
            return null;
        down = false;
        if (suppressRelease)
        {
            suppressRelease = false;
            return null;
        }
        if (Latched)
            return null;
        previousTap = latchEnabled && microseconds >= pressedAt && microseconds - pressedAt <= window
            ? pressedAt : null;
        return false;
    }
}
