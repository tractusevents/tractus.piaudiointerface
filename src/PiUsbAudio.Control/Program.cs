using System.Text.Json;
using PiUsbAudio.Control;

var command = args.FirstOrDefault(argument => !argument.StartsWith('-')) ?? "serve";
var configPath = GetOption(args, "--config") ??
    Environment.GetEnvironmentVariable("PI_USB_AUDIO_CONFIG") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "pi-usb-audio", "router.json");

if (command is "list" or "apply")
{
    var commandRunner = new CommandRunner();
    var graph = new PipeWireGraph(commandRunner);
    if (command == "list")
    {
        var snapshot = await graph.GetSnapshotAsync();
        var view = snapshot.Nodes
            .Where(node => node.MediaClass is "Audio/Source" or "Audio/Sink")
            .Select(node => new
            {
                node.Id,
                node.Name,
                node.Description,
                node.MediaClass,
                node.CardKey,
                Ports = snapshot.PortsFor(node, node.MediaClass == "Audio/Source" ? "out" : "in")
                    .Select(port => new { port.Id, port.Name, port.Channel })
            });
        Console.WriteLine(JsonSerializer.Serialize(view, new JsonSerializerOptions { WriteIndented = true }));
        return;
    }

    var router = new AudioRouter(graph, new ConfigStore(configPath));
    var applyResult = await router.ApplyAsync();
    Console.WriteLine(JsonSerializer.Serialize(applyResult, new JsonSerializerOptions { WriteIndented = true }));
    Environment.ExitCode = applyResult.Success ? 0 : 1;
    return;
}

if (command != "serve")
{
    Console.Error.WriteLine("Usage: PiUsbAudio.Control [serve|list|apply] [--config PATH]");
    Environment.ExitCode = 2;
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = "wwwroot"
});
var listenUrl = Environment.GetEnvironmentVariable("PI_USB_AUDIO_URL") ?? "http://0.0.0.0:5055";
builder.WebHost.UseUrls(listenUrl);
builder.Services.AddSingleton(new ConfigStore(configPath));
builder.Services.AddSingleton<CommandRunner>();
builder.Services.AddSingleton<PipeWireGraph>();
builder.Services.AddSingleton<ControlEventBus>();
builder.Services.AddSingleton<DspBridgeService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<DspBridgeService>());
builder.Services.AddSingleton<NdiBridgeService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<NdiBridgeService>());
builder.Services.AddSingleton<AudioRouter>();
builder.Services.AddSingleton<GadgetDiagnosticsService>();
builder.Services.AddSingleton<RouterControl>();
builder.Services.AddHostedService<RouterReconciler>();
builder.Services.AddHostedService<SerialControlService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/info", () => Results.Ok(new
{
    service = "Tractus USB Audio Interface",
    apiVersion = 1,
    serialProtocolVersion = 1,
    virtualDeviceCount = 4,
    capabilities = new[]
    {
        "ducking", "multi-trigger-ducking", "self-ducking", "solo", "push-meters", "server-sent-events",
        "sidetone", "ndi-audio", "ndi-audio-receiver", "ndi-source-discovery",
        "push-ndi-status", "push-ndi-sources", "usb-gadget-diagnostics"
    }
}));

app.MapGet("/api/config", async (ConfigStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.LoadAsync(cancellationToken)));

app.MapPut("/api/config", async (
    RouterConfiguration configuration,
    ConfigStore store,
    AudioRouter router,
    ControlEventBus eventBus,
    CancellationToken cancellationToken) =>
{
    var errors = configuration.Validate();
    if (errors.Count > 0)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["configuration"] = errors.ToArray() });
    await store.SaveAsync(configuration, cancellationToken);
    var routing = await router.ApplyAsync(cancellationToken);
    eventBus.Publish("state", new ControlState(configuration, routing));
    return Results.Ok(routing);
});

app.MapPost("/api/apply", async (
    AudioRouter router,
    ConfigStore store,
    ControlEventBus eventBus,
    CancellationToken cancellationToken) =>
{
    var routing = await router.ApplyAsync(cancellationToken);
    eventBus.Publish("state", new ControlState(await store.LoadAsync(cancellationToken), routing));
    return Results.Ok(routing);
});

app.MapGet("/api/mappings", async (ConfigStore store, CancellationToken cancellationToken) =>
    Results.Ok(AudioDeviceMappings.FromConfiguration(await store.LoadAsync(cancellationToken))));

app.MapPut("/api/mappings", async (
    AudioDeviceMappings mappings,
    RouterControl control,
    CancellationToken cancellationToken) =>
    Results.Ok(await control.SetMappingsAsync(mappings, cancellationToken)));

app.MapGet("/api/state", async (RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.GetStateAsync(cancellationToken)));

app.MapGet("/api/gadget", (GadgetDiagnosticsService diagnostics) =>
    Results.Ok(diagnostics.Read()));

app.MapPost("/api/mics/{number:int}/mute", async (
    int number, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetMicrophoneEnabledAsync(number, false, cancellationToken)));
