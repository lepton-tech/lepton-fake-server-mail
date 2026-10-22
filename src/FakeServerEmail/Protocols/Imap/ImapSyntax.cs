using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FakeServerEmail.Protocols.Imap;

public sealed class ImapParseException(string message) : Exception(message);

public enum ImapTokenType { Atom, String, List }

public sealed class ImapToken
{
    public ImapTokenType Type { get; init; }
    public string Text { get; init; } = "";
    public byte[]? Bytes { get; init; }
    public List<ImapToken> Items { get; } = [];

    public bool IsNil => Type == ImapTokenType.Atom && Text.Equals("NIL", StringComparison.OrdinalIgnoreCase);

    public string Upper => Text.ToUpperInvariant();

    public override string ToString() => Type == ImapTokenType.List ? "(" + string.Join(' ', Items) + ")" : Text;
}

public sealed partial class ImapCommand
{
    private static readonly UTF8Encoding Utf8 = new(false, false);

    public required string Text { get; init; }
    public required List<byte[]> Literals { get; init; }

    public string LogText
    {
        get
        {
            var i = 0;
            return Regex.Replace(Text, "\u0001", _ => i < Literals.Count ? $"{{{Literals[i++].Length} bytes}}" : "{literal}");
        }
    }

    public static async Task<ImapCommand?> ReadAsync(MailConnection c, Func<Task> sendContinuation, CancellationToken ct)
    {
        var text = new StringBuilder();
        var literals = new List<byte[]>();
        while (true)
        {
            var bytes = await c.ReadLineBytesAsync(ct, 256 * 1024);
            if (bytes is null)
                return null;
            var line = Utf8.GetString(bytes);
            var match = LiteralSuffix().Match(line);
            if (!match.Success)
            {
                text.Append(line);
                return new ImapCommand { Text = text.ToString(), Literals = literals };
            }

            text.Append(line, 0, match.Index);
            var size = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (match.Groups[2].Value != "+")
                await sendContinuation();
            var literal = await c.ReadExactAsync(size, ct);
            if (literal is null)
                return null;
            literals.Add(literal);
            text.Append('\u0001');
        }
    }

    [GeneratedRegex(@"\{(\d{1,10})(\+?)\}$")]
    private static partial Regex LiteralSuffix();
}

public sealed class ImapTokenizer(string text, List<byte[]> literals)
{
    private static readonly UTF8Encoding Utf8 = new(false, false);
    private int _position;
    private int _literal;

    public bool AtEnd
    {
        get
        {
            SkipSpaces();
            return _position >= text.Length;
        }
    }

    public bool PeekIs(char ch)
    {
        SkipSpaces();
        return _position < text.Length && text[_position] == ch;
    }

    public string Remaining
    {
        get
        {
            SkipSpaces();
            return text[_position..];
        }
    }

    public ImapToken? Next()
    {
        SkipSpaces();
        if (_position >= text.Length)
            return null;

        var ch = text[_position];
        if (ch == '(')
        {
            _position++;
            var list = new ImapToken { Type = ImapTokenType.List };
            while (true)
            {
                SkipSpaces();
                if (_position >= text.Length)
                    throw new ImapParseException("Lista sem ')'");
                if (text[_position] == ')')
                {
                    _position++;
                    return list;
                }
                list.Items.Add(Next() ?? throw new ImapParseException("Lista incompleta"));
            }
        }

        if (ch == ')')
            throw new ImapParseException("')' inesperado");

        if (ch == '"')
        {
            _position++;
            var sb = new StringBuilder();
            while (_position < text.Length && text[_position] != '"')
            {
                if (text[_position] == '\\' && _position + 1 < text.Length)
                    _position++;
                sb.Append(text[_position++]);
            }
            if (_position >= text.Length)
                throw new ImapParseException("String sem aspas de fechamento");
            _position++;
            return new ImapToken { Type = ImapTokenType.String, Text = sb.ToString() };
        }

        if (ch == '\u0001')
        {
            _position++;
            if (_literal >= literals.Count)
                throw new ImapParseException("Literal ausente");
            var bytes = literals[_literal++];
            return new ImapToken { Type = ImapTokenType.String, Text = Utf8.GetString(bytes), Bytes = bytes };
        }

        var start = _position;
        var depth = 0;
        while (_position < text.Length)
        {
            var c = text[_position];
            if (c == '[')
                depth++;
            else if (c == ']')
                depth = Math.Max(0, depth - 1);
            else if (depth == 0 && (c == ' ' || c == '(' || c == ')'))
                break;
            _position++;
        }
        return new ImapToken { Type = ImapTokenType.Atom, Text = text[start.._position] };
    }

    public ImapToken Require(string what) => Next() ?? throw new ImapParseException($"Faltou {what}");

    public string RequireAString(string what)
    {
        var token = Require(what);
        if (token.Type == ImapTokenType.List)
            throw new ImapParseException($"{what} inválido");
        return token.Text;
    }

    private void SkipSpaces()
    {
        while (_position < text.Length && text[_position] == ' ')
            _position++;
    }
}

public sealed class ImapWriter
{
    private static readonly UTF8Encoding Utf8 = new(false, false);
    private readonly MemoryStream _buffer = new();

    public ImapWriter Raw(string text)
    {
        _buffer.Write(Utf8.GetBytes(text));
        return this;
    }

    public ImapWriter Raw(ReadOnlySpan<byte> bytes)
    {
        _buffer.Write(bytes);
        return this;
    }

    public ImapWriter Number(long value) => Raw(value.ToString(CultureInfo.InvariantCulture));

    public ImapWriter Literal(ReadOnlySpan<byte> bytes)
    {
        Raw("{" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "}\r\n");
        return Raw(bytes);
    }

    public ImapWriter String(string? value)
    {
        if (value is null)
            return Raw("NIL");

        var safe = value.Length < 1024;
        foreach (var ch in value)
        {
            if (ch > 0x7e || ch < 0x20)
            {
                safe = false;
                break;
            }
        }

        if (!safe)
            return Literal(Utf8.GetBytes(value));
        return Raw("\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
    }

    public ImapWriter Space() => Raw(" ");

    public byte[] ToArray() => _buffer.ToArray();

    public long Length => _buffer.Length;
}

public sealed class SequenceSet
{
    private readonly List<(long From, long To)> _ranges = [];

    public static bool TryParse(string text, out SequenceSet set)
    {
        set = new SequenceSet();
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var part in text.Split(','))
        {
            var bounds = part.Split(':');
            if (bounds.Length is < 1 or > 2)
                return false;
            if (!TryNumber(bounds[0], out var from))
                return false;
            var to = from;
            if (bounds.Length == 2 && !TryNumber(bounds[1], out to))
                return false;
            set._ranges.Add((from, to));
        }
        return true;
    }

    public bool Contains(long value, long max)
    {
        foreach (var (rawFrom, rawTo) in _ranges)
        {
            var from = rawFrom < 0 ? max : rawFrom;
            var to = rawTo < 0 ? max : rawTo;
            if (from > to)
                (from, to) = (to, from);
            if (value >= from && value <= to)
                return true;
        }
        return false;
    }

    private static bool TryNumber(string text, out long value)
    {
        if (text == "*")
        {
            value = -1;
            return true;
        }
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
    }
}
