using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FakeServerEmail.Core;
using MimeKit;
using MimeKit.Utils;

namespace FakeServerEmail.Protocols.Imap;

public enum FetchKind { Uid, Flags, InternalDate, Size, Envelope, BodyStructure, Body, Section }

public sealed class FetchItem
{
    public FetchKind Kind { get; init; }
    public string Label { get; init; } = "";
    public bool Peek { get; init; }
    public List<int> Path { get; init; } = [];
    public string Spec { get; init; } = "";
    public List<string> Fields { get; init; } = [];
    public long? PartialStart { get; init; }
    public long? PartialLength { get; init; }

    public bool NeedsBody => Kind is FetchKind.Envelope or FetchKind.BodyStructure or FetchKind.Body or FetchKind.Section;
}

public static partial class ImapFetch
{
    public static List<FetchItem> ParseItems(ImapToken token)
    {
        var items = new List<FetchItem>();
        var atoms = token.Type == ImapTokenType.List ? token.Items : [token];
        foreach (var atom in atoms)
        {
            var name = atom.Text.ToUpperInvariant();
            switch (name)
            {
                case "ALL":
                    items.AddRange([Simple(FetchKind.Flags), Simple(FetchKind.InternalDate), Simple(FetchKind.Size), Simple(FetchKind.Envelope)]);
                    break;
                case "FAST":
                    items.AddRange([Simple(FetchKind.Flags), Simple(FetchKind.InternalDate), Simple(FetchKind.Size)]);
                    break;
                case "FULL":
                    items.AddRange([Simple(FetchKind.Flags), Simple(FetchKind.InternalDate), Simple(FetchKind.Size), Simple(FetchKind.Envelope), Simple(FetchKind.Body)]);
                    break;
                case "UID": items.Add(Simple(FetchKind.Uid)); break;
                case "FLAGS": items.Add(Simple(FetchKind.Flags)); break;
                case "INTERNALDATE": items.Add(Simple(FetchKind.InternalDate)); break;
                case "RFC822.SIZE": items.Add(Simple(FetchKind.Size)); break;
                case "ENVELOPE": items.Add(Simple(FetchKind.Envelope)); break;
                case "BODYSTRUCTURE": items.Add(Simple(FetchKind.BodyStructure)); break;
                case "BODY": items.Add(Simple(FetchKind.Body)); break;
                case "RFC822": items.Add(new FetchItem { Kind = FetchKind.Section, Label = "RFC822" }); break;
                case "RFC822.HEADER": items.Add(new FetchItem { Kind = FetchKind.Section, Label = "RFC822.HEADER", Peek = true, Spec = "HEADER" }); break;
                case "RFC822.TEXT": items.Add(new FetchItem { Kind = FetchKind.Section, Label = "RFC822.TEXT", Spec = "TEXT" }); break;
                case "MODSEQ":
                case "X-GM-MSGID":
                case "X-GM-THRID":
                case "X-GM-LABELS":
                case "EMAILID":
                case "THREADID":
                case "SAVEDATE":
                case "PREVIEW":
                    break;
                default:
                    items.Add(ParseSection(atom.Text));
                    break;
            }
        }
        return items;
    }

    private static FetchItem Simple(FetchKind kind) => new() { Kind = kind };

