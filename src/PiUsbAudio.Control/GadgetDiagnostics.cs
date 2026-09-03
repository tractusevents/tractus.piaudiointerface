using System.Globalization;

namespace PiUsbAudio.Control;

public sealed record GadgetAudioFunction(
    string Name,
    string? DisplayName,
    int? PiToHostChannels,
    int? PiToHostSampleRate,
    int? PiToHostBitDepth,
    string? PiToHostName,
    int? HostToPiChannels,
    int? HostToPiSampleRate,
    int? HostToPiBitDepth,
    string? HostToPiName,
    string? HostToPiSynchronization,
    int? RequestCount);

public sealed record GadgetDiagnostics(
    bool Configured,
    bool Bound,
    bool Healthy,
    string GadgetPath,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> AvailableControllers,
    string? BoundController,
    string? ControllerState,
    string? CurrentSpeed,
    string? MaximumSpeed,
    string? VendorId,
    string? ProductId,
    string? DeviceRevision,
    string? Manufacturer,
    string? Product,
    string? SerialNumber,
    int? MaxPowerMilliAmps,
    IReadOnlyList<string> ActiveFunctions,
    IReadOnlyList<GadgetAudioFunction> AudioFunctions,
    string SerialControlDevice,
    bool SerialControlPresent,
    ApplyResult? Routing);

public sealed class GadgetDiagnosticsService(AudioRouter router)
{
    private const string GadgetPath = "/sys/kernel/config/usb_gadget/pi-usb-audio";
    private const string ControllerPath = "/sys/class/udc";
    private const string SerialControlDevice = "/dev/ttyGS0";

    public GadgetDiagnostics Read()
    {
        var configured = Directory.Exists(GadgetPath);
        var controllers = DirectoryNames(ControllerPath);
        var boundController = ReadText(Path.Combine(GadgetPath, "UDC"));
        var bound = !string.IsNullOrWhiteSpace(boundController);
        var controllerDirectory = bound
            ? Path.Combine(ControllerPath, boundController!)
            : null;
        var activeFunctions = DirectoryNames(Path.Combine(GadgetPath, "configs", "c.1"))
            .Where(name => name.StartsWith("uac2.", StringComparison.Ordinal) ||
                name.StartsWith("acm.", StringComparison.Ordinal))
            .ToArray();
        var audioFunctions = DirectoryNames(Path.Combine(GadgetPath, "functions"))
            .Where(name => name.StartsWith("uac2.", StringComparison.Ordinal))
            .OrderBy(FunctionNumber)
            .Select(ReadAudioFunction)
            .ToArray();
        var controllerState = controllerDirectory is null
            ? null
            : ReadText(Path.Combine(controllerDirectory, "state"));
        var serialPresent = File.Exists(SerialControlDevice);
        var problems = new List<string>();
        if (!configured)
            problems.Add("The pi-usb-audio ConfigFS gadget does not exist.");
        if (controllers.Count == 0)
            problems.Add("No USB Device Controller is available.");
        if (configured && !bound)
            problems.Add("The gadget is not bound to a USB Device Controller.");
        if (bound && !string.Equals(controllerState, "configured", StringComparison.OrdinalIgnoreCase))
            problems.Add($"The host has not configured the gadget (controller state: {controllerState ?? "unknown"}).");
        if (audioFunctions.Length != 4)
            problems.Add($"Expected 4 UAC2 functions; found {audioFunctions.Length}.");
        if (!activeFunctions.Contains("acm.control", StringComparer.Ordinal))
            problems.Add("The CDC ACM serial function is not active.");
        if (!serialPresent)
            problems.Add($"The serial control device {SerialControlDevice} is not present.");

        return new GadgetDiagnostics(
            configured,
            bound,
            problems.Count == 0,
            GadgetPath,
            problems,
            controllers,
            boundController,
            controllerState,
            controllerDirectory is null ? null : ReadText(Path.Combine(controllerDirectory, "current_speed")),
            controllerDirectory is null ? null : ReadText(Path.Combine(controllerDirectory, "maximum_speed")),
            ReadText(Path.Combine(GadgetPath, "idVendor")),
            ReadText(Path.Combine(GadgetPath, "idProduct")),
            ReadText(Path.Combine(GadgetPath, "bcdDevice")),
            ReadText(Path.Combine(GadgetPath, "strings", "0x409", "manufacturer")),
            ReadText(Path.Combine(GadgetPath, "strings", "0x409", "product")),
            ReadText(Path.Combine(GadgetPath, "strings", "0x409", "serialnumber")),
            ReadInteger(Path.Combine(GadgetPath, "configs", "c.1", "MaxPower")),
            activeFunctions,
            audioFunctions,
            SerialControlDevice,
            serialPresent,
            router.LastResult);
    }

    private static GadgetAudioFunction ReadAudioFunction(string name)
    {
        var path = Path.Combine(GadgetPath, "functions", name);
        return new GadgetAudioFunction(
            name,
            ReadText(Path.Combine(path, "function_name")),
            CountChannels(ReadText(Path.Combine(path, "p_chmask"))),
            ReadInteger(Path.Combine(path, "p_srate")),
            ToBitDepth(ReadInteger(Path.Combine(path, "p_ssize"))),
            ReadText(Path.Combine(path, "p_it_name")),
            CountChannels(ReadText(Path.Combine(path, "c_chmask"))),
            ReadInteger(Path.Combine(path, "c_srate")),
            ToBitDepth(ReadInteger(Path.Combine(path, "c_ssize"))),
            ReadText(Path.Combine(path, "c_it_name")),
            ReadText(Path.Combine(path, "c_sync")),
            ReadInteger(Path.Combine(path, "req_number")));
    }

    private static IReadOnlyList<string> DirectoryNames(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? ReadInteger(string path)
    {
        var text = ReadText(path);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static int? CountChannels(string? mask)
    {
        if (string.IsNullOrWhiteSpace(mask))
            return null;
        var style = mask.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? NumberStyles.AllowHexSpecifier
            : NumberStyles.Integer;
        var valueText = style == NumberStyles.AllowHexSpecifier ? mask[2..] : mask;
        if (!ulong.TryParse(valueText, style, CultureInfo.InvariantCulture, out var value))
            return null;
        var count = 0;
        while (value != 0)
        {
            count += (int)(value & 1);
            value >>= 1;
        }
        return count;
    }

    private static int? ToBitDepth(int? sampleSizeBytes) => sampleSizeBytes * 8;

    private static int FunctionNumber(string name)
    {
        var suffix = name[(name.LastIndexOf('.') + 1)..];
        return int.TryParse(suffix.AsSpan("device".Length), out var number) ? number : int.MaxValue;
    }
}
