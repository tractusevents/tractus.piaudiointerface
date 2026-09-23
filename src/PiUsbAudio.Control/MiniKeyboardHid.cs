using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PiUsbAudio.Control;

public sealed record MiniKeyboardDevice(string Path, string UsbPath, ushort VendorId, ushort ProductId)
{
    public string InstanceId { get; init; } = string.Empty;
}

public sealed class MiniKeyboardHid : IDisposable
{
    private const string Library = "libhidapi-hidraw.so.0";
    private readonly IntPtr handle;
    private readonly SafeFileHandle lockHandle;

    public MiniKeyboardHid(MiniKeyboardDevice device)
    {
        lockHandle = File.OpenHandle(device.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            // Cooperating service/diagnostic instances serialize complete
            // transactions across processes, not just individual HID writes.
            if (Flock(lockHandle.DangerousGetHandle().ToInt32(), 2 | 4) != 0)
                throw new IOException($"Mini-keyboard {device.Path} is busy. Disable automatic LED feedback before running diagnostics.");
            handle = HidOpenPath(device.Path);
            if (handle == IntPtr.Zero)
                throw new IOException($"Cannot open {device.Path}. Check mini-keyboard hidraw permissions.");
        }
        catch
        {
            lockHandle.Dispose();
            throw;
        }
    }

    public static IReadOnlyList<MiniKeyboardDevice> Enumerate()
    {
        if (!OperatingSystem.IsLinux())
            return [];
        var devices = new List<MiniKeyboardDevice>();
        var first = HidEnumerate(0, 0);
        try
        {
            for (var pointer = first; pointer != IntPtr.Zero;)
            {
                var info = Marshal.PtrToStructure<HidDeviceInfo>(pointer);
                pointer = info.Next;
                if (Environment.GetEnvironmentVariable("PI_USB_AUDIO_HID_DEBUG") == "1")
                    Console.Error.WriteLine($"HID {Marshal.PtrToStringUTF8(info.Path)} {info.VendorId:x4}:{info.ProductId:x4} interface={info.InterfaceNumber}");
                if (info.InterfaceNumber != 0 || !IsSupported(info.VendorId, info.ProductId))
                    continue;
                var path = Marshal.PtrToStringUTF8(info.Path);
                var usb = path is null ? null : FindUsbParent(
                    $"/sys/class/hidraw/{System.IO.Path.GetFileName(path)}/device");
                if (Environment.GetEnvironmentVariable("PI_USB_AUDIO_HID_DEBUG") == "1")
                    Console.Error.WriteLine($"USB parent: {usb ?? "(not found)"}");
                if (path is not null && usb is not null)
                    devices.Add(new(path, usb, info.VendorId, info.ProductId)
                    {
                        InstanceId = ResolvePath($"/sys/class/hidraw/{System.IO.Path.GetFileName(path)}/device") ?? path
                    });
            }
        }
        finally { HidFreeEnumeration(first); }
        return devices;
    }

    public static bool IsSupported(ushort vendor, ushort product) =>
        vendor is 0x1189 or 0x514c &&
        (product is 0x8842 or 0x8840 or 0x8830 or 0x8831 or 0x8832 or 0x8833 or 0x8850 ||
            vendor == 0x514c && product == 0x8851);