    private static FetchItem ParseSection(string text)
    {
        var match = SectionRegex().Match(text);
        if (!match.Success)
            throw new ImapParseException($"Item de FETCH desconhecido: {text}");

        var peek = match.Groups[1].Value.EndsWith(".PEEK", StringComparison.OrdinalIgnoreCase);
        var section = match.Groups[2].Value.Trim();
        var path = new List<int>();
        var rest = section;
        while (true)
        {
            var dot = rest.IndexOf('.');
            var head = dot < 0 ? rest : rest[..dot];
            if (head.Length == 0 || !head.All(char.IsAsciiDigit))
                break;
            path.Add(int.Parse(head, CultureInfo.InvariantCulture));
            rest = dot < 0 ? "" : rest[(dot + 1)..];
        }

        var spec = rest.ToUpperInvariant();
        var fields = new List<string>();
        var paren = spec.IndexOf('(');
        if (paren >= 0)
        {
            fields = rest[(paren + 1)..].TrimEnd(')', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim('"')).ToList();
            spec = spec[..paren].Trim();
        }

        if (spec is not ("" or "HEADER" or "HEADER.FIELDS" or "HEADER.FIELDS.NOT" or "TEXT" or "MIME"))
            throw new ImapParseException($"Seção inválida: {section}");

        long? start = null;
        long? length = null;
        if (match.Groups[3].Success)
        {
            start = long.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
            if (match.Groups[5].Success)
                length = long.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
        }

        var normalized = string.Join('.', path.Select(p => p.ToString(CultureInfo.InvariantCulture)).Append(spec).Where(s => s.Length > 0));
        if (fields.Count > 0)
            normalized += " (" + string.Join(' ', fields.Select(f => f.ToUpperInvariant())) + ")";

        return new FetchItem
        {
            Kind = FetchKind.Section,
            Label = $"BODY[{normalized}]" + (start is null ? "" : $"<{start}>"),
            Peek = peek,
            Path = path,
            Spec = spec,
            Fields = fields,
            PartialStart = start,
            PartialLength = length,
        };
    }

