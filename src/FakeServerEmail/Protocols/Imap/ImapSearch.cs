using System.Globalization;
using FakeServerEmail.Core;
using MimeKit;

namespace FakeServerEmail.Protocols.Imap;

public sealed class SearchTarget(StoredMessage message, int sequence, long maxSequence, long maxUid, Func<byte[]?> loadRaw)
{
    private MimeMessage? _parsed;
    private bool _loaded;

    public StoredMessage Message { get; } = message;
    public int Sequence { get; } = sequence;
    public long MaxSequence { get; } = maxSequence;
    public long MaxUid { get; } = maxUid;

    public MimeMessage? Parsed
    {
        get
        {
            if (_loaded)
                return _parsed;
            _loaded = true;
            var raw = loadRaw();
            if (raw is null)
                return null;
            try
            {
                using var ms = new MemoryStream(raw, false);
                _parsed = MimeMessage.Load(ms);
            }
            catch
            {
                _parsed = null;
            }
            return _parsed;
        }
    }
}

public static class ImapSearch
{
    public static Func<SearchTarget, bool> Parse(List<ImapToken> tokens)
    {
        var queue = new Queue<ImapToken>(tokens);
        if (queue.Count > 0 && queue.Peek().Upper == "CHARSET")
        {
            queue.Dequeue();
            if (queue.Count > 0)
                queue.Dequeue();
        }
        if (queue.Count > 0 && queue.Peek().Upper == "RETURN")
        {
            queue.Dequeue();
            if (queue.Count > 0)
                queue.Dequeue();
        }

        var keys = new List<Func<SearchTarget, bool>>();
        while (queue.Count > 0)
            keys.Add(ParseKey(queue));
        if (keys.Count == 0)
            throw new ImapParseException("Critério de busca vazio");
        return t => keys.All(k => k(t));
    }