app.MapPost("/api/mics/{number:int}/unmute", async (
    int number, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetMicrophoneEnabledAsync(number, true, cancellationToken)));
app.MapPost("/api/mics/mute-all", async (
    RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetMicrophoneEnabledAsync(null, false, cancellationToken)));
app.MapPost("/api/mics/unmute-all", async (
    RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetMicrophoneEnabledAsync(null, true, cancellationToken)));
app.MapPost("/api/mics/{number:int}/gain", async (
    int number, double percent, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetMicrophoneGainAsync(number, percent / 100.0, cancellationToken)));

app.MapPost("/api/outputs/{number:int}/mute", async (
    int number, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetOutputEnabledAsync(number, false, cancellationToken)));
app.MapPost("/api/outputs/{number:int}/unmute", async (
    int number, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetOutputEnabledAsync(number, true, cancellationToken)));
app.MapPost("/api/outputs/mute-all", async (
    RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetOutputEnabledAsync(null, false, cancellationToken)));
app.MapPost("/api/outputs/unmute-all", async (
    RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetOutputEnabledAsync(null, true, cancellationToken)));
app.MapPost("/api/outputs/{number:int}/gain", async (
    int number, double percent, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetOutputGainAsync(number, percent / 100.0, cancellationToken)));
app.MapPost("/api/outputs/{number:int}/solo", async (
    int number, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetOutputSoloAsync(number, true, false, cancellationToken)));
app.MapPost("/api/outputs/{number:int}/solo-exclusive", async (
    int number, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetOutputSoloAsync(number, true, true, cancellationToken)));
app.MapPost("/api/outputs/{number:int}/unsolo", async (
    int number, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetOutputSoloAsync(number, false, false, cancellationToken)));
app.MapPost("/api/outputs/unsolo-all", async (
    RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.ClearOutputSoloAsync(cancellationToken)));
app.MapPost("/api/master/gain", async (
    double percent, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetPhysicalOutputGainAsync(percent / 100.0, cancellationToken)));
app.MapPost("/api/sidetone/mute", async (
    RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetSidetoneEnabledAsync(false, cancellationToken)));
app.MapPost("/api/sidetone/unmute", async (
    RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetSidetoneEnabledAsync(true, cancellationToken)));
app.MapPost("/api/sidetone/gain", async (
    double percent, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetSidetoneGainAsync(percent / 100.0, cancellationToken)));

app.MapPost("/api/ducking/enable", async (
    RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetDuckingEnabledAsync(true, cancellationToken)));
app.MapPost("/api/ducking/bypass", async (
    RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetDuckingEnabledAsync(false, cancellationToken)));
app.MapPost("/api/ducking/priority", async (
    int device, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetDuckingPriorityAsync(device, cancellationToken)));
app.MapPost("/api/ducking/priority-self", async (
    RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetDuckingPriorityAsync(0, cancellationToken)));
app.MapGet("/api/ducking/triggers", async (
    ConfigStore store, CancellationToken cancellationToken) =>
    Results.Ok(new DuckingTriggerSelection
    {
        Sources = (await store.LoadAsync(cancellationToken)).Ducking.TriggerSources ?? []
    }));
app.MapPut("/api/ducking/triggers", async (
    DuckingTriggerSelection selection,
    RouterControl control,
    CancellationToken cancellationToken) =>
    Results.Ok(await control.SetDuckingTriggersAsync(selection.Sources, cancellationToken)));
app.MapPost("/api/ducking/triggers/{source:int}/enable", async (
    int source, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetDuckingTriggerAsync(source, true, cancellationToken)));
app.MapPost("/api/ducking/triggers/{source:int}/disable", async (
    int source, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetDuckingTriggerAsync(source, false, cancellationToken)));
app.MapPost("/api/ducking/threshold", async (
    double dbfs, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetDuckingThresholdAsync(dbfs, cancellationToken)));
app.MapPost("/api/ducking/depth", async (
    double db, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetDuckingDepthAsync(db, cancellationToken)));
app.MapPost("/api/ducking/attack", async (
    double milliseconds, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetDuckingAttackAsync(milliseconds, cancellationToken)));
app.MapPost("/api/ducking/hold", async (
    double milliseconds, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetDuckingHoldAsync(milliseconds, cancellationToken)));
app.MapPost("/api/ducking/release", async (
    double milliseconds, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetDuckingReleaseAsync(milliseconds, cancellationToken)));

app.MapGet("/api/ndi", async (ConfigStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.LoadAsync(cancellationToken)).NdiAudio));
app.MapPut("/api/ndi", async (
    NdiAudioConfiguration ndiAudio,
    RouterControl control,
    CancellationToken cancellationToken) =>
    Results.Ok(await control.SetNdiConfigurationAsync(ndiAudio, cancellationToken)));
app.MapPost("/api/ndi/enable", async (
    RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetNdiEnabledAsync(true, cancellationToken)));
app.MapPost("/api/ndi/disable", async (
    RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetNdiEnabledAsync(false, cancellationToken)));
