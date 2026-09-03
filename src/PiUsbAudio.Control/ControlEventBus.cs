using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PiUsbAudio.Control;

public sealed record DspDeviceMeter(int Number, double PeakDbfs, double RmsDbfs);

public sealed record DspMixMeter(double PeakDbfs, double RmsDbfs);

public sealed record DspMeterState(
    DateTimeOffset Timestamp,
    long Sequence,
    bool DuckingActive,
    double DuckGainReductionDb,
    IReadOnlyList<DspDeviceMeter> Devices,
    DspMixMeter Sidetone,
    DspMixMeter NdiReceiver,
    DspMixMeter Mix);

public sealed record NdiAudioStatus(
    DateTimeOffset Timestamp,
    ulong Sequence,
    bool Enabled,
    bool SenderOnline,
    int Connections,
    double PeakDbfs,
    double RmsDbfs,
    double QueueMilliseconds,
    ulong Underruns,
    ulong Overruns,
    bool ReceiverEnabled,
    bool ReceiverConnected,
    double ReceiverPeakDbfs,
    double ReceiverRmsDbfs,
    double ReceiverQueueMilliseconds,
    ulong ReceiverUnderruns,
    ulong ReceiverOverruns);

public sealed record NdiSourceList(
    DateTimeOffset Timestamp,
    ulong Sequence,
    IReadOnlyList<string> Sources);

public sealed record ControlEvent(string Name, object Data);

public sealed class ControlEventBus
{
    private readonly ConcurrentDictionary<Guid, Channel<ControlEvent>> subscribers = new();
    private DspMeterState? lastMeters;
    private NdiAudioStatus? lastNdi;
    private NdiSourceList? lastNdiSources;

    public DspMeterState? LastMeters => Volatile.Read(ref lastMeters);
    public NdiAudioStatus? LastNdi => Volatile.Read(ref lastNdi);
    public NdiSourceList? LastNdiSources => Volatile.Read(ref lastNdiSources);

    public ControlEventSubscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<ControlEvent>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        subscribers[id] = channel;
        return new ControlEventSubscription(id, channel.Reader, Remove);
    }

    public void Publish(string name, object data)
    {
        if (data is DspMeterState meters)
            Volatile.Write(ref lastMeters, meters);
        else if (data is NdiAudioStatus ndi)
            Volatile.Write(ref lastNdi, ndi);
        else if (data is NdiSourceList ndiSources)
            Volatile.Write(ref lastNdiSources, ndiSources);

        var value = new ControlEvent(name, data);
        foreach (var subscriber in subscribers.Values)
            subscriber.Writer.TryWrite(value);
    }

    private void Remove(Guid id)
    {
        if (subscribers.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }
}

public sealed class ControlEventSubscription(
    Guid id,
    ChannelReader<ControlEvent> reader,
    Action<Guid> remove) : IAsyncDisposable
{
    public ChannelReader<ControlEvent> Reader { get; } = reader;

    public ValueTask DisposeAsync()
    {
        remove(id);
        return ValueTask.CompletedTask;
    }
}
