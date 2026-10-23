using System.Text;
using MimeKit;

namespace FakeServerEmail.Protocols.Imap;

internal sealed record RawHeader(string Name, string Value, int Start, int End);

internal sealed class MimeNode
{
    public int Start { get; init; }
    public int BodyStart { get; init; }
    public int End { get; init; }
    public List<RawHeader> Headers { get; init; } = [];
    public string MediaType { get; set; } = "text";
    public string SubType { get; set; } = "plain";
    public List<(string Name, string Value)> Parameters { get; set; } = [];
    public string? ContentId { get; set; }
    public string? Description { get; set; }
    public string Encoding { get; set; } = "7BIT";
    public string? Md5 { get; set; }
    public string? Disposition { get; set; }
    public List<(string Name, string Value)> DispositionParameters { get; set; } = [];
    public string? Language { get; set; }
    public string? Location { get; set; }
    public List<MimeNode> Children { get; } = [];
    public MimeNode? Message { get; set; }
    public int BodyLines { get; set; }

    public bool IsMultipart => MediaType.Equals("multipart", StringComparison.OrdinalIgnoreCase);

    public bool IsMessage => MediaType.Equals("message", StringComparison.OrdinalIgnoreCase)
        && (SubType.Equals("rfc822", StringComparison.OrdinalIgnoreCase) || SubType.Equals("global", StringComparison.OrdinalIgnoreCase));

    public int BodyLength => End - BodyStart;

    public string? Header(string name) => Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
}

internal static class MimeStructure
{
    private const int MaxDepth = 50;

    public static MimeNode Parse(byte[] raw) => ParseEntity(raw, 0, raw.Length, false, 0);

    private static MimeNode ParseEntity(byte[] raw, int start, int end, bool digestChild, int depth)
    {
        var headers = new List<RawHeader>();
        var position = start;
        var bodyStart = end;
        string? currentName = null;
        var currentValue = new StringBuilder();
        var currentStart = start;

        void Commit(int lineEnd)
        {
            if (currentName is not null)
                headers.Add(new RawHeader(currentName, currentValue.ToString().Trim(), currentStart, lineEnd));
            currentName = null;
            currentValue.Clear();
        }

        while (position < end)
        {
            var newline = IndexOf(raw, (byte)'\n', position, end);
            var lineEnd = newline < 0 ? end : newline + 1;
            var contentEnd = newline < 0 ? end : (newline > position && raw[newline - 1] == '\r' ? newline - 1 : newline);

            if (contentEnd == position)
            {
                Commit(position);
                bodyStart = lineEnd;
                break;
            }

            var line = System.Text.Encoding.UTF8.GetString(raw, position, contentEnd - position);
            if (line[0] is ' ' or '\t')
            {
                if (currentName is not null)
                    currentValue.Append(' ').Append(line.Trim());
            }
            else
            {
                Commit(position);
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    currentName = line[..colon].Trim();
                    currentValue.Append(line[(colon + 1)..]);
                    currentStart = position;
                }
            }
            position = lineEnd;
            if (position >= end)
            {
                Commit(position);
                bodyStart = end;
            }
        }

        var node = new MimeNode { Start = start, BodyStart = Math.Min(bodyStart, end), End = end, Headers = headers };
        if (digestChild)
        {
            node.MediaType = "message";
            node.SubType = "rfc822";
        }

        var contentTypeValue = node.Header("Content-Type");
        if (contentTypeValue is not null && ContentType.TryParse(contentTypeValue, out var contentType))
        {
            node.MediaType = contentType.MediaType;
            node.SubType = contentType.MediaSubtype;
            node.Parameters = contentType.Parameters.Select(p => (p.Name, p.Value)).ToList();
        }
        else if (!digestChild)
        {
            node.Parameters = [("charset", "us-ascii")];
        }

        node.Encoding = node.Header("Content-Transfer-Encoding")?.Trim() is { Length: > 0 } enc ? enc : "7BIT";
        node.ContentId = node.Header("Content-ID");
        node.Description = node.Header("Content-Description");
        node.Md5 = node.Header("Content-MD5");
        node.Language = node.Header("Content-Language");
        node.Location = node.Header("Content-Location");
        var dispositionValue = node.Header("Content-Disposition");
        if (dispositionValue is not null && ContentDisposition.TryParse(dispositionValue, out var disposition))
        {
            node.Disposition = disposition.Disposition;
            node.DispositionParameters = disposition.Parameters.Select(p => (p.Name, p.Value)).ToList();
        }

        node.BodyLines = CountLines(raw, node.BodyStart, end);

        if (depth > MaxDepth)
            return node;

        if (node.IsMultipart)
        {
            var boundary = node.Parameters.FirstOrDefault(p => p.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrEmpty(boundary))
            {
                var isDigest = node.SubType.Equals("digest", StringComparison.OrdinalIgnoreCase);
                foreach (var (partStart, partEnd) in SplitMultipart(raw, node.BodyStart, end, boundary))
                    node.Children.Add(ParseEntity(raw, partStart, partEnd, isDigest, depth + 1));
            }
            if (node.Children.Count == 0)
                node.Children.Add(new MimeNode { Start = node.BodyStart, BodyStart = node.BodyStart, End = end, Parameters = [("charset", "us-ascii")], BodyLines = node.BodyLines });
        }
        else if (node.IsMessage)
        {
            node.Message = ParseEntity(raw, node.BodyStart, end, false, depth + 1);
        }

        return node;
    }

    private static List<(int Start, int End)> SplitMultipart(byte[] raw, int start, int end, string boundary)
    {
        var delimiter = System.Text.Encoding.ASCII.GetBytes("--" + boundary);
        var parts = new List<(int, int)>();
        var position = start;
        var partStart = -1;

        while (position < end)
        {
            var newline = IndexOf(raw, (byte)'\n', position, end);
            var lineEnd = newline < 0 ? end : newline + 1;
            var contentEnd = newline < 0 ? end : (newline > position && raw[newline - 1] == '\r' ? newline - 1 : newline);

            if (contentEnd - position >= delimiter.Length && raw.AsSpan(position, delimiter.Length).SequenceEqual(delimiter))
            {
                var rest = raw.AsSpan(position + delimiter.Length, contentEnd - position - delimiter.Length);
                var closing = rest.Length >= 2 && rest[0] == '-' && rest[1] == '-';
                var trailing = closing ? rest[2..] : rest;
                if (IsWhitespace(trailing))
                {
                    if (partStart >= 0)
                    {
                        var partEnd = position;
                        if (partEnd > partStart && raw[partEnd - 1] == '\n')
                            partEnd--;
                        if (partEnd > partStart && raw[partEnd - 1] == '\r')
                            partEnd--;
                        parts.Add((partStart, Math.Max(partStart, partEnd)));
                    }
                    if (closing)
                        return parts;
                    partStart = lineEnd;
                }
            }
            position = lineEnd;
        }

        if (partStart >= 0 && partStart <= end)
            parts.Add((partStart, end));
        return parts;
    }

    private static bool IsWhitespace(ReadOnlySpan<byte> span)
    {
        foreach (var b in span)
            if (b is not ((byte)' ' or (byte)'\t'))
                return false;
        return true;
    }

    private static int CountLines(byte[] raw, int start, int end)
    {
        if (end <= start)
            return 0;
        var count = raw.AsSpan(start, end - start).Count((byte)'\n');
        if (raw[end - 1] != '\n')
            count++;
        return count;
    }

    private static int IndexOf(byte[] raw, byte value, int start, int end)
    {
        var index = raw.AsSpan(start, end - start).IndexOf(value);
        return index < 0 ? -1 : start + index;
    }
}
