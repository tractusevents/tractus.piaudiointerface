using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PiUsbAudio.Control;

public interface IStreamDeckConnection : IDisposable
{
    byte[] ReadReport(int timeoutMilliseconds);
    void SetBrightness(int percent);
    void SetKeyImage(int key, byte[] bitmap);
}

public interface IStreamDeckDeviceProvider
{
    IReadOnlyList<string> Enumerate();
    IStreamDeckConnection Open(string path);
}

public sealed class StreamDeckDeviceProvider(ILogger<StreamDeckHid> logger) : IStreamDeckDeviceProvider
{
    public IReadOnlyList<string> Enumerate() => StreamDeckHid.Enumerate();
    public IStreamDeckConnection Open(string path) => new StreamDeckHid(path, logger);
}

// The Mini uses its own legacy HID protocol, not the protocol for larger decks.
// https://docs.elgato.com/streamdeck/hid/mini/
public sealed class StreamDeckHid : IStreamDeckConnection
{
    private const string Library = "libhidapi-hidraw.so.0";
    private const int ImageReportSize = 1024;
    private const int ImageHeaderSize = 16;
    private const int InterruptedSystemCall = 4;
    private readonly IntPtr handle;
    private readonly SafeFileHandle lockHandle;
    private readonly ILogger<StreamDeckHid>? logger;

    public StreamDeckHid(string path, ILogger<StreamDeckHid>? logger = null)
    {
        this.logger = logger;
        lockHandle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            if (Flock(lockHandle.DangerousGetHandle().ToInt32(), 2 | 4) != 0)
                throw new IOException($"Stream Deck {path} is already in use.");
            handle = HidOpenPath(path);
            if (handle == IntPtr.Zero)
                throw new IOException($"Cannot open Stream Deck {path}. Check hidraw permissions.");
        }
        catch
        {
            lockHandle.Dispose();
            throw;
        }
    }

    public static IReadOnlyList<string> Enumerate()
    {
        if (!OperatingSystem.IsLinux())
            return [];
        var paths = new List<string>();
        var first = HidEnumerate(0x0fd9, 0x0063);
        try
        {
            for (var pointer = first; pointer != IntPtr.Zero;)
            {
                var device = Marshal.PtrToStructure<HidDeviceInfo>(pointer);
                pointer = device.Next;
                if (device.InterfaceNumber == 0 && Marshal.PtrToStringUTF8(device.Path) is { } path)
                    paths.Add(path);
            }
        }
        finally { HidFreeEnumeration(first); }
        return paths.Order(StringComparer.Ordinal).ToArray();
    }

    public byte[] ReadReport(int timeoutMilliseconds)
    {
        var report = new byte[65];
        var count = HidReadTimeout(handle, report, (nuint)report.Length, timeoutMilliseconds);
        if (count < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            // hidapi 0.14's hidraw backend returns -1 when poll/read is
            // interrupted by a Unix signal. The device is still connected.
            // Treat this as an empty read and let the next poll resume input.
            if (error == InterruptedSystemCall)
            {
                logger?.LogInformation("Stream Deck HID read interrupted; keeping the connection open");
                return [];
            }
            throw Failure("Stream Deck HID input failed", error);
        }
        return report[..count];
    }

    public void SetBrightness(int percent)
    {
        if (percent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent));
        var report = new byte[17];
        new byte[] { 0x05, 0x55, 0xaa, 0xd1, 0x01, (byte)percent }.CopyTo(report, 0);
        WriteReport(report, feature: true);
    }

    public void SetKeyImage(int key, byte[] bitmap)
    {
        foreach (var report in ImageReports(key, bitmap))
            WriteReport(report, feature: false);
    }

    private void WriteReport(byte[] report, bool feature)
    {
        while (true)
        {
            var count = feature
                ? HidSendFeatureReport(handle, report, (nuint)report.Length)
                : HidWrite(handle, report, (nuint)report.Length);
            var error = Marshal.GetLastPInvokeError();
            if (count == report.Length)
                return;
            // A syscall returning EINTR transferred no data, so retry the
            // same report. Never replay a positive, partially written report.
            if (count < 0 && error == InterruptedSystemCall)
                continue;
            throw Failure("Stream Deck HID write failed", error);
        }
    }

    private IOException Failure(string operation, int error)
    {
        var pointer = HidError(handle);
        var detail = new StringBuilder();
        // Linux wchar_t is UTF-32, whereas PtrToStringUni reads UTF-16.
        if (pointer != IntPtr.Zero)
            for (var offset = 0; offset < 1024; offset++)
            {
                var code = Marshal.ReadInt32(pointer, offset * sizeof(int));
                if (code == 0)
                    break;
                if (!Rune.TryCreate(code, out var rune))
                    break;
                detail.Append(rune.ToString());
            }
        return new IOException($"{operation} (errno {error}): {detail}");
    }

    public static IEnumerable<byte[]> ImageReports(int key, byte[] bitmap)
    {
        if (key is < 0 or >= 6)
            throw new ArgumentOutOfRangeException(nameof(key));
        if (bitmap.Length != StreamDeckImages.BitmapSize || bitmap[0] != 'B' || bitmap[1] != 'M')
            throw new ArgumentException("An 80 by 80, 24-bit BMP is required.", nameof(bitmap));
        const int payloadSize = ImageReportSize - ImageHeaderSize;
        for (int offset = 0, page = 0; offset < bitmap.Length; offset += payloadSize, page++)
        {
            var length = Math.Min(payloadSize, bitmap.Length - offset);
            var report = new byte[ImageReportSize];
            report[0] = 0x02;
            report[1] = 0x01;
            report[2] = (byte)page;
            report[4] = (byte)(offset + length == bitmap.Length ? 1 : 0);
            report[5] = (byte)(key + 1);
            bitmap.AsSpan(offset, length).CopyTo(report.AsSpan(ImageHeaderSize));
            yield return report;
        }
    }

    public void Dispose()
    {
        HidClose(handle);
        lockHandle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidDeviceInfo
    {
        public IntPtr Path;
        public ushort VendorId;
        public ushort ProductId;
        public IntPtr SerialNumber;
        public ushort ReleaseNumber;
        public IntPtr ManufacturerString;
        public IntPtr ProductString;
        public ushort UsagePage;
        public ushort Usage;
        public int InterfaceNumber;
        public IntPtr Next;
    }

    [DllImport(Library, EntryPoint = "hid_enumerate")]
    private static extern IntPtr HidEnumerate(ushort vendor, ushort product);
    [DllImport(Library, EntryPoint = "hid_free_enumeration")]
    private static extern void HidFreeEnumeration(IntPtr devices);
    [DllImport(Library, EntryPoint = "hid_open_path")]
    private static extern IntPtr HidOpenPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library, EntryPoint = "hid_write", SetLastError = true)]
    private static extern int HidWrite(IntPtr device, byte[] data, nuint length);
    [DllImport(Library, EntryPoint = "hid_send_feature_report", SetLastError = true)]
    private static extern int HidSendFeatureReport(IntPtr device, byte[] data, nuint length);
    [DllImport(Library, EntryPoint = "hid_read_timeout", SetLastError = true)]
    private static extern int HidReadTimeout(IntPtr device, byte[] data, nuint length, int milliseconds);
    [DllImport(Library, EntryPoint = "hid_error")]
    private static extern IntPtr HidError(IntPtr device);
    [DllImport(Library, EntryPoint = "hid_close")]
    private static extern void HidClose(IntPtr device);
    [DllImport("libc", EntryPoint = "flock")]
    private static extern int Flock(int descriptor, int operation);
}