    [GeneratedRegex(@"^(BODY|BODY\.PEEK|BINARY|BINARY\.PEEK)\[(.*)\](<(\d+)(?:\.(\d+))?>)?$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SectionRegex();

    public static byte[]? ExtractSection(byte[] raw, MimeNode root, FetchItem item)
    {
        var node = root;
        var messageContext = true;
        foreach (var number in item.Path)
        {
            if (node.IsMessage && !messageContext && node.Message is not null)
                node = node.Message;
            if (node.IsMultipart)
            {
                if (number < 1 || number > node.Children.Count)
                    return null;
                node = node.Children[number - 1];
            }
            else if (number != 1)
            {
                return null;
            }
            messageContext = false;
        }

        var message = item.Path.Count == 0 ? root : node.Message ?? node;
        byte[] data = item.Spec switch
        {
            "" when item.Path.Count == 0 => raw,
            "" => raw[node.BodyStart..node.End],
            "MIME" => HeaderBlock(raw, node),
            "HEADER" => HeaderBlock(raw, message),
            "TEXT" => raw[message.BodyStart..message.End],
            "HEADER.FIELDS" => SelectHeaders(raw, message, item.Fields, true),
            "HEADER.FIELDS.NOT" => SelectHeaders(raw, message, item.Fields, false),
            _ => [],
        };

        if (item.PartialStart is { } start)
        {
            if (start >= data.Length)
                return [];
            var length = item.PartialLength is { } l ? Math.Min(l, data.Length - start) : data.Length - start;
            data = data.AsSpan((int)start, (int)length).ToArray();
        }
        return data;
    }

    private static byte[] HeaderBlock(byte[] raw, MimeNode node)
    {
        var block = raw[node.Start..node.BodyStart];
        if (block.Length >= 2 && block[^1] == '\n' && (block[^2] == '\n' || (block.Length >= 3 && block[^2] == '\r' && block[^3] == '\n')))
            return block;
        return [.. block, .. "\r\n"u8];
    }

    private static byte[] SelectHeaders(byte[] raw, MimeNode node, List<string> fields, bool include)
    {
        using var ms = new MemoryStream();
        foreach (var h in node.Headers)
        {
            var listed = fields.Contains(h.Name, StringComparer.OrdinalIgnoreCase);
            if (listed != include)
                continue;
            ms.Write(raw, h.Start, h.End - h.Start);
            if (h.End == 0 || raw[h.End - 1] != '\n')
                ms.Write("\r\n"u8);
        }
        ms.Write("\r\n"u8);
        return ms.ToArray();
    }

    public static string FormatInternalDate(DateTimeOffset date)
    {
        var offset = date.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return date.ToString("dd-MMM-yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + sign + offset.Hours.ToString("00", CultureInfo.InvariantCulture) + offset.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    public static string FormatFlags(IEnumerable<string> flags) => "(" + string.Join(' ', flags) + ")";

    public static void WriteEnvelope(ImapWriter w, MimeNode message)
    {
        string? H(string name) => message.Header(name);
        var from = H("From");
        w.Raw("(");
        w.String(H("Date")).Space();
        w.String(H("Subject")).Space();
        WriteAddresses(w, from).Space();
        WriteAddresses(w, H("Sender") ?? from).Space();
        WriteAddresses(w, H("Reply-To") ?? from).Space();
        WriteAddresses(w, H("To")).Space();
        WriteAddresses(w, H("Cc")).Space();
        WriteAddresses(w, H("Bcc")).Space();
        w.String(H("In-Reply-To")).Space();
        w.String(H("Message-ID"));
        w.Raw(")");
    }

    private static ImapWriter WriteAddresses(ImapWriter w, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !InternetAddressList.TryParse(value, out var list) || list.Count == 0)
            return w.Raw("NIL");

        w.Raw("(");
        foreach (var address in list)
        {
            if (address is GroupAddress group)
            {
                w.Raw("(NIL NIL ").String(group.Name).Raw(" NIL)");
                foreach (var member in group.Members.Mailboxes)
                    WriteMailbox(w, member);
                w.Raw("(NIL NIL NIL NIL)");
            }
            else if (address is MailboxAddress mailbox)
            {
                WriteMailbox(w, mailbox);
            }
        }
        return w.Raw(")");
    }

    private static void WriteMailbox(ImapWriter w, MailboxAddress mailbox)
    {
        string? name = string.IsNullOrEmpty(mailbox.Name) ? null : mailbox.Name;
        if (name is not null && name.Any(c => c > 0x7e))
            name = Encoding.ASCII.GetString(Rfc2047.EncodePhrase(Encoding.UTF8, name));
        var at = mailbox.Address.LastIndexOf('@');
        var local = at < 0 ? mailbox.Address : mailbox.Address[..at];
        var host = at < 0 ? null : mailbox.Address[(at + 1)..];
        w.Raw("(").String(name).Raw(" NIL ").String(local).Space().String(host).Raw(")");
    }

    public static void WriteBodyStructure(ImapWriter w, MimeNode node, bool extensible)
    {
        if (node.IsMultipart)
        {
            w.Raw("(");
            foreach (var child in node.Children)
                WriteBodyStructure(w, child, extensible);
            w.Space().String(node.SubType.ToUpperInvariant());
            if (extensible)
            {
                w.Space();
                WriteParameters(w, node.Parameters);
                w.Space();
                WriteDisposition(w, node);
                w.Space().String(node.Language).Space().String(node.Location);
            }
            w.Raw(")");
            return;
        }

        w.Raw("(");
        w.String(node.MediaType.ToUpperInvariant()).Space().String(node.SubType.ToUpperInvariant()).Space();
        WriteParameters(w, node.Parameters);
        w.Space().String(node.ContentId).Space().String(node.Description).Space().String(node.Encoding.ToUpperInvariant()).Space().Number(node.BodyLength);

        if (node.IsMessage && node.Message is not null)
        {
            w.Space();
            WriteEnvelope(w, node.Message);
            w.Space();
            WriteBodyStructure(w, node.Message, extensible);
            w.Space().Number(node.BodyLines);
        }
        else if (node.MediaType.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            w.Space().Number(node.BodyLines);
        }

        if (extensible)
        {
            w.Space().String(node.Md5).Space();
            WriteDisposition(w, node);
            w.Space().String(node.Language).Space().String(node.Location);
        }
        w.Raw(")");
    }

    private static void WriteParameters(ImapWriter w, List<(string Name, string Value)> parameters)
    {
        if (parameters.Count == 0)
        {
            w.Raw("NIL");
            return;
        }
        w.Raw("(");
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
                w.Space();
            w.String(parameters[i].Name.ToUpperInvariant()).Space().String(parameters[i].Value);
        }
        w.Raw(")");
    }

    private static void WriteDisposition(ImapWriter w, MimeNode node)
    {
        if (node.Disposition is null)
        {
            w.Raw("NIL");
            return;
        }
        w.Raw("(").String(node.Disposition.ToUpperInvariant()).Space();
        WriteParameters(w, node.DispositionParameters);
        w.Raw(")");
    }

    public static bool SetsSeen(FetchItem item) => item.Kind == FetchKind.Section && !item.Peek;
}