app.MapPost("/api/ndi/name", async (
    string name, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetNdiSourceNameAsync(name, cancellationToken)));
app.MapGet("/api/ndi/status", (ControlEventBus eventBus) =>
    eventBus.LastNdi is { } ndi ? Results.Ok(ndi) : Results.NoContent());
app.MapGet("/api/ndi/receiver", async (ConfigStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.LoadAsync(cancellationToken)).NdiReceiver));
app.MapPut("/api/ndi/receiver", async (
    NdiReceiverConfiguration receiver,
    RouterControl control,
    CancellationToken cancellationToken) =>
    Results.Ok(await control.SetNdiReceiverConfigurationAsync(receiver, cancellationToken)));
app.MapPost("/api/ndi/receiver/enable", async (
    RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetNdiReceiverEnabledAsync(true, cancellationToken)));
app.MapPost("/api/ndi/receiver/disable", async (
    RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetNdiReceiverEnabledAsync(false, cancellationToken)));
app.MapPost("/api/ndi/receiver/source", async (
    string name, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetNdiReceiverSourceAsync(name, cancellationToken)));
app.MapPost("/api/ndi/receiver/gain", async (
    double percent, RouterControl control, CancellationToken cancellationToken) =>
    Results.Ok(await control.SetNdiReceiverGainAsync(percent / 100.0, cancellationToken)));
app.MapGet("/api/ndi/sources", (ControlEventBus eventBus) =>
    Results.Ok(eventBus.LastNdiSources ?? new NdiSourceList(
        DateTimeOffset.UtcNow, 0, Array.Empty<string>())));

app.MapGet("/api/meters", (ControlEventBus eventBus) =>
    eventBus.LastMeters is { } meters ? Results.Ok(meters) : Results.NoContent());

var eventJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
app.MapGet("/api/events", async (
    HttpContext context,
    RouterControl control,
    ControlEventBus eventBus) =>
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";
    await using var subscription = eventBus.Subscribe();
    try
    {
        await WriteEventAsync(
            context,
            "state",
            await control.GetStateAsync(context.RequestAborted),
            eventJsonOptions);
        if (eventBus.LastMeters is { } meters)
            await WriteEventAsync(context, "meters", meters, eventJsonOptions);
        if (eventBus.LastNdi is { } ndi)
            await WriteEventAsync(context, "ndi", ndi, eventJsonOptions);
        if (eventBus.LastNdiSources is { } ndiSources)
            await WriteEventAsync(context, "ndi-sources", ndiSources, eventJsonOptions);

        await foreach (var item in subscription.Reader.ReadAllAsync(context.RequestAborted))
        {
            await WriteEventAsync(context, item.Name, item.Data, eventJsonOptions);
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // Normal SSE client disconnect.
    }
});

app.MapGet("/api/status", async (
    AudioRouter router,
    PipeWireGraph graph,
    ConfigStore store,
    ControlEventBus eventBus,
    CancellationToken cancellationToken) =>
{
    try
    {
        var configuration = await store.LoadAsync(cancellationToken);
        var snapshot = await graph.GetSnapshotAsync(cancellationToken);
        return Results.Ok(new
        {
            router.LastResult,
            Ndi = eventBus.LastNdi,
            NdiSources = eventBus.LastNdiSources,
            GadgetCount = snapshot.FindGadgets(configuration.GadgetMatch).Count,
            Sources = snapshot.Nodes.Where(node => node.MediaClass == "Audio/Source")
                .Select(node => new { node.Name, node.Description }),
            Sinks = snapshot.Nodes.Where(node => node.MediaClass == "Audio/Sink")
                .Select(node => new { node.Name, node.Description })
        });
    }
    catch (Exception exception)
    {
        return Results.Problem(exception.Message);
    }
});

app.MapGet("/api/nodes", async (PipeWireGraph graph, CancellationToken cancellationToken) =>
{
    var snapshot = await graph.GetSnapshotAsync(cancellationToken);
    return Results.Ok(snapshot.Nodes
        .Where(node => node.MediaClass is "Audio/Source" or "Audio/Sink")
        .Select(node => new
        {
            node.Id,
            node.Name,
            node.Description,
            node.MediaClass,
            IsGadget = PipeWireSnapshot.IsGadgetNode(node),
            Ports = snapshot.PortsFor(node, node.MediaClass == "Audio/Source" ? "out" : "in")
                .Select(port => new { port.Id, port.Name, port.Channel })
        }));
});

app.MapFallbackToFile("index.html");
await app.RunAsync();

static string? GetOption(string[] arguments, string option)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (arguments[index] == option)
            return arguments[index + 1];
    }
    return null;
}

static async Task WriteEventAsync(
    HttpContext context,
    string eventName,
    object data,
    JsonSerializerOptions jsonOptions)
{
    var json = JsonSerializer.Serialize(data, data.GetType(), jsonOptions);
    await context.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", context.RequestAborted);
    await context.Response.Body.FlushAsync(context.RequestAborted);
}
