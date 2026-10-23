using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FakeServerEmail.Core;

namespace FakeServerEmail.Protocols.Imap;

public sealed class ImapHandler(ServerContext context, bool allowStartTls) : IProtocolHandler
{
    public string Protocol => "IMAP";

    public Task HandleAsync(MailConnection connection, CancellationToken ct) => new ImapSession(context, connection, allowStartTls).RunAsync(ct);
}

internal sealed partial class ImapSession(ServerContext context, MailConnection c, bool allowStartTls)
{
    private const int UnauthenticatedLiteralLimit = 64 * 1024;
    private const int IdleLineLimit = 8192;
    private const int StructureCacheSize = 200;
    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(31);
    private static readonly TimeSpan IdleKeepAlive = TimeSpan.FromMinutes(25);

    private enum Access { Any, Authenticated, Selected }

    private readonly record struct Call(string Tag, string Name, ImapTokenizer Args, bool Uid, CancellationToken Ct);

    private sealed record Command(Access Access, bool AllowsUid, Func<ImapSession, Call, Task<bool>> Run)
    {
        public static Command Of(Access access, Action<ImapSession, Call> run, bool allowsUid = false) =>
            new(access, allowsUid, (session, call) =>
            {
                run(session, call);
                return Task.FromResult(true);
            });

        public static Command OfAsync(Access access, Func<ImapSession, Call, Task> run, bool allowsUid = false) =>
            new(access, allowsUid, async (session, call) =>
            {
                await run(session, call);
                return true;
            });
    }

