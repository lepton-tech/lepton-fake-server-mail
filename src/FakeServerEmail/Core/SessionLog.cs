namespace FakeServerEmail.Core;

public sealed record LogLine(DateTimeOffset Time, char Direction, string Text);

public sealed class ProtocolSession
{
    private readonly Lock _lock = new();
    private readonly List<LogLine> _lines = [];
    private readonly SessionLog _owner;

    internal ProtocolSession(SessionLog owner, int id, string protocol, int port, string client, bool secure)
    {
        _owner = owner;
        Id = id;
        Protocol = protocol;
        Port = port;
        Client = client;
        Secure = secure;
        StartedAt = DateTimeOffset.Now;
    }

    public int Id { get; }
    public string Protocol { get; }
    public int Port { get; }
    public string Client { get; }
    public bool Secure { get; set; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? EndedAt { get; private set; }
    public string? User { get; set; }
    public string? AuthMechanism { get; set; }
    public string? Error { get; set; }
    public int MessagesReceived { get; set; }

    public int LineCount
    {
        get { lock (_lock) return _lines.Count; }
    }

    public IReadOnlyList<LogLine> Lines
    {
        get { lock (_lock) return [.. _lines]; }
    }

    public void FromClient(string text) => Add('C', text);

    public void FromServer(string text) => Add('S', text);

    public void Info(string text) => Add('*', text);

    public void End()
    {
        EndedAt = DateTimeOffset.Now;
        _owner.NotifyChanged(this);
    }

    private void Add(char direction, string text)
    {
        if (text.Length > 4000)
            text = text[..4000] + $" … (+{text.Length - 4000} caracteres)";
        lock (_lock)
        {
            if (_lines.Count >= 5000)
                return;
            _lines.Add(new LogLine(DateTimeOffset.Now, direction, text));
        }
        _owner.NotifyChanged(this);
    }
}

public sealed class SessionLog
{
    private const int MaxSessions = 500;
    private readonly Lock _lock = new();
    private readonly LinkedList<ProtocolSession> _sessions = new();
    private int _nextId;

    public event Action<ProtocolSession?>? Changed;

    public event Action<string>? Activity;

    public ProtocolSession Start(string protocol, int port, string client, bool secure)
    {
        var session = new ProtocolSession(this, Interlocked.Increment(ref _nextId), protocol, port, client, secure);
        lock (_lock)
        {
            _sessions.AddFirst(session);
            while (_sessions.Count > MaxSessions)
                _sessions.RemoveLast();
        }
        NotifyChanged(session);
        return session;
    }

    public IReadOnlyList<ProtocolSession> List()
    {
        lock (_lock)
            return [.. _sessions];
    }

    public ProtocolSession? Get(int id)
    {
        lock (_lock)
            return _sessions.FirstOrDefault(s => s.Id == id);
    }

    public void Clear()
    {
        lock (_lock)
            _sessions.Clear();
        Changed?.Invoke(null);
    }

    public void Report(string activity) => Activity?.Invoke(activity);

    internal void NotifyChanged(ProtocolSession session) => Changed?.Invoke(session);
}
