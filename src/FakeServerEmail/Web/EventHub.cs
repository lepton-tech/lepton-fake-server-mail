using System.Threading.Channels;

namespace FakeServerEmail.Web;

public sealed class EventHub : IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<Channel<string>> _subscribers = [];
    private readonly HashSet<string> _pending = [];
    private readonly Timer _timer;

    public EventHub()
    {
        _timer = new Timer(_ => Dispatch(), null, 300, 300);
    }

    public void Publish(string name)
    {
        lock (_lock)
            _pending.Add(name);
    }

    public ChannelReader<string> Subscribe(CancellationToken ct)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_lock)
            _subscribers.Add(channel);
        ct.Register(() =>
        {
            lock (_lock)
                _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        });
        return channel.Reader;
    }

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

    public void Dispose() => _timer.Dispose();
}