    private static readonly FrozenDictionary<string, Command> Commands = new Dictionary<string, Command>
    {
        ["CAPABILITY"] = Command.Of(Access.Any, static (s, call) => s.Capability(call.Tag)),
        ["NOOP"] = Command.Of(Access.Any, static (s, call) => s.Noop(call.Tag)),
        ["LOGOUT"] = new(Access.Any, false, static (s, call) => Task.FromResult(s.Logout(call.Tag))),
        ["ID"] = Command.Of(Access.Any, static (s, call) => s.Id(call.Tag)),
        ["STARTTLS"] = Command.OfAsync(Access.Any, static (s, call) => s.StartTlsAsync(call.Tag, call.Ct)),
        ["LOGIN"] = Command.Of(Access.Any, static (s, call) => s.Login(call.Tag, call.Args)),
        ["AUTHENTICATE"] = Command.OfAsync(Access.Any, static (s, call) => s.AuthenticateAsync(call.Tag, call.Args, call.Ct)),
        ["SELECT"] = Command.Of(Access.Authenticated, static (s, call) => s.Select(call.Tag, call.Args, false)),
        ["EXAMINE"] = Command.Of(Access.Authenticated, static (s, call) => s.Select(call.Tag, call.Args, true)),
        ["CREATE"] = Command.Of(Access.Authenticated, static (s, call) => s.Create(call.Tag, call.Args)),
        ["DELETE"] = Command.Of(Access.Authenticated, static (s, call) => s.Delete(call.Tag, call.Args)),
        ["RENAME"] = Command.Of(Access.Authenticated, static (s, call) => s.Rename(call.Tag, call.Args)),
        ["SUBSCRIBE"] = Command.Of(Access.Authenticated, static (s, call) => s.Subscribe(call.Tag, call.Name, call.Args)),
        ["UNSUBSCRIBE"] = Command.Of(Access.Authenticated, static (s, call) => s.Subscribe(call.Tag, call.Name, call.Args)),
        ["LIST"] = Command.Of(Access.Authenticated, static (s, call) => s.List(call.Tag, call.Args, call.Name)),
        ["LSUB"] = Command.Of(Access.Authenticated, static (s, call) => s.List(call.Tag, call.Args, call.Name)),
        ["STATUS"] = Command.Of(Access.Authenticated, static (s, call) => s.Status(call.Tag, call.Args)),
        ["APPEND"] = Command.Of(Access.Authenticated, static (s, call) => s.Append(call.Tag, call.Args)),
        ["NAMESPACE"] = Command.Of(Access.Authenticated, static (s, call) => s.Namespace(call.Tag)),
        ["ENABLE"] = Command.Of(Access.Authenticated, static (s, call) => s.Enable(call.Tag)),
        ["IDLE"] = new(Access.Authenticated, false, static (s, call) => s.IdleAsync(call.Tag, call.Ct)),
        ["CHECK"] = Command.Of(Access.Selected, static (s, call) => s.Check(call.Tag)),
        ["CLOSE"] = Command.Of(Access.Selected, static (s, call) => s.Close(call.Tag)),
        ["UNSELECT"] = Command.Of(Access.Selected, static (s, call) => s.Unselect(call.Tag)),
        ["EXPUNGE"] = Command.Of(Access.Selected, static (s, call) => s.Expunge(call.Tag, call.Args, call.Uid), allowsUid: true),
        ["SEARCH"] = Command.Of(Access.Selected, static (s, call) => s.Search(call.Tag, call.Args, call.Uid), allowsUid: true),
        ["FETCH"] = Command.OfAsync(Access.Selected, static (s, call) => s.FetchAsync(call.Tag, call.Args, call.Uid, call.Ct), allowsUid: true),
        ["STORE"] = Command.Of(Access.Selected, static (s, call) => s.Store(call.Tag, call.Args, call.Uid), allowsUid: true),
        ["COPY"] = Command.Of(Access.Selected, static (s, call) => s.CopyOrMove(call.Tag, call.Args, call.Uid, false), allowsUid: true),
        ["MOVE"] = Command.Of(Access.Selected, static (s, call) => s.CopyOrMove(call.Tag, call.Args, call.Uid, true), allowsUid: true),
        ["UID"] = new(Access.Selected, false, static (s, call) => s.UidAsync(call)),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private sealed class Selected
    {
        public required string Name { get; set; }
        public bool ReadOnly { get; init; }
        public List<uint> Uids { get; } = [];
        public Dictionary<uint, string> Ids { get; } = [];
        public Dictionary<uint, string> FlagState { get; } = [];
    }

    private static readonly string[] SpecialUse = ["Sent", "Drafts", "Trash", "Junk", "Archive"];

    private readonly ProtocolSession _log = c.Log!;
    private readonly Dictionary<string, MimeNode> _structureCache = [];
    private TaskCompletionSource _changeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _user;
    private Selected? _selected;

    private string Capabilities
    {
        get
        {
            var sb = new StringBuilder("IMAP4rev1 LITERAL+ SASL-IR ID ENABLE IDLE NAMESPACE UIDPLUS MOVE UNSELECT CHILDREN SPECIAL-USE");
            if (_user is null)
            {
                var settings = context.Settings.Current;
                foreach (var m in settings.AuthMechanisms)
                    sb.Append(" AUTH=").Append(m);
                if (c.CanStartTls(allowStartTls, settings))
                    sb.Append(" STARTTLS");
            }
            return sb.ToString();
        }
    }

    private long LiteralLimit => _user is null ? UnauthenticatedLiteralLimit : context.Settings.Current.MaxMessageBytes();

    private string LiteralLimitText => _user is null
        ? $"Dados grandes demais antes do login (limite de {UnauthenticatedLiteralLimit / 1024} KB)"
        : $"Dados grandes demais (limite de {context.Settings.Current.MaxMessageSizeMb} MB)";

    public async Task RunAsync(CancellationToken ct)
    {
        c.IdleTimeout = SessionIdleTimeout;
        context.Store.Changed += OnStoreChanged;
        try
        {
            await c.SendLineAsync($"* OK [CAPABILITY {Capabilities}] Fake Server Email IMAP4rev1 pronto", ct);
            while (true)
            {
                ImapCommand? command;
                try
                {
                    command = await ImapCommand.ReadAsync(c, () => c.SendLineAsync("+ Pronto para receber", ct), LiteralLimit, ct);
                }
                catch (LineTooLongException)
                {
                    await c.SendLineAsync("* BYE Linha muito longa", ct);
                    return;
                }
                catch (ImapLiteralTooBigException ex)
                {
                    _log.FromClient($"{ex.Tag} [literal de {ex.Size} bytes recusado]");
                    if (!ex.Synchronizing)
                    {
                        await c.SendLineAsync($"* BYE [TOOBIG] {LiteralLimitText}", ct);
                        return;
                    }
                    await c.SendLineAsync($"{ex.Tag} NO [TOOBIG] {LiteralLimitText}", ct);
                    continue;
                }
                if (command is null)
                    return;

                var t = new ImapTokenizer(command.Text, command.Literals);
                string tag;
                string? name;
                try
                {
                    var tagToken = t.Next();
                    if (tagToken is null)
                        continue;
                    tag = tagToken.Text;
                    name = t.Next()?.Upper;
                }
                catch (ImapParseException)
                {
                    _log.FromClient(command.LogText);
                    await c.SendLineAsync("* BAD Comando ilegível", ct);
                    continue;
                }

                _log.FromClient(name switch
                {
                    "LOGIN" => $"{tag} LOGIN ********",
                    "AUTHENTICATE" => $"{tag} AUTHENTICATE {Sasl.MaskArguments(t.Remaining)}".TrimEnd(),
                    _ => command.LogText,
                });
                if (name is null)
                {
                    await c.SendLineAsync($"{tag} BAD Comando ausente", ct);
                    continue;
                }

                var keepGoing = true;
                try
                {
                    keepGoing = await DispatchAsync(tag, name, t, ct);
                }
                catch (ImapParseException ex)
                {
                    c.WriteLine($"{tag} BAD {ex.Message}");
                }
                await c.FlushAsync(ct);
                if (!keepGoing)
                    return;
            }
        }
        finally
        {
            context.Store.Changed -= OnStoreChanged;
        }
    }

    private void OnStoreChanged(StoreChange change) =>
        Interlocked.Exchange(ref _changeSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    private void Untagged(string text) => c.WriteLine("* " + text);

    private void Ok(string tag, string text) => c.WriteLine($"{tag} OK {text}");

    private void No(string tag, string text) => c.WriteLine($"{tag} NO {text}");

    private void Bad(string tag, string text) => c.WriteLine($"{tag} BAD {text}");

    private async Task<bool> DispatchAsync(string tag, string name, ImapTokenizer t, CancellationToken ct)
    {
        if (!Commands.TryGetValue(name, out var command))
        {
            Bad(tag, "Comando desconhecido");
            return true;
        }
        if (command.Access != Access.Any && _user is null)
        {
            Bad(tag, "Autentique-se primeiro");
            return true;
        }
        if (command.Access == Access.Selected && _selected is null)
        {
            Bad(tag, "Nenhuma pasta selecionada");
            return true;
        }
        return await command.Run(this, new Call(tag, name, t, false, ct));
    }

    private Task<bool> UidAsync(Call call)
    {
        var name = call.Args.Require("comando UID").Upper;
        if (!Commands.TryGetValue(name, out var command) || !command.AllowsUid)
        {
            Bad(call.Tag, "Comando desconhecido");
            return Task.FromResult(true);
        }
        return command.Run(this, call with { Name = name, Uid = true });
    }

    private void Capability(string tag)
    {
        Untagged("CAPABILITY " + Capabilities);
        Ok(tag, "CAPABILITY concluído");
    }

    private void Noop(string tag)
    {
        Sync(true);
        Ok(tag, "NOOP concluído");
    }

    private bool Logout(string tag)
    {
        Untagged("BYE Até logo");
        Ok(tag, "LOGOUT concluído");
        return false;
    }

    private void Id(string tag)
    {
        Untagged("ID (\"name\" \"Fake Server Email\" \"version\" \"1.0\" \"vendor\" \"Fake Server Email\")");
        Ok(tag, "ID concluído");
    }

    private async Task StartTlsAsync(string tag, CancellationToken ct)
    {
        if (!c.CanStartTls(allowStartTls, context.Settings.Current) || _user is not null)
        {
            Bad(tag, "STARTTLS não disponível");
            return;
        }
        await c.SendLineAsync($"{tag} OK Iniciando TLS", ct);
        await c.UpgradeToTlsAsync(ct);
    }

    private void Login(string tag, ImapTokenizer t)
    {
        if (_user is not null)
        {
            Bad(tag, "Já autenticado");
            return;
        }
        var user = t.RequireAString("usuário");
        var password = t.RequireAString("senha");
        var result = context.Authenticator.ValidatePassword(user, password);
        if (!result.Success)
        {
            _log.Info("Falha na autenticação: " + result.Error);
            No(tag, "[AUTHENTICATIONFAILED] " + result.Error);
            return;
        }
        SetUser(result.User ?? user, "LOGIN");
        Ok(tag, $"[CAPABILITY {Capabilities}] LOGIN concluído");
    }

    private async Task AuthenticateAsync(string tag, ImapTokenizer t, CancellationToken ct)
    {
        if (_user is not null)
        {
            Bad(tag, "Já autenticado");
            return;
        }
        var mechanism = t.Require("mecanismo").Text;
        var initial = t.Next()?.Text;
        if (!Sasl.IsOffered(context.Settings.Current, mechanism))
        {
            No(tag, "[CANNOT] Mecanismo de autenticação não suportado");
            return;
        }

        var outcome = await Sasl.ExchangeAsync(c, context, mechanism, initial, "+ ", ct);
        if (outcome.Success)
        {
            SetUser(outcome.User ?? "", outcome.Mechanism);
            Ok(tag, $"[CAPABILITY {Capabilities}] Autenticado");
        }
        else if (outcome.Cancelled)
        {
            Bad(tag, "Autenticação cancelada");
        }
        else
        {
            No(tag, "[AUTHENTICATIONFAILED] Falha na autenticação: " + outcome.Error);
        }
    }

    private void SetUser(string user, string mechanism)
    {
        _user = user;
        _log.MarkAuthenticated(user, mechanism);
    }

    private void Subscribe(string tag, string name, ImapTokenizer t)
    {
        t.RequireAString("pasta");
        Ok(tag, $"{name} concluído");
    }

    private void Namespace(string tag)
    {
        Untagged("NAMESPACE ((\"\" \"/\")) NIL NIL");
        Ok(tag, "NAMESPACE concluído");
    }

    private void Enable(string tag)
    {
        Untagged("ENABLED");
        Ok(tag, "ENABLE concluído");
    }

    private void Check(string tag)
    {
        Sync(true);
        Ok(tag, "CHECK concluído");
    }

    private void Close(string tag)
    {
        if (!_selected!.ReadOnly)
            context.Store.Delete(Entries(null, false).Where(e => HasDeleted(e.Id)).Select(e => e.Id).ToList());
        _selected = null;
        Ok(tag, "CLOSE concluído");
    }

    private void Unselect(string tag)
    {
        _selected = null;
        Ok(tag, "UNSELECT concluído");
    }

    private List<StoredMessage> View(string folder)
    {
        var separate = context.Settings.Current.SeparateMailboxesByRecipient;
        return context.Store.List(folder).Where(m => MessageStore.IsVisibleTo(m, _user, separate)).OrderBy(m => m.Uid).ToList();
    }

    private static string FlagKey(StoredMessage m) => string.Join(' ', m.Flags.Order(StringComparer.OrdinalIgnoreCase));

    private void Select(string tag, ImapTokenizer t, bool examine)
    {
        _selected = null;
        var name = ImapUtf7.Decode(t.RequireAString("pasta"));
        var folder = context.Store.ResolveFolder(name);
        if (folder is null)
        {
            No(tag, "[NONEXISTENT] Pasta não existe");
            return;
        }

        var view = View(folder);
        var selected = new Selected { Name = folder, ReadOnly = examine };
        foreach (var m in view)
        {
            selected.Uids.Add(m.Uid);
            selected.Ids[m.Uid] = m.Id;
            selected.FlagState[m.Uid] = FlagKey(m);
        }
        _selected = selected;

        Untagged("FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)");
        Untagged(examine ? "OK [PERMANENTFLAGS ()] Somente leitura" : "OK [PERMANENTFLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft \\*)] Flags permitidas");
        Untagged($"{view.Count} EXISTS");
        Untagged("0 RECENT");
        var firstUnseen = view.FindIndex(m => !m.HasFlag(ImapFlags.Seen));
        if (firstUnseen >= 0)
            Untagged($"OK [UNSEEN {firstUnseen + 1}] Primeira mensagem não lida");
        Untagged($"OK [UIDVALIDITY {context.Store.UidValidity}] UIDs válidos");
        Untagged($"OK [UIDNEXT {context.Store.NextUid}] Próximo UID");
        Ok(tag, $"[{(examine ? "READ-ONLY" : "READ-WRITE")}] {(examine ? "EXAMINE" : "SELECT")} concluído");
    }

    private void Sync(bool allowExpunge)
    {
        if (_selected is null)
            return;

        var view = View(_selected.Name);
        var current = view.ToDictionary(m => m.Uid);

        if (allowExpunge)
        {
            for (var i = _selected.Uids.Count - 1; i >= 0; i--)
            {
                var uid = _selected.Uids[i];
                if (current.ContainsKey(uid))
                    continue;
                Untagged($"{i + 1} EXPUNGE");
                RemoveAt(i);
            }
        }

        var maxKnown = _selected.Uids.Count > 0 ? _selected.Uids.Max() : 0;
        var added = view.Where(m => m.Uid > maxKnown).ToList();
        foreach (var m in added)
        {
            _selected.Uids.Add(m.Uid);
            _selected.Ids[m.Uid] = m.Id;
            _selected.FlagState[m.Uid] = FlagKey(m);
        }
        if (added.Count > 0)
        {
            Untagged($"{_selected.Uids.Count} EXISTS");
            Untagged("0 RECENT");
        }

        for (var i = 0; i < _selected.Uids.Count; i++)
        {
            var uid = _selected.Uids[i];
            if (!current.TryGetValue(uid, out var m))
                continue;
            var key = FlagKey(m);
            if (_selected.FlagState.TryGetValue(uid, out var previous) && previous == key)
                continue;
            _selected.FlagState[uid] = key;
            Untagged($"{i + 1} FETCH (UID {uid} FLAGS {ImapFetch.FormatFlags(m.Flags)})");
        }
    }

    private void RemoveAt(int index)
    {
        var uid = _selected!.Uids[index];
        _selected.Uids.RemoveAt(index);
        _selected.Ids.Remove(uid);
        _selected.FlagState.Remove(uid);
    }

    private sealed record Entry(int Sequence, uint Uid, string Id);

    private List<Entry> Entries(SequenceSet? set, bool byUid)
    {
        var selected = _selected!;
        var result = new List<Entry>();
        var maxSeq = selected.Uids.Count;
        var maxUid = selected.Uids.Count > 0 ? selected.Uids[^1] : 0;
        for (var i = 0; i < selected.Uids.Count; i++)
        {
            var uid = selected.Uids[i];
            if (set is not null && !(byUid ? set.Contains(uid, maxUid) : set.Contains(i + 1, maxSeq)))
                continue;
            result.Add(new Entry(i + 1, uid, selected.Ids[uid]));
        }
        return result;
    }

    private bool HasDeleted(string id) => context.Store.Get(id)?.HasFlag(ImapFlags.Deleted) == true;

    private static SequenceSet ParseSet(ImapTokenizer t)
    {
        var text = t.Require("conjunto de mensagens").Text;
        if (!SequenceSet.TryParse(text, out var set))
            throw new ImapParseException($"Conjunto de mensagens inválido: {text}");
        return set;
    }

    private async Task FetchAsync(string tag, ImapTokenizer t, bool byUid, CancellationToken ct)
    {
        var set = ParseSet(t);
        var items = ImapFetch.ParseItems(t.Require("itens do FETCH"));
        if (byUid && !items.Any(i => i.Kind == FetchKind.Uid))
            items.Insert(0, new FetchItem { Kind = FetchKind.Uid });

        var needsBody = items.Any(i => i.NeedsBody);
        var setsSeen = !_selected!.ReadOnly && items.Any(ImapFetch.SetsSeen);
        var labels = string.Join(' ', items.Select(i => i.Kind == FetchKind.Section ? i.Label : i.Kind.ToString().ToUpperInvariant()));
        var seen = new List<Entry>();

        try
        {
            foreach (var entry in Entries(set, byUid))
            {
                var message = context.Store.Get(entry.Id);
                if (message is null)
                    continue;

                byte[]? raw = null;
                MimeNode? root = null;
                if (needsBody)
                {
                    raw = context.Store.GetRaw(entry.Id);
                    if (raw is null)
                        continue;
                    root = Structure(entry.Id, raw);
                }

                var markSeen = setsSeen && !message.HasFlag(ImapFlags.Seen);
                if (markSeen)
                    seen.Add(entry);

                using var w = new ImapWriter();
                WriteFetchResponse(w, entry, message, items, raw, root, markSeen);
                c.Write(w.Written);
                _log.FromServer($"* {entry.Sequence} FETCH ({labels}) [{w.Length} bytes]");
                await c.FlushIfLargeAsync(ct);
            }
        }
        finally
        {
            MarkSeen(seen);
        }

        Sync(byUid);
        Ok(tag, (byUid ? "UID " : "") + "FETCH concluído");
    }

    private void MarkSeen(List<Entry> entries)
    {
        if (entries.Count == 0)
            return;
        context.Store.SetSeen(entries.Select(e => e.Id), true);
        foreach (var entry in entries)
        {
            if (context.Store.Get(entry.Id) is { } updated)
                _selected!.FlagState[entry.Uid] = FlagKey(updated);
        }
    }

    private static void WriteFetchResponse(ImapWriter w, Entry entry, StoredMessage message, List<FetchItem> items, byte[]? raw, MimeNode? root, bool markSeen)
    {
        List<string> flags = markSeen ? [.. message.Flags, ImapFlags.Seen] : message.Flags;
        w.Raw($"* {entry.Sequence} FETCH (");
        var flagsWritten = false;
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
                w.Space();
            WriteFetchItem(w, items[i], entry, message, flags, raw, root);
            flagsWritten |= items[i].Kind == FetchKind.Flags;
        }
        if (markSeen && !flagsWritten)
        {
            if (items.Count > 0)
                w.Space();
            w.Raw("FLAGS " + ImapFetch.FormatFlags(flags));
        }
        w.Raw(")\r\n");
    }

    private static void WriteFetchItem(ImapWriter w, FetchItem item, Entry entry, StoredMessage message, List<string> flags, byte[]? raw, MimeNode? root)
    {
        switch (item.Kind)
        {
            case FetchKind.Uid:
                w.Raw("UID ").Number(entry.Uid);
                break;
            case FetchKind.Flags:
                w.Raw("FLAGS " + ImapFetch.FormatFlags(flags));
                break;
            case FetchKind.InternalDate:
                w.Raw("INTERNALDATE ").NString(ImapFetch.FormatInternalDate(message.ReceivedAt));
                break;
            case FetchKind.Size:
                w.Raw("RFC822.SIZE ").Number(message.Size);
                break;
            case FetchKind.Envelope:
                w.Raw("ENVELOPE ");
                ImapFetch.WriteEnvelope(w, root!);
                break;
            case FetchKind.BodyStructure:
                w.Raw("BODYSTRUCTURE ");
                ImapFetch.WriteBodyStructure(w, root!, true);
                break;
            case FetchKind.Body:
                w.Raw("BODY ");
                ImapFetch.WriteBodyStructure(w, root!, false);
                break;
            case FetchKind.Section:
                var data = ImapFetch.ExtractSection(raw!, root!, item);
                w.Raw(item.Label + " ");
                if (data is { } section)
                    w.Literal(section.Span);
                else
                    w.Raw("NIL");
                break;
        }
    }

    private MimeNode Structure(string id, byte[] raw)
    {
        if (_structureCache.TryGetValue(id, out var cached))
            return cached;
        var node = MimeStructure.Parse(raw);
        if (_structureCache.Count > StructureCacheSize)
            _structureCache.Clear();
        _structureCache[id] = node;
        return node;
    }

    private void Store(string tag, ImapTokenizer t, bool byUid)
    {
        var set = ParseSet(t);
        if (t.PeekIs('('))
            t.Next();
        var action = t.Require("ação do STORE").Upper;
        var silent = action.EndsWith(".SILENT", StringComparison.Ordinal);
        var mode = action.Replace(".SILENT", "");
        if (mode is not ("FLAGS" or "+FLAGS" or "-FLAGS"))
            throw new ImapParseException($"Ação de STORE inválida: {action}");

        var flags = new List<string>();
        while (!t.AtEnd)
        {
            var token = t.Next()!;
            if (token.Type == ImapTokenType.List)
                flags.AddRange(token.Items.Select(i => i.Text));
            else
                flags.Add(token.Text);
        }
        flags = flags.Where(f => !f.Equals("\\Recent", StringComparison.OrdinalIgnoreCase)).Select(NormalizeFlag).ToList();

        if (_selected!.ReadOnly)
        {
            No(tag, "[READ-ONLY] Pasta aberta somente para leitura");
            return;
        }

        var entries = Entries(set, byUid);
        context.Store.UpdateFlags(entries.Select(e => e.Id), existing => mode switch
        {
            "FLAGS" => [.. flags],
            "+FLAGS" => [.. existing, .. flags],
            _ => existing.Where(f => !flags.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList(),
        });

        foreach (var entry in entries)
        {
            var message = context.Store.Get(entry.Id);
            if (message is null)
                continue;
            _selected.FlagState[entry.Uid] = FlagKey(message);
            if (!silent)
                Untagged($"{entry.Sequence} FETCH (FLAGS {ImapFetch.FormatFlags(message.Flags)}{(byUid ? $" UID {entry.Uid}" : "")})");
        }

        Sync(byUid);
        Ok(tag, (byUid ? "UID " : "") + "STORE concluído");
    }

    private static string NormalizeFlag(string flag) =>
        ImapFlags.System.FirstOrDefault(f => f.Equals(flag, StringComparison.OrdinalIgnoreCase)) ?? flag;

    private void CopyOrMove(string tag, ImapTokenizer t, bool byUid, bool move)
    {
        var set = ParseSet(t);
        var destination = ImapUtf7.Decode(t.RequireAString("pasta de destino"));
        var folder = context.Store.ResolveFolder(destination);
        if (folder is null)
        {
            No(tag, "[TRYCREATE] Pasta de destino não existe");
            return;
        }
        if (move && _selected!.ReadOnly)
        {
            No(tag, "[READ-ONLY] Pasta aberta somente para leitura");
            return;
        }

        var entries = Entries(set, byUid);
        var source = new List<uint>();
        var target = new List<uint>();

        if (move)
        {
            var moved = context.Store.MoveTo(entries.Select(e => e.Id), folder, null);
            foreach (var entry in entries)
            {
                if (!moved.TryGetValue(entry.Id, out var newUid))
                    continue;
                source.Add(entry.Uid);
                target.Add(newUid);
            }
            if (source.Count > 0)
                Untagged($"OK [COPYUID {context.Store.UidValidity} {FormatSet(source)} {FormatSet(target)}] Mensagens movidas");
            foreach (var entry in entries.Where(e => moved.ContainsKey(e.Id)).OrderByDescending(e => e.Sequence))
            {
                Untagged($"{entry.Sequence} EXPUNGE");
                RemoveAt(entry.Sequence - 1);
            }
            Sync(true);
            Ok(tag, (byUid ? "UID " : "") + "MOVE concluído");
            return;
        }

        foreach (var entry in entries)
        {
            var copy = context.Store.Copy(entry.Id, folder, null);
            if (copy is null)
                continue;
            source.Add(entry.Uid);
            target.Add(copy.Uid);
        }
        Sync(byUid);
        Ok(tag, source.Count > 0
            ? $"[COPYUID {context.Store.UidValidity} {FormatSet(source)} {FormatSet(target)}] {(byUid ? "UID " : "")}COPY concluído"
            : $"{(byUid ? "UID " : "")}COPY concluído");
    }

    private static string FormatSet(List<uint> uids)
    {
        var parts = new List<string>();
        var i = 0;
        while (i < uids.Count)
        {
            var j = i;
            while (j + 1 < uids.Count && uids[j + 1] == uids[j] + 1)
                j++;
            parts.Add(i == j ? uids[i].ToString(CultureInfo.InvariantCulture) : $"{uids[i]}:{uids[j]}");
            i = j + 1;
        }
        return string.Join(',', parts);
    }

    private void Expunge(string tag, ImapTokenizer t, bool byUid)
    {
        var set = byUid ? ParseSet(t) : null;
        if (_selected!.ReadOnly)
        {
            No(tag, "[READ-ONLY] Pasta aberta somente para leitura");
            return;
        }
        var targets = Entries(set, byUid).Where(e => HasDeleted(e.Id)).ToList();
        context.Store.Delete(targets.Select(e => e.Id).ToList());
        foreach (var entry in targets.OrderByDescending(e => e.Sequence))
        {
            Untagged($"{entry.Sequence} EXPUNGE");
            RemoveAt(entry.Sequence - 1);
        }
        Sync(true);
        Ok(tag, (byUid ? "UID " : "") + "EXPUNGE concluído");
    }

    private void Search(string tag, ImapTokenizer t, bool byUid)
    {
        var tokens = new List<ImapToken>();
        while (!t.AtEnd)
            tokens.Add(t.Next()!);
        var predicate = ImapSearch.Parse(tokens);

        var entries = Entries(null, false);
        var maxSeq = entries.Count;
        var maxUid = entries.Count > 0 ? entries[^1].Uid : 0;
        var results = new List<long>();
        foreach (var entry in entries)
        {
            var message = context.Store.Get(entry.Id);
            if (message is null)
                continue;
            var target = new SearchTarget(message, entry.Sequence, maxSeq, maxUid, () => context.Store.GetRaw(entry.Id));
            if (predicate(target))
                results.Add(byUid ? entry.Uid : entry.Sequence);
        }

        Untagged("SEARCH" + (results.Count > 0 ? " " + string.Join(' ', results) : ""));
        Sync(byUid);
        Ok(tag, (byUid ? "UID " : "") + "SEARCH concluído");
    }

    private void Create(string tag, ImapTokenizer t)
    {
        var name = ImapUtf7.Decode(t.RequireAString("pasta")).TrimEnd('/');
        if (name.Length == 0 || name.Equals(MessageStore.Inbox, StringComparison.OrdinalIgnoreCase) || !context.Store.CreateFolder(name))
        {
            No(tag, "[ALREADYEXISTS] Pasta já existe");
            return;
        }
        Ok(tag, "CREATE concluído");
    }

    private void Delete(string tag, ImapTokenizer t)
    {
        var name = ImapUtf7.Decode(t.RequireAString("pasta"));
        var folder = context.Store.ResolveFolder(name);
        if (folder is null)
        {
            No(tag, "[NONEXISTENT] Pasta não existe");
            return;
        }
        if (folder == MessageStore.Inbox)
        {
            No(tag, "Não é possível excluir a INBOX");
            return;
        }
        if (_selected?.Name.Equals(folder, StringComparison.OrdinalIgnoreCase) == true)
            _selected = null;
        context.Store.DeleteFolder(folder);
        Ok(tag, "DELETE concluído");
    }

    private void Rename(string tag, ImapTokenizer t)
    {
        var from = ImapUtf7.Decode(t.RequireAString("pasta de origem"));
        var to = ImapUtf7.Decode(t.RequireAString("novo nome")).TrimEnd('/');
        if (to.Length == 0)
        {
            No(tag, "Nome de pasta inválido");
            return;
        }
        var folder = context.Store.ResolveFolder(from);
        if (folder is null)
        {
            No(tag, "[NONEXISTENT] Pasta não existe");
            return;
        }
        if (!context.Store.RenameFolder(folder, to))
        {
            No(tag, "[ALREADYEXISTS] Já existe uma pasta com esse nome");
            return;
        }
        if (_selected?.Name.Equals(folder, StringComparison.OrdinalIgnoreCase) == true && folder != MessageStore.Inbox)
            _selected.Name = to;
        Ok(tag, "RENAME concluído");
    }

    private void List(string tag, ImapTokenizer t, string verb)
    {
        var first = t.Require("referência");
        var specialOnly = false;
        string reference;
        if (first.Type == ImapTokenType.List)
        {
            specialOnly = first.Items.Any(i => i.Upper == "SPECIAL-USE");
            reference = t.RequireAString("referência");
        }
        else
        {
            reference = first.Text;
        }

        var patternToken = t.Require("padrão");
        var patterns = patternToken.Type == ImapTokenType.List ? patternToken.Items.Select(i => i.Text).ToList() : [patternToken.Text];

        if (patterns.Count == 1 && patterns[0].Length == 0)
        {
            Untagged($"{verb} (\\Noselect) \"/\" \"\"");
            Ok(tag, $"{verb} concluído");
            return;
        }

        var decoded = patterns.Select(p => ImapUtf7.Decode(reference + p)).ToList();
        var folders = context.Store.Folders;
        foreach (var folder in folders)
        {
            if (!decoded.Any(p => ImapSyntax.MatchesMailboxPattern(p, folder)))
                continue;
            var attributes = new List<string>
            {
                folders.Any(f => f.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) ? "\\HasChildren" : "\\HasNoChildren",
            };
            var special = SpecialUse.FirstOrDefault(s => s.Equals(folder, StringComparison.OrdinalIgnoreCase));
            if (special is not null)
                attributes.Add("\\" + special);
            else if (specialOnly)
                continue;

            using var w = new ImapWriter();
            w.Raw($"{verb} ({string.Join(' ', attributes)}) \"/\" ").NString(ImapUtf7.Encode(folder));
            Untagged(Encoding.UTF8.GetString(w.Written));
        }
        Ok(tag, $"{verb} concluído");
    }

    private void Status(string tag, ImapTokenizer t)
    {
        var name = ImapUtf7.Decode(t.RequireAString("pasta"));
        var folder = context.Store.ResolveFolder(name);
        if (folder is null)
        {
            No(tag, "[NONEXISTENT] Pasta não existe");
            return;
        }
        var itemsToken = t.Require("itens do STATUS");
        var items = itemsToken.Type == ImapTokenType.List ? itemsToken.Items.Select(i => i.Upper).ToList() : [itemsToken.Upper];
        var view = View(folder);
        var values = new List<string>();
        foreach (var item in items)
        {
            switch (item)
            {
                case "MESSAGES": values.Add($"MESSAGES {view.Count}"); break;
                case "RECENT": values.Add("RECENT 0"); break;
                case "UIDNEXT": values.Add($"UIDNEXT {context.Store.NextUid}"); break;
                case "UIDVALIDITY": values.Add($"UIDVALIDITY {context.Store.UidValidity}"); break;
                case "UNSEEN": values.Add($"UNSEEN {view.Count(m => !m.HasFlag(ImapFlags.Seen))}"); break;
                case "SIZE": values.Add($"SIZE {view.Sum(m => m.Size)}"); break;
            }
        }
        using var w = new ImapWriter();
        w.Raw("STATUS ").NString(ImapUtf7.Encode(folder)).Raw($" ({string.Join(' ', values)})");
        Untagged(Encoding.UTF8.GetString(w.Written));
        Ok(tag, "STATUS concluído");
    }

    private void Append(string tag, ImapTokenizer t)
    {
        var name = ImapUtf7.Decode(t.RequireAString("pasta"));
        var folder = context.Store.ResolveFolder(name);

        var flags = new List<string>();
        DateTimeOffset? date = null;
        var token = t.Require("mensagem");
        if (token.Type == ImapTokenType.List)
        {
            flags = token.Items.Select(i => NormalizeFlag(i.Text)).Where(f => !f.Equals("\\Recent", StringComparison.OrdinalIgnoreCase)).ToList();
            token = t.Require("mensagem");
        }
        if (token.Bytes is null && token.Type == ImapTokenType.Quoted)
        {
            date = ParseInternalDate(token.Text);
            token = t.Require("mensagem");
        }

        if (folder is null)
        {
            No(tag, "[TRYCREATE] Pasta não existe");
            return;
        }

        var bytes = token.Bytes ?? Encoding.UTF8.GetBytes(token.Text);
        if (bytes.Length > context.Settings.Current.MaxMessageBytes())
        {
            No(tag, "[TOOBIG] Mensagem muito grande");
            return;
        }

        var stored = context.Store.Add(bytes, new MessageOrigin("IMAP APPEND", null, [], c.RemoteAddress, _user), folder, _user, flags, date);
        _log.Info($"Mensagem adicionada em '{folder}' ({bytes.Length} bytes)");
        Sync(true);
        Ok(tag, $"[APPENDUID {context.Store.UidValidity} {stored.Uid}] APPEND concluído");
    }

    private static DateTimeOffset? ParseInternalDate(string text)
    {
        var match = InternalDateRegex().Match(text.Trim());
        if (!match.Success)
            return null;
        var normalized = $"{match.Groups[1].Value.PadLeft(2, '0')}-{match.Groups[2].Value}-{match.Groups[3].Value} {match.Groups[4].Value}:{match.Groups[5].Value}:{match.Groups[6].Value} {match.Groups[7].Value}{match.Groups[8].Value}:{match.Groups[9].Value}";
        return DateTimeOffset.TryParseExact(normalized, "dd-MMM-yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    [GeneratedRegex(@"^(\d{1,2})-([A-Za-z]{3})-(\d{4}) (\d{2}):(\d{2}):(\d{2}) ([+-])(\d{2})(\d{2})$")]
    private static partial Regex InternalDateRegex();

    private async Task<bool> IdleAsync(string tag, CancellationToken ct)
    {
        await c.SendLineAsync("+ Aguardando novidades (IDLE)", ct);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var read = c.ReadLineAsync(ct, IdleLineLimit);
        var keepAlive = Task.Delay(IdleKeepAlive, idle.Token);
        try
        {
            while (true)
            {
                var changed = Volatile.Read(ref _changeSignal).Task;
                Sync(true);
                await c.FlushAsync(ct);

                var done = await Task.WhenAny(read, changed, keepAlive);
                if (done == read)
                    break;
                if (done == keepAlive)
                {
                    Untagged("OK Ainda conectado");
                    keepAlive = Task.Delay(IdleKeepAlive, idle.Token);
                }
            }
        }
        finally
        {
            await idle.CancelAsync();
        }

        string? line;
        try
        {
            line = await read;
        }
        catch (LineTooLongException)
        {
            await c.SendLineAsync("* BYE Linha muito longa", ct);
            return false;
        }
        if (line is null)
            return false;
        _log.FromClient(line);
        if (line.Trim().Equals("DONE", StringComparison.OrdinalIgnoreCase))
            Ok(tag, "IDLE encerrado");
        else
            Bad(tag, "Esperava DONE");
        return true;
    }
}
