using System.Text.Json;
using MimeKit;

namespace FakeServerEmail.Core;

public sealed class StoredMessage
{
    public string Id { get; set; } = "";
    public uint Uid { get; set; }
    public string Folder { get; set; } = MessageStore.Inbox;
    public string? Owner { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string? EnvelopeFrom { get; set; }
    public List<string> EnvelopeTo { get; set; } = [];
    public string Subject { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public DateTimeOffset? Date { get; set; }
    public long Size { get; set; }
    public bool HasAttachments { get; set; }
    public string Source { get; set; } = "";
    public string? ClientAddress { get; set; }
    public string? AuthenticatedUser { get; set; }
    public List<string> Flags { get; set; } = [];

    public bool HasFlag(string flag) => Flags.Contains(flag, StringComparer.OrdinalIgnoreCase);

    public StoredMessage Snapshot()
    {
        var copy = (StoredMessage)MemberwiseClone();
        copy.EnvelopeTo = [.. EnvelopeTo];
        copy.Flags = [.. Flags];
        return copy;
    }
}

public sealed record MessageOrigin(string Source, string? EnvelopeFrom, IReadOnlyList<string> EnvelopeTo, string? ClientAddress, string? AuthenticatedUser);

public enum StoreChangeKind { Added, Removed, Updated, Folders }

public sealed record StoreChange(StoreChangeKind Kind, IReadOnlyList<string> Ids);

public sealed record MessageCounts(int Messages, int Unread);

public sealed record FolderSummary(string Name, int Total, int Unread);

public sealed class MessageStore : IDisposable
{
    public const string Inbox = "INBOX";
    public const string RecoveredSource = "Recuperada";
    public static readonly IReadOnlyList<string> DefaultFolders = [Inbox, "Sent", "Drafts", "Trash", "Junk"];

    private const int SaveDelayMilliseconds = 400;
    private const int MaxCachedMessageBytes = 16 * 1024 * 1024;
    private const long MaxCacheBytes = 96L * 1024 * 1024;
    private const uint UidGapAfterUncleanShutdown = 1000;

    private sealed class StoreState
    {
        public uint UidValidity { get; set; }
        public uint NextUid { get; set; } = 1;
        public bool CleanShutdown { get; set; }
        public List<string> Folders { get; set; } = [];
        public List<StoredMessage> Messages { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Lock _lock = new();
    private readonly Lock _flushLock = new();
    private readonly string _directory;
    private readonly string _indexPath;
    private readonly List<StoredMessage> _messages = [];
    private readonly Dictionary<string, StoredMessage> _byId = [];
    private readonly List<string> _folders = [];
    private readonly Dictionary<string, byte[]> _rawCache = [];
    private readonly Queue<string> _rawCacheOrder = new();
    private readonly Timer _saveTimer;
    private long _rawCacheBytes;
    private bool _dirty;
    private bool _disposed;

    public MessageStore(string dataDirectory)
    {
        _directory = Path.Combine(dataDirectory, DataFiles.MessagesDirectory);
        Directory.CreateDirectory(_directory);
        _indexPath = Path.Combine(_directory, DataFiles.MessageIndex);
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        Load();
    }

    public event Action<StoreChange>? Changed;

    public uint UidValidity { get; private set; }

    public uint NextUid { get; private set; } = 1;

    public string MessagesDirectory => _directory;

    public StoredMessage Add(byte[] raw, MessageOrigin origin, string folder = Inbox, string? owner = null, IEnumerable<string>? flags = null, DateTimeOffset? internalDate = null)
    {
        var message = new StoredMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            Folder = folder,
            Owner = owner,
            ReceivedAt = internalDate ?? DateTimeOffset.Now,
            EnvelopeFrom = origin.EnvelopeFrom,
            EnvelopeTo = [.. origin.EnvelopeTo],
            Size = raw.LongLength,
            Source = origin.Source,
            ClientAddress = origin.ClientAddress,
            AuthenticatedUser = origin.AuthenticatedUser,
            Flags = flags?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [],
        };
        FillMetadata(message, raw);

        File.WriteAllBytes(RawPath(message.Id), raw);

        lock (_lock)
        {
            message.Uid = NextUid++;
            _messages.Add(message);
            _byId[message.Id] = message;
            if (!_folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                _folders.Add(folder);
            CacheRaw(message.Id, raw);
            MarkDirty();
        }

        Changed?.Invoke(new StoreChange(StoreChangeKind.Added, [message.Id]));
        return message.Snapshot();
    }

    public IReadOnlyList<StoredMessage> List(string? folder = null)
    {
        lock (_lock)
            return _messages.Where(m => folder is null || string.Equals(m.Folder, folder, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Snapshot())
                .ToList();
    }

    public MessageCounts Counts()
    {
        lock (_lock)
            return new MessageCounts(_messages.Count, _messages.Count(m => !m.HasFlag(ImapFlags.Seen)));
    }

    public IReadOnlyList<FolderSummary> FolderSummaries()
    {
        lock (_lock)
            return _folders.Select(f =>
            {
                var inFolder = _messages.Where(m => string.Equals(m.Folder, f, StringComparison.OrdinalIgnoreCase)).ToList();
                return new FolderSummary(f, inFolder.Count, inFolder.Count(m => !m.HasFlag(ImapFlags.Seen)));
            }).ToList();
    }

    public StoredMessage? Get(string id)
    {
        lock (_lock)
            return _byId.TryGetValue(id, out var m) ? m.Snapshot() : null;
    }

    public byte[]? GetRaw(string id)
    {
        lock (_lock)
        {
            if (!_byId.ContainsKey(id))
                return null;
            if (_rawCache.TryGetValue(id, out var cached))
                return cached;
        }

        byte[] raw;
        try
        {
            raw = File.ReadAllBytes(RawPath(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        lock (_lock)
            CacheRaw(id, raw);
        return raw;
    }

    public void Delete(IEnumerable<string> ids)
    {
        var removed = new List<string>();
        lock (_lock)
        {
            foreach (var id in ids)
            {
                if (!_byId.Remove(id))
                    continue;
                RemoveFromCache(id);
                removed.Add(id);
            }
            if (removed.Count > 0)
            {
                var gone = removed.ToHashSet();
                _messages.RemoveAll(m => gone.Contains(m.Id));
                MarkDirty();
            }
        }

        foreach (var id in removed)
        {
            try
            {
                File.Delete(RawPath(id));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (removed.Count > 0)
            Changed?.Invoke(new StoreChange(StoreChangeKind.Removed, removed));
    }

    public void DeleteAll(string? folder = null)
    {
        List<string> ids;
        lock (_lock)
            ids = _messages.Where(m => folder is null || string.Equals(m.Folder, folder, StringComparison.OrdinalIgnoreCase)).Select(m => m.Id).ToList();
        Delete(ids);
    }

    public void UpdateFlags(IEnumerable<string> ids, Func<List<string>, List<string>> change)
    {
        var updated = new List<string>();
        lock (_lock)
        {
            foreach (var id in ids)
            {
                if (!_byId.TryGetValue(id, out var m))
                    continue;
                var next = change([.. m.Flags]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (next.Count == m.Flags.Count && next.All(f => m.HasFlag(f)))
                    continue;
                m.Flags = next;
                updated.Add(id);
            }
            if (updated.Count > 0)
                MarkDirty();
        }

        if (updated.Count > 0)
            Changed?.Invoke(new StoreChange(StoreChangeKind.Updated, updated));
    }

    public void SetSeen(IEnumerable<string> ids, bool seen) =>
        UpdateFlags(ids, flags =>
        {
            flags.RemoveAll(f => f.Equals(ImapFlags.Seen, StringComparison.OrdinalIgnoreCase));
            if (seen)
                flags.Add(ImapFlags.Seen);
            return flags;
        });

    public StoredMessage? Copy(string id, string folder, string? owner)
    {
        var source = Get(id);
        var raw = GetRaw(id);
        if (source is null || raw is null)
            return null;
        var origin = new MessageOrigin(source.Source, source.EnvelopeFrom, source.EnvelopeTo, source.ClientAddress, source.AuthenticatedUser);
        return Add(raw, origin, folder, owner ?? source.Owner, source.Flags, source.ReceivedAt);
    }

    public IReadOnlyList<string> Folders
    {
        get
        {
            lock (_lock)
                return [.. _folders];
        }
    }

    public bool FolderExists(string name)
    {
        lock (_lock)
            return _folders.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public string? ResolveFolder(string name)
    {
        if (name.Equals(Inbox, StringComparison.OrdinalIgnoreCase))
            return Inbox;
        lock (_lock)
            return _folders.FirstOrDefault(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public bool CreateFolder(string name)
    {
        lock (_lock)
        {
            if (_folders.Contains(name, StringComparer.OrdinalIgnoreCase))
                return false;
            _folders.Add(name);
            MarkDirty();
        }
        Changed?.Invoke(new StoreChange(StoreChangeKind.Folders, []));
        return true;
    }

    public bool DeleteFolder(string name)
    {
        if (name.Equals(Inbox, StringComparison.OrdinalIgnoreCase))
            return false;
        List<string> ids;
        lock (_lock)
        {
            var removed = _folders.RemoveAll(f => IsSameOrChild(f, name));
            if (removed == 0)
                return false;
            ids = _messages.Where(m => IsSameOrChild(m.Folder, name)).Select(m => m.Id).ToList();
            MarkDirty();
        }
        Delete(ids);
        Changed?.Invoke(new StoreChange(StoreChangeKind.Folders, []));
        return true;
    }

    public bool RenameFolder(string from, string to)
    {
        var changed = new List<string>();
        lock (_lock)
        {
            if (_folders.Contains(to, StringComparer.OrdinalIgnoreCase))
                return false;
            if (!_folders.Exists(f => f.Equals(from, StringComparison.OrdinalIgnoreCase)))
                return false;

            for (var i = 0; i < _folders.Count; i++)
            {
                if (IsSameOrChild(_folders[i], from))
                    _folders[i] = to + _folders[i][from.Length..];
            }

            foreach (var m in _messages.Where(m => IsSameOrChild(m.Folder, from)))
            {
                m.Folder = to + m.Folder[from.Length..];
                changed.Add(m.Id);
            }

            if (from.Equals(Inbox, StringComparison.OrdinalIgnoreCase))
                _folders.Insert(0, Inbox);
            MarkDirty();
        }

        Changed?.Invoke(new StoreChange(StoreChangeKind.Folders, []));
        if (changed.Count > 0)
            Changed?.Invoke(new StoreChange(StoreChangeKind.Removed, changed));
        return true;
    }

    public Dictionary<string, uint> MoveTo(IEnumerable<string> ids, string folder, string? owner)
    {
        var moved = new Dictionary<string, uint>();
        lock (_lock)
        {
            foreach (var id in ids)
            {
                if (!_byId.TryGetValue(id, out var m))
                    continue;
                m.Folder = folder;
                m.Uid = NextUid++;
                if (owner is not null)
                    m.Owner = owner;
                moved[id] = m.Uid;
            }
            if (!_folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                _folders.Add(folder);
            if (moved.Count > 0)
            {
                _messages.Sort((a, b) => a.Uid.CompareTo(b.Uid));
                MarkDirty();
            }
        }

        if (moved.Count > 0)
            Changed?.Invoke(new StoreChange(StoreChangeKind.Removed, [.. moved.Keys]));
        return moved;
    }

    public static bool IsVisibleTo(StoredMessage m, string? user, bool separateByRecipient)
    {
        if (!separateByRecipient || string.IsNullOrEmpty(user) || !user.Contains('@'))
            return true;
        if (m.Owner is not null)
            return m.Owner.Equals(user, StringComparison.OrdinalIgnoreCase);
        return m.EnvelopeTo.Any(r => r.Equals(user, StringComparison.OrdinalIgnoreCase));
    }

    public void Flush() => Flush(cleanShutdown: false);

    public void Dispose()
    {
        using (var callbacksDone = new ManualResetEvent(false))
        {
            if (_saveTimer.Dispose(callbacksDone))
                callbacksDone.WaitOne();
        }
        lock (_lock)
        {
            _disposed = true;
            _dirty = true;
        }
        Flush(cleanShutdown: true);
    }

    private void Flush(bool cleanShutdown)
    {
        lock (_flushLock)
        {
            StoreState state;
            lock (_lock)
            {
                if (!_dirty)
                    return;
                _dirty = false;
                state = new StoreState
                {
                    UidValidity = UidValidity,
                    NextUid = NextUid,
                    CleanShutdown = cleanShutdown,
                    Folders = [.. _folders],
                    Messages = _messages.Select(m => m.Snapshot()).ToList(),
                };
            }

            try
            {
                var temp = _indexPath + DataFiles.TemporarySuffix;
                File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
                File.Move(temp, _indexPath, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (_lock)
                    MarkDirty();
            }
        }
    }

    private void Load()
    {
        StoreState? state = null;
        var hadIndex = File.Exists(_indexPath);
        try
        {
            if (hadIndex)
                state = JsonSerializer.Deserialize<StoreState>(File.ReadAllText(_indexPath), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            state = null;
        }

        var recovered = state is null;
        state ??= new StoreState();
        if (state.UidValidity == 0)
            state.UidValidity = (uint)Math.Max(1, DateTimeOffset.UtcNow.ToUnixTimeSeconds() % int.MaxValue);

        UidValidity = state.UidValidity;
        foreach (var m in state.Messages.Where(m => File.Exists(RawPath(m.Id))).OrderBy(m => m.Uid))
        {
            m.EnvelopeTo ??= [];
            m.Flags ??= [];
            m.Folder = string.IsNullOrEmpty(m.Folder) ? Inbox : m.Folder;
            _messages.Add(m);
            _byId[m.Id] = m;
        }

        var nextUid = Math.Max(state.NextUid, _messages.Count == 0 ? 1 : _messages.Max(m => m.Uid) + 1);
        NextUid = hadIndex && !recovered && !state.CleanShutdown ? nextUid + UidGapAfterUncleanShutdown : nextUid;

        foreach (var file in Directory.GetFiles(_directory, "*.eml").Select(f => new FileInfo(f)).OrderBy(f => f.CreationTimeUtc))
        {
            var id = Path.GetFileNameWithoutExtension(file.Name);
            if (_byId.ContainsKey(id))
                continue;
            try
            {
                var raw = File.ReadAllBytes(file.FullName);
                var orphan = new StoredMessage { Id = id, Uid = NextUid++, ReceivedAt = file.CreationTime, Size = raw.LongLength, Source = RecoveredSource };
                FillMetadata(orphan, raw);
                _messages.Add(orphan);
                _byId[id] = orphan;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        foreach (var f in DefaultFolders.Concat(state.Folders ?? []).Concat(_messages.Select(m => m.Folder)))
            if (!_folders.Contains(f, StringComparer.OrdinalIgnoreCase))
                _folders.Add(f);

        _dirty = true;
        Flush();
    }

    private static bool IsSameOrChild(string folder, string root) =>
        folder.Equals(root, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    private static void FillMetadata(StoredMessage message, byte[] raw)
    {
        try
        {
            using var stream = new MemoryStream(raw, false);
            var parsed = MimeMessage.Load(stream);
            message.Subject = parsed.Subject ?? "";
            message.From = parsed.From.ToString();
            message.To = parsed.To.ToString();
            message.Cc = parsed.Cc.ToString();
            message.Date = parsed.Headers.Contains(HeaderId.Date) ? parsed.Date : null;
            message.HasAttachments = parsed.Attachments.Any();
            if (message.EnvelopeTo.Count == 0)
                message.EnvelopeTo = parsed.To.Mailboxes.Concat(parsed.Cc.Mailboxes).Concat(parsed.Bcc.Mailboxes).Select(a => a.Address).ToList();
            message.EnvelopeFrom ??= parsed.From.Mailboxes.FirstOrDefault()?.Address;
        }
        catch (Exception)
        {
            message.Subject = "(mensagem não pôde ser interpretada)";
        }
    }

    private string RawPath(string id) => Path.Combine(_directory, id + ".eml");

    private void MarkDirty()
    {
        _dirty = true;
        if (_disposed)
            ThreadPool.UnsafeQueueUserWorkItem(_ => Flush(), null);
        else
            _saveTimer.Change(SaveDelayMilliseconds, Timeout.Infinite);
    }

    private void CacheRaw(string id, byte[] raw)
    {
        if (_rawCache.ContainsKey(id) || raw.Length > MaxCachedMessageBytes)
            return;
        _rawCache[id] = raw;
        _rawCacheOrder.Enqueue(id);
        _rawCacheBytes += raw.Length;
        while (_rawCacheBytes > MaxCacheBytes && _rawCacheOrder.Count > 0)
            RemoveFromCache(_rawCacheOrder.Dequeue());
    }

    private void RemoveFromCache(string id)
    {
        if (_rawCache.Remove(id, out var raw))
            _rawCacheBytes -= raw.Length;
    }
}

public static class ImapFlags
{
    public const string Seen = @"\Seen";
    public const string Answered = @"\Answered";
    public const string Flagged = @"\Flagged";
    public const string Deleted = @"\Deleted";
    public const string Draft = @"\Draft";
    public static readonly IReadOnlyList<string> System = [Answered, Flagged, Deleted, Seen, Draft];
}
