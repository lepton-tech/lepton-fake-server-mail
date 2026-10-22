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

internal sealed class ImapSession(ServerContext context, MailConnection c, bool allowStartTls)
{
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
                foreach (var m in Sasl.Mechanisms)
                    sb.Append(" AUTH=").Append(m);
                if (allowStartTls && !c.IsSecure)
                    sb.Append(" STARTTLS");
            }
            return sb.ToString();
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        c.IdleTimeout = TimeSpan.FromMinutes(31);
        context.Store.Changed += OnStoreChanged;
        try
        {
            await c.SendLineAsync($"* OK [CAPABILITY {Capabilities}] Fake Server Email IMAP4rev1 pronto", ct);
            while (true)
            {
                ImapCommand? command;
                try
                {
                    command = await ImapCommand.ReadAsync(c, () => c.SendLineAsync("+ Pronto para receber", ct), ct);
                }
                catch (LineTooLongException)
                {
                    await c.SendLineAsync("* BYE Linha muito longa", ct);
                    return;
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

                _log.FromClient(name == "LOGIN" ? $"{tag} LOGIN ********" : command.LogText);
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
                catch (Exception ex) when (ex is ImapParseException or FormatException or OverflowException)
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
        switch (name)
        {
            case "CAPABILITY":
                Untagged("CAPABILITY " + Capabilities);
                Ok(tag, "CAPABILITY concluído");
                return true;
            case "NOOP":
                Sync(true);
                Ok(tag, "NOOP concluído");
                return true;
            case "LOGOUT":
                Untagged("BYE Até logo");
                Ok(tag, "LOGOUT concluído");
                return false;
            case "ID":
                Untagged("ID (\"name\" \"Fake Server Email\" \"version\" \"1.0\" \"vendor\" \"Fake Server Email\")");
                Ok(tag, "ID concluído");
                return true;
            case "STARTTLS":
                if (!allowStartTls || c.IsSecure || _user is not null)
                {
                    Bad(tag, "STARTTLS não disponível");
                    return true;
                }
                await c.SendLineAsync($"{tag} OK Iniciando TLS", ct);
                await c.UpgradeToTlsAsync(ct);
                _log.Secure = true;
                return true;
            case "LOGIN":
                Login(tag, t);
                return true;
            case "AUTHENTICATE":
                await AuthenticateAsync(tag, t, ct);
                return true;
        }

        if (_user is null)
        {
            Bad(tag, IsKnownCommand(name) ? "Autentique-se primeiro" : "Comando desconhecido");
            return true;
        }

        switch (name)
        {
            case "SELECT":
            case "EXAMINE":
                Select(tag, t, name == "EXAMINE");
                return true;
            case "CREATE":
                Create(tag, t);
                return true;
            case "DELETE":
                Delete(tag, t);
                return true;
            case "RENAME":
                Rename(tag, t);
                return true;
            case "SUBSCRIBE":
            case "UNSUBSCRIBE":
                t.RequireAString("pasta");
                Ok(tag, $"{name} concluído");
                return true;
            case "LIST":
            case "LSUB":
                List(tag, t, name);
                return true;
            case "STATUS":
                Status(tag, t);
                return true;
            case "APPEND":
                Append(tag, t);
                return true;
            case "NAMESPACE":
                Untagged("NAMESPACE ((\"\" \"/\")) NIL NIL");
                Ok(tag, "NAMESPACE concluído");
                return true;
            case "ENABLE":
                Untagged("ENABLED");
                Ok(tag, "ENABLE concluído");
                return true;
            case "IDLE":
                return await IdleAsync(tag, ct);
        }

        if (_selected is null)
        {
            Bad(tag, IsKnownCommand(name) ? "Nenhuma pasta selecionada" : "Comando desconhecido");
            return true;
        }

        var uid = false;
        if (name == "UID")
        {
            uid = true;
            name = t.Require("comando UID").Upper;
        }

        switch (name)
        {
            case "CHECK" when !uid:
                Sync(true);
                Ok(tag, "CHECK concluído");
                return true;
            case "CLOSE" when !uid:
                if (!_selected.ReadOnly)
                    context.Store.Delete(Entries(null, false).Where(e => HasDeleted(e.Id)).Select(e => e.Id).ToList());
                _selected = null;
                Ok(tag, "CLOSE concluído");
                return true;
            case "UNSELECT" when !uid:
                _selected = null;
                Ok(tag, "UNSELECT concluído");
                return true;
            case "EXPUNGE":
                Expunge(tag, t, uid);
                return true;
            case "SEARCH":
                Search(tag, t, uid);
                return true;
            case "FETCH":
                await FetchAsync(tag, t, uid, ct);
                return true;
            case "STORE":
                Store(tag, t, uid);
                return true;
            case "COPY":
            case "MOVE":
                CopyOrMove(tag, t, uid, name == "MOVE");
                return true;
        }

        Bad(tag, "Comando desconhecido");
        return true;
    }

    private static bool IsKnownCommand(string name) => name is "SELECT" or "EXAMINE" or "CREATE" or "DELETE" or "RENAME" or "SUBSCRIBE"
        or "UNSUBSCRIBE" or "LIST" or "LSUB" or "STATUS" or "APPEND" or "NAMESPACE" or "ENABLE" or "IDLE" or "CHECK" or "CLOSE"
        or "UNSELECT" or "EXPUNGE" or "SEARCH" or "FETCH" or "STORE" or "COPY" or "MOVE" or "UID";

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
        if (!Sasl.IsSupported(mechanism))
        {
            No(tag, "[CANNOT] Mecanismo de autenticação não suportado");
            return;
        }

        var outcome = await Sasl.AuthenticateAsync(mechanism, initial, async challenge =>
        {
            await c.SendLineAsync("+ " + challenge, ct);
            var response = await c.ReadLineAsync(ct, 64 * 1024);
            if (response is not null)
                _log.FromClient(response);
            return response;
        }, context.Authenticator, context.OpenIdConfigurationUrl);

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
            _log.Info($"Falha na autenticação {outcome.Mechanism}: {outcome.Error}");
            No(tag, "[AUTHENTICATIONFAILED] Falha na autenticação: " + outcome.Error);
        }
    }

    private void SetUser(string user, string mechanism)
    {
        _user = user;
        _log.User = user;
        _log.AuthMechanism = mechanism;
        _log.Info($"Autenticado como '{user}' via {mechanism}");
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
            {
                context.Store.SetSeen([entry.Id], true);
                message = context.Store.Get(entry.Id) ?? message;
                _selected.FlagState[entry.Uid] = FlagKey(message);
            }

            var w = new ImapWriter();
            w.Raw($"* {entry.Sequence} FETCH (");
            var first = true;
            var flagsWritten = false;
            void Separator()
            {
                if (!first)
                    w.Space();
                first = false;
            }

            foreach (var item in items)
            {
                Separator();
                switch (item.Kind)
                {
                    case FetchKind.Uid:
                        w.Raw("UID ").Number(entry.Uid);
                        break;
                    case FetchKind.Flags:
                        w.Raw("FLAGS " + ImapFetch.FormatFlags(message.Flags));
                        flagsWritten = true;
                        break;
                    case FetchKind.InternalDate:
                        w.Raw("INTERNALDATE ").String(ImapFetch.FormatInternalDate(message.ReceivedAt));
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
                        if (data is null)
                            w.Raw("NIL");
                        else
                            w.Literal(data);
                        break;
                }
            }

            if (markSeen && !flagsWritten)
            {
                Separator();
                w.Raw("FLAGS " + ImapFetch.FormatFlags(message.Flags));
            }
            w.Raw(")\r\n");
            c.Write(w.ToArray());
            _log.FromServer($"* {entry.Sequence} FETCH ({labels}) [{w.Length} bytes]");

            if (c.PendingOutput > 1024 * 1024)
                await c.FlushAsync(ct);
        }

        Sync(byUid);
        Ok(tag, (byUid ? "UID " : "") + "FETCH concluído");
    }

    private MimeNode Structure(string id, byte[] raw)
    {
        if (_structureCache.TryGetValue(id, out var cached))
            return cached;
        var node = MimeStructure.Parse(raw);
        if (_structureCache.Count > 200)
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

        var regexes = patterns.Select(p => PatternRegex(ImapUtf7.Decode(reference + p))).ToList();
        var folders = context.Store.Folders;
        foreach (var folder in folders)
        {
            if (!regexes.Any(r => r.IsMatch(folder)))
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

            var w = new ImapWriter();
            w.Raw($"{verb} ({string.Join(' ', attributes)}) \"/\" ").String(ImapUtf7.Encode(folder));
            Untagged(Encoding.UTF8.GetString(w.ToArray()));
        }
        Ok(tag, $"{verb} concluído");
    }

    private static Regex PatternRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (var ch in pattern)
        {
            sb.Append(ch switch
            {
                '*' => ".*",
                '%' => "[^/]*",
                _ => Regex.Escape(ch.ToString()),
            });
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
        var w = new ImapWriter();
        w.Raw("STATUS ").String(ImapUtf7.Encode(folder)).Raw($" ({string.Join(' ', values)})");
        Untagged(Encoding.UTF8.GetString(w.ToArray()));
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
        if (token.Bytes is null && token.Type == ImapTokenType.String)
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
        var maxSize = context.Settings.Current.MaxMessageSizeMb * 1024L * 1024L;
        if (bytes.Length > maxSize)
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
        var match = Regex.Match(text.Trim(), @"^(\d{1,2})-([A-Za-z]{3})-(\d{4}) (\d{2}):(\d{2}):(\d{2}) ([+-])(\d{2})(\d{2})$");
        if (!match.Success)
            return null;
        var normalized = $"{match.Groups[1].Value.PadLeft(2, '0')}-{match.Groups[2].Value}-{match.Groups[3].Value} {match.Groups[4].Value}:{match.Groups[5].Value}:{match.Groups[6].Value} {match.Groups[7].Value}{match.Groups[8].Value}:{match.Groups[9].Value}";
        return DateTimeOffset.TryParseExact(normalized, "dd-MMM-yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private async Task<bool> IdleAsync(string tag, CancellationToken ct)
    {
        await c.SendLineAsync("+ Aguardando novidades (IDLE)", ct);
        var read = c.ReadLineAsync(ct, 8192);
        while (true)
        {
            var changed = Volatile.Read(ref _changeSignal).Task;
            Sync(true);
            await c.FlushAsync(ct);

            var timeout = Task.Delay(TimeSpan.FromMinutes(25), ct);
            var done = await Task.WhenAny(read, changed, timeout);
            if (done == read)
            {
                var line = await read;
                if (line is null)
                    return false;
                _log.FromClient(line);
                if (line.Trim().Equals("DONE", StringComparison.OrdinalIgnoreCase))
                    Ok(tag, "IDLE encerrado");
                else
                    Bad(tag, "Esperava DONE");
                return true;
            }
            if (done == timeout)
                Untagged("OK Ainda conectado");
        }
    }
}