    public static string? FindUsbParent(string path)
    {
        // sysfs paths contain symlinks in multiple components. Resolving only
        // the final "device" link can leave a logical /sys/class parent chain.
        var canonical = ResolvePath(path);
        if (canonical is null)
            return null;
        var directory = new DirectoryInfo(canonical);
        for (; directory is not null; directory = directory.Parent)
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "idVendor")) &&
                File.Exists(System.IO.Path.Combine(directory.FullName, "idProduct")))
                return directory.FullName;
        return null;
    }

    private static string? ResolvePath(string path)
    {
        var resolved = RealPath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
            return null;
        try { return Marshal.PtrToStringUTF8(resolved); }
        finally { Free(resolved); }
    }

    public static byte[] RgbReport(int layer, bool red)
    {
        if (layer is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(layer));
        var report = Report(0xfe, 0xb0, (byte)layer, 0x08);
        report[10] = 0x01;
        report[12] = red ? (byte)0x11 : (byte)0x41;
        return report;
    }

    public void SetColor(int layer, bool red, bool commit)
    {
        Write(RgbReport(layer, red));
        if (commit)
        {
            Write(Report(0xfd, 0xfe, 0xff));
            Thread.Sleep(200);
        }
    }

    // Experimental K8850 LED-only packet, exposed only to the diagnostic tool.
    // Compatibility with K8851 must be established visually before live use.
    public static byte[] PerKeyProbeReport(int wireLayer, bool reverse)
    {
        if (wireLayer is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(wireLayer));
        var report = Report(0xfe, 0xb0, (byte)wireLayer, 0x01, 0xff, 0x00, 0x00);
        byte[][] colors = [[255, 0, 0], [0, 255, 0], [0, 0, 255], [255, 255, 0]];
        for (var key = 0; key < 16; key++)
            colors[(key + (reverse ? 1 : 0)) % colors.Length].CopyTo(report, 8 + key * 3);
        return report;
    }

    public void SetPerKeyProbePattern(int wireLayer, bool reverse) =>
        Write(PerKeyProbeReport(wireLayer, reverse));

    // Exact legacy LED format reported in ch57x-keyboard-tool issue #180.
    // On the reporter's 8840, these produced one white/green key instead of
    // global lighting. This is a diagnostic, not a supported per-key API.
    public static byte[] LegacyLedProbeReport(int layer, bool green)
    {
        if (layer is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(layer));
        return Report(0xfe, 0xb0, (byte)layer, 0x08, 0x00, 0x05, 0x01,
            0x00, green ? (byte)0x23 : (byte)0x11, 0x00, 0x34);
    }

    public void SetLegacyLedProbe(int layer, bool green) =>
        Write(LegacyLedProbeReport(layer, green));

    public byte[] QueryModel()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Write(Report(0xfb, 0xfb, 0xfb));
            var report = Read();
            if (report.Length >= 5 && report[0] == 0x03 && report[1] == 0xfb)
                return report;
        }
        return [];
    }

    public IReadOnlyList<byte[]> ReadLayer(int layer)
    {
        if (layer is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(layer));
        var reports = new SortedDictionary<int, byte[]>();
        // Some firmware drops records during a burst or delivers an earlier
        // query reply. Match the layer/key identifiers and retry missing keys.
        for (var attempt = 0; attempt < 3 && reports.Count < 24; attempt++)
        {
            Write(Report(0xfa, 0x0f, 0x03, (byte)layer));
            for (var response = 0; response < 26; response++)
            {
                var report = Read();
                if (report.Length == 0)
                    break;
                if (report.Length >= 51 && report[0] == 0x03 && report[1] == 0xfa &&
                    report[2] is >= 1 and <= 24 && report[3] == layer)
                    reports[report[2]] = report;
            }
        }
        return reports.Values.ToArray();
    }

    private byte[] Read()
    {
        var report = new byte[64];
        var count = HidReadTimeout(handle, report, (nuint)report.Length, 500);
        if (count < 0)
            throw new IOException("Mini-keyboard HID read failed.");
        if (Environment.GetEnvironmentVariable("PI_USB_AUDIO_HID_DEBUG") == "1")
            Console.Error.WriteLine($"HID RX ({count}): {Convert.ToHexString(report.AsSpan(0, count))}");
        return report[..count];
    }

    private static byte[] Report(params byte[] payload)
    {
        var report = new byte[65];
        report[0] = 0x03;
        payload.CopyTo(report, 1);
        return report;
    }

    private void Write(byte[] report)
    {
        if (Environment.GetEnvironmentVariable("PI_USB_AUDIO_HID_DEBUG") == "1")
            Console.Error.WriteLine($"HID TX ({report.Length}): {Convert.ToHexString(report)}");
        if (HidWrite(handle, report, (nuint)report.Length) != report.Length)
            throw new IOException("Mini-keyboard HID write failed or was incomplete.");
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
    [DllImport(Library, EntryPoint = "hid_write")]
    private static extern int HidWrite(IntPtr device, byte[] data, nuint length);
    [DllImport(Library, EntryPoint = "hid_read_timeout")]
    private static extern int HidReadTimeout(IntPtr device, byte[] data, nuint length, int milliseconds);
    [DllImport(Library, EntryPoint = "hid_close")]
    private static extern void HidClose(IntPtr device);
    [DllImport("libc", EntryPoint = "realpath")]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);
    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);
    [DllImport("libc", EntryPoint = "flock")]
    private static extern int Flock(int descriptor, int operation);
}
