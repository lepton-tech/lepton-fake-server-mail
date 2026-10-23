using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace FakeServerEmail.Web;

public sealed class EventHub : IDisposable
{
    private const int SubscriberCapacity = 100;
    private static readonly TimeSpan DispatchInterval = TimeSpan.FromMilliseconds(300);

    private readonly Lock _lock = new();
    private readonly List<Channel<string>> _subscribers = [];
    private readonly HashSet<string> _pending = [];
    private readonly Timer _timer;

    public EventHub()
    {
        _timer = new Timer(_ => Dispatch(), null, DispatchInterval, DispatchInterval);
    }

    public void Publish(string name)
    {
        lock (_lock)
            _pending.Add(name);
    }

    public async IAsyncEnumerable<string> ListenAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(SubscriberCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_lock)
            _subscribers.Add(channel);
        try
        {
            await foreach (var name in channel.Reader.ReadAllAsync(ct))
                yield return name;
        }
        finally
        {
            lock (_lock)
                _subscribers.Remove(channel);
        }
    }

    public void Dispose() => _timer.Dispose();

    private void Dispatch()
    {
        string[] names;
        Channel<string>[] subscribers;
        lock (_lock)
        {
            if (_pending.Count == 0)
                return;
            names = [.. _pending];
            _pending.Clear();
            subscribers = [.. _subscribers];
        }
        foreach (var s in subscribers)
            foreach (var n in names)
                s.Writer.TryWrite(n);
    }
}