    private static Func<SearchTarget, bool> ParseKey(Queue<ImapToken> q)
    {
        var token = q.Dequeue();
        if (token.Type == ImapTokenType.List)
        {
            var inner = new Queue<ImapToken>(token.Items);
            var keys = new List<Func<SearchTarget, bool>>();
            while (inner.Count > 0)
                keys.Add(ParseKey(inner));
            return t => keys.All(k => k(t));
        }

        string Arg() => q.Count > 0 ? q.Dequeue().Text : throw new ImapParseException($"Faltou argumento para {token.Text}");
        DateTime DateArg()
        {
            var text = Arg();
            if (!DateTime.TryParseExact(text, ["d-MMM-yyyy", "dd-MMM-yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                throw new ImapParseException($"Data inválida: {text}");
            return d.Date;
        }

        switch (token.Upper)
        {
            case "ALL": return _ => true;
            case "ANSWERED": return t => t.Message.HasFlag(ImapFlags.Answered);
            case "DELETED": return t => t.Message.HasFlag(ImapFlags.Deleted);
            case "DRAFT": return t => t.Message.HasFlag(ImapFlags.Draft);
            case "FLAGGED": return t => t.Message.HasFlag(ImapFlags.Flagged);
            case "SEEN": return t => t.Message.HasFlag(ImapFlags.Seen);
            case "UNANSWERED": return t => !t.Message.HasFlag(ImapFlags.Answered);
            case "UNDELETED": return t => !t.Message.HasFlag(ImapFlags.Deleted);
            case "UNDRAFT": return t => !t.Message.HasFlag(ImapFlags.Draft);
            case "UNFLAGGED": return t => !t.Message.HasFlag(ImapFlags.Flagged);
            case "UNSEEN": return t => !t.Message.HasFlag(ImapFlags.Seen);
            case "RECENT":
            case "NEW":
                return _ => false;
            case "OLD": return _ => true;
            case "KEYWORD":
                {
                    var k = Arg();
                    return t => t.Message.HasFlag(k);
                }
            case "UNKEYWORD":
                {
                    var k = Arg();
                    return t => !t.Message.HasFlag(k);
                }
            case "FROM": return HeaderContains("From", Arg());
            case "TO": return HeaderContains("To", Arg());
            case "CC": return HeaderContains("Cc", Arg());
            case "BCC": return HeaderContains("Bcc", Arg());
            case "SUBJECT": return HeaderContains("Subject", Arg());
            case "HEADER":
                {
                    var field = Arg();
                    return HeaderContains(field, Arg());
                }
            case "BODY":
                {
                    var s = Arg();
                    return t => BodyText(t).Contains(s, StringComparison.OrdinalIgnoreCase);
                }
            case "TEXT":
                {
                    var s = Arg();
                    return t => BodyText(t).Contains(s, StringComparison.OrdinalIgnoreCase)
                        || (t.Parsed?.Headers.Any(h => h.Value.Contains(s, StringComparison.OrdinalIgnoreCase)) ?? false);
                }
            case "BEFORE":
                {
                    var d = DateArg();
                    return t => t.Message.ReceivedAt.Date < d;
                }
            case "ON":
                {
                    var d = DateArg();
                    return t => t.Message.ReceivedAt.Date == d;
                }
            case "SINCE":
                {
                    var d = DateArg();
                    return t => t.Message.ReceivedAt.Date >= d;
                }
            case "SENTBEFORE":
                {
                    var d = DateArg();
                    return t => (t.Message.Date ?? t.Message.ReceivedAt).Date < d;
                }
            case "SENTON":
                {
                    var d = DateArg();
                    return t => (t.Message.Date ?? t.Message.ReceivedAt).Date == d;
                }
            case "SENTSINCE":
                {
                    var d = DateArg();
                    return t => (t.Message.Date ?? t.Message.ReceivedAt).Date >= d;
                }
            case "LARGER":
                {
                    var n = long.Parse(Arg(), CultureInfo.InvariantCulture);
                    return t => t.Message.Size > n;
                }
            case "SMALLER":
                {
                    var n = long.Parse(Arg(), CultureInfo.InvariantCulture);
                    return t => t.Message.Size < n;
                }
            case "YOUNGER":
                {
                    var n = long.Parse(Arg(), CultureInfo.InvariantCulture);
                    return t => (DateTimeOffset.Now - t.Message.ReceivedAt).TotalSeconds <= n;
                }
            case "OLDER":
                {
                    var n = long.Parse(Arg(), CultureInfo.InvariantCulture);
                    return t => (DateTimeOffset.Now - t.Message.ReceivedAt).TotalSeconds > n;
                }
            case "UID":
                {
                    var text = Arg();
                    if (!SequenceSet.TryParse(text, out var set))
                        throw new ImapParseException($"Conjunto de UID inválido: {text}");
                    return t => set.Contains(t.Message.Uid, t.MaxUid);
                }
            case "NOT":
                {
                    var inner = ParseKey(q);
                    return t => !inner(t);
                }
            case "OR":
                {
                    var a = ParseKey(q);
                    var b = ParseKey(q);
                    return t => a(t) || b(t);
                }
            case "MODSEQ":
                Arg();
                return _ => true;
            default:
                if (SequenceSet.TryParse(token.Text, out var sequence))
                    return t => sequence.Contains(t.Sequence, t.MaxSequence);
                throw new ImapParseException($"Critério de busca desconhecido: {token.Text}");
        }
    }

    private static Func<SearchTarget, bool> HeaderContains(string field, string value) => t =>
    {
        var parsed = t.Parsed;
        if (parsed is null)
            return false;
        var headers = parsed.Headers.Where(h => h.Field.Equals(field, StringComparison.OrdinalIgnoreCase)).ToList();
        if (value.Length == 0)
            return headers.Count > 0;
        return headers.Any(h => h.Value.Contains(value, StringComparison.OrdinalIgnoreCase)
            || DecodedAddressText(parsed, field).Contains(value, StringComparison.OrdinalIgnoreCase));
    };

    private static string DecodedAddressText(MimeMessage m, string field) => field.ToUpperInvariant() switch
    {
        "FROM" => m.From.ToString(),
        "TO" => m.To.ToString(),
        "CC" => m.Cc.ToString(),
        "BCC" => m.Bcc.ToString(),
        "SUBJECT" => m.Subject ?? "",
        _ => "",
    };

    private static string BodyText(SearchTarget t)
    {
        var parsed = t.Parsed;
        if (parsed is null)
            return "";
        return (parsed.TextBody ?? "") + "\n" + (parsed.HtmlBody ?? "");
    }
}
