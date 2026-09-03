using System.Globalization;
using System.Text.Json;

namespace PiUsbAudio.Control;

public sealed record PipeWireNode(int Id, IReadOnlyDictionary<string, string> Properties)
{
    public string Name => Property("node.name");
    public string Description => Property("node.description", "device.description", "node.nick", "node.name");
    public string MediaClass => Property("media.class");
    public string CardKey => Property("api.alsa.card", "api.alsa.card.id", "device.id", "node.name");

    public string Property(params string[] names)
    {
        foreach (var name in names)
        {
            if (Properties.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }
        return string.Empty;
    }
}

public sealed record PipeWirePort(int Id, int NodeId, IReadOnlyDictionary<string, string> Properties)
{
    public string Name => Property("port.name");
    public string Channel => Property("audio.channel");
    public string Direction => Property("port.direction");

    public string Property(string name) => Properties.TryGetValue(name, out var value) ? value : string.Empty;
}

public sealed record PipeWireLink(int Id, int OutputNodeId, int OutputPortId, int InputNodeId, int InputPortId);

public sealed record GadgetPair(int Number, string CardKey, PipeWireNode InputSink, PipeWireNode OutputSource);

public sealed class PipeWireSnapshot
{
    public required IReadOnlyList<PipeWireNode> Nodes { get; init; }
    public required IReadOnlyList<PipeWirePort> Ports { get; init; }
    public required IReadOnlyList<PipeWireLink> Links { get; init; }

    public IReadOnlyList<PipeWirePort> PortsFor(PipeWireNode node, string direction) => Ports
        .Where(port => port.NodeId == node.Id &&
            string.Equals(port.Direction, direction, StringComparison.OrdinalIgnoreCase))
        .OrderBy(port => port.Id)
        .ToList();

    public IReadOnlyList<GadgetPair> FindGadgets(string match)
    {
        var normalizedMatch = Normalize(match);
        bool Matches(PipeWireNode node) => node.Properties.Values.Any(value =>
            Normalize(value).Contains(normalizedMatch, StringComparison.Ordinal)) ||
            IsGadgetNode(node);

        var matched = Nodes.Where(node =>
            (node.MediaClass == "Audio/Sink" || node.MediaClass == "Audio/Source") && Matches(node));

        return matched
            .GroupBy(node => node.CardKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                CardKey = group.Key,
                Sink = group.FirstOrDefault(node => node.MediaClass == "Audio/Sink"),
                Source = group.FirstOrDefault(node => node.MediaClass == "Audio/Source")
            })
            .Where(pair => pair.Sink is not null && pair.Source is not null)
            .OrderBy(pair => SortableCardKey(pair.CardKey), StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.CardKey, StringComparer.OrdinalIgnoreCase)
            .Select((pair, index) => new GadgetPair(index + 1, pair.CardKey, pair.Sink!, pair.Source!))
            .ToList();
    }

    public static bool IsGadgetNode(PipeWireNode node) => node.Properties.Values.Any(value =>
        Normalize(value).Contains("uac2gadget", StringComparison.Ordinal));

    public PipeWireNode? FindNode(string configuredName, string mediaClass)
    {
        if (string.IsNullOrWhiteSpace(configuredName))
            return null;

        var candidates = Nodes.Where(node => node.MediaClass == mediaClass).ToList();
        var exact = candidates.FirstOrDefault(node =>
            string.Equals(node.Name, configuredName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var partial = candidates.Where(node =>
            node.Name.Contains(configuredName, StringComparison.OrdinalIgnoreCase) ||
            node.Description.Contains(configuredName, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }

    public PipeWireNode? FindNodeByName(string name) => Nodes.FirstOrDefault(node =>
        string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string SortableCardKey(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("D8", CultureInfo.InvariantCulture)
            : value;

    private static string Normalize(string value) => new(
        value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

public sealed class PipeWireGraph(CommandRunner commandRunner)
{
    public async Task<PipeWireSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var result = await commandRunner.RunAsync("pw-dump", [], cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"pw-dump failed: {result.StandardError.Trim()}");

        using var document = JsonDocument.Parse(result.StandardOutput);
        var nodes = new List<PipeWireNode>();
        var ports = new List<PipeWirePort>();
        var links = new List<PipeWireLink>();

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var id = ReadInt(item, "id");
            var type = ReadString(item, "type");
            var properties = ReadProperties(item);
            if (type.EndsWith(":Node", StringComparison.Ordinal))
            {
                nodes.Add(new PipeWireNode(id, properties));
            }
            else if (type.EndsWith(":Port", StringComparison.Ordinal))
            {
                ports.Add(new PipeWirePort(id, ReadInt(properties, "node.id"), properties));
            }
            else if (type.EndsWith(":Link", StringComparison.Ordinal))
            {
                links.Add(new PipeWireLink(
                    id,
                    ReadInt(properties, "link.output.node"),
                    ReadInt(properties, "link.output.port"),
                    ReadInt(properties, "link.input.node"),
                    ReadInt(properties, "link.input.port")));
            }
        }

        return new PipeWireSnapshot { Nodes = nodes, Ports = ports, Links = links };
    }

    public Task<CommandResult> CreateLinkAsync(
        int outputPortId,
        int inputPortId,
        CancellationToken cancellationToken = default) =>
        commandRunner.RunAsync(
            "pw-link",
            [outputPortId.ToString(CultureInfo.InvariantCulture), inputPortId.ToString(CultureInfo.InvariantCulture)],
            cancellationToken);

    public Task<CommandResult> RemoveLinkAsync(
        int outputPortId,
        int inputPortId,
        CancellationToken cancellationToken = default) =>
        commandRunner.RunAsync(
            "pw-link",
            ["--disconnect", outputPortId.ToString(CultureInfo.InvariantCulture),
                inputPortId.ToString(CultureInfo.InvariantCulture)],
            cancellationToken);

    public Task<CommandResult> SetVolumeAsync(
        int nodeId,
        double volume,
        double limit,
        CancellationToken cancellationToken = default) =>
        commandRunner.RunAsync(
            "wpctl",
            ["set-volume", "--limit", limit.ToString("0.0000", CultureInfo.InvariantCulture),
                nodeId.ToString(CultureInfo.InvariantCulture),
                volume.ToString("0.0000", CultureInfo.InvariantCulture)],
            cancellationToken);

    private static IReadOnlyDictionary<string, string> ReadProperties(JsonElement item)
    {
        if (!item.TryGetProperty("info", out var info) ||
            !info.TryGetProperty("props", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties.EnumerateObject())
            result[property.Name] = ElementToString(property.Value);
        return result;
    }

    private static int ReadInt(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            return -1;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : int.TryParse(ElementToString(value), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out number) ? number : -1;
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> properties, string name) =>
        properties.TryGetValue(name, out var value) &&
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : -1;

    private static string ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? ElementToString(value) : string.Empty;

    private static string ElementToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText()
    };
}
