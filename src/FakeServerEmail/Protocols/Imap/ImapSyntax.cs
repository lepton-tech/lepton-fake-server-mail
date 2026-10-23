using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace FakeServerEmail.Protocols.Imap;

internal sealed class ImapParseException(string message) : Exception(message);

internal sealed class ImapLiteralTooBigException(string tag, long size, bool synchronizing) : Exception("Literal grande demais")
{
    public string Tag { get; } = tag;
    public long Size { get; } = size;
    public bool Synchronizing { get; } = synchronizing;
}

internal enum ImapTokenType { Atom, Quoted, List }

internal sealed class ImapToken
{
    public ImapTokenType Type { get; init; }
    public string Text { get; init; } = "";
    public byte[]? Bytes { get; init; }
    public List<ImapToken> Items { get; } = [];

    public bool IsNil => Type == ImapTokenType.Atom && Text.Equals("NIL", StringComparison.OrdinalIgnoreCase);

    public string Upper => Text.ToUpperInvariant();

    public override string ToString() => Type == ImapTokenType.List ? "(" + string.Join(' ', Items) + ")" : Text;
}

internal static class ImapSyntax
{
    public static T RequireNumber<T>(string text) where T : IBinaryInteger<T> =>
        T.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw new ImapParseException($"Número inválido: {text}");

    public static bool MatchesMailboxPattern(string pattern, string name)
    {
        var builder = new StringBuilder(pattern.Length);
        var literals = 0;
        foreach (var ch in pattern)
        {
            var wildcard = IsWildcard(ch);
            if (wildcard && builder.Length > 0 && IsWildcard(builder[^1]))
            {
                if (ch == '*')
                    builder[^1] = '*';
                continue;
            }
            if (!wildcard)
                literals++;
            builder.Append(ch);
        }
        if (literals > name.Length)
            return false;

        var compact = builder.ToString();
        var states = new bool[compact.Length + 1];
        var next = new bool[compact.Length + 1];
        states[0] = true;
        FollowWildcards(compact, states);
        foreach (var ch in name)
        {
            Array.Clear(next);
            var alive = false;
            for (var i = 0; i < compact.Length; i++)
            {
                if (!states[i])
                    continue;
                var p = compact[i];
                if (p == '*' || (p == '%' && ch != '/'))
                    next[i] = alive = true;
                else if (!IsWildcard(p) && char.ToUpperInvariant(p) == char.ToUpperInvariant(ch))
                    next[i + 1] = alive = true;
            }
            if (!alive)
                return false;
            FollowWildcards(compact, next);
            (states, next) = (next, states);
        }
        return states[compact.Length];
    }

    private static bool IsWildcard(char ch) => ch is '*' or '%';

    private static void FollowWildcards(string pattern, bool[] states)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            if (states[i] && IsWildcard(pattern[i]))
                states[i + 1] = true;
        }
    }
}

internal sealed partial class ImapCommand
{
    private const int TextLimit = 256 * 1024;
    private const int InitialLiteralCapacity = 64 * 1024;

    public required string Text { get; init; }
    public required List<byte[]> Literals { get; init; }

    public string LogText
    {
        get
        {
            var sb = new StringBuilder(Text.Length);
            var literal = 0;
            foreach (var ch in Text)
            {
                if (ch != '\u0001')
                    sb.Append(ch);
                else if (literal < Literals.Count)
                    sb.Append('{').Append(Literals[literal++].Length.ToString(CultureInfo.InvariantCulture)).Append(" bytes}");
                else
                    sb.Append("{literal}");
            }
            return sb.ToString();
        }
    }

    public static async Task<ImapCommand?> ReadAsync(MailConnection connection, Func<Task> sendContinuation, long literalLimit, CancellationToken ct)
    {
        var text = new StringBuilder();
        var literals = new List<byte[]>();
        var literalBytes = 0L;
        while (true)
        {
            var line = await connection.ReadLineAsync(ct, Math.Max(0, TextLimit - text.Length));
            if (line is null)
                return null;
            var match = LiteralSuffix().Match(line);
            if (!match.Success)
            {
                text.Append(line);
                return new ImapCommand { Text = text.ToString(), Literals = literals };
            }

            text.Append(line, 0, match.Index);
            var size = ImapSyntax.RequireNumber<long>(match.Groups[1].Value);
            var synchronizing = match.Groups[2].Length == 0;
            literalBytes += size;
            if (literalBytes > literalLimit)
                throw new ImapLiteralTooBigException(Tag(text), size, synchronizing);

            if (synchronizing)
                await sendContinuation();
            var literal = await ReadLiteralAsync(connection, size, ct);
            if (literal is null)
                return null;
            literals.Add(literal);
            text.Append('\u0001');
        }
    }

    private static async Task<byte[]?> ReadLiteralAsync(MailConnection connection, long size, CancellationToken ct)
    {
        var literal = new byte[Math.Min(size, InitialLiteralCapacity)];
        var filled = 0;
        while (filled < size)
        {
            if (filled == literal.Length)
                Array.Resize(ref literal, (int)Math.Min(size, literal.Length * 2L));
            using var slice = new MemoryStream(literal, filled, literal.Length - filled);
            if (!await connection.CopyExactAsync(literal.Length - filled, slice, ct))
                return null;
            filled = literal.Length;
        }
        return literal;
    }

    private static string Tag(StringBuilder text)
    {
        var line = text.ToString();
        var space = line.IndexOf(' ');
        return space > 0 ? line[..space] : "*";
    }

    [GeneratedRegex(@"\{(\d{1,10})(\+?)\}$")]
    private static partial Regex LiteralSuffix();
}

internal sealed class ImapTokenizer(string text, List<byte[]> literals)
{
    private const int MaxListDepth = 64;
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

    public ImapToken? Next() => Next(0);

    public ImapToken Require(string what) => Next() ?? throw new ImapParseException($"Faltou {what}");

    public string RequireAString(string what)
    {
        var token = Require(what);
        if (token.Type == ImapTokenType.List)
            throw new ImapParseException($"{what} inválido");
        return token.Text;
    }

    private ImapToken? Next(int depth)
    {
        SkipSpaces();
        if (_position >= text.Length)
            return null;

        var ch = text[_position];
        if (ch == '(')
        {
            if (depth >= MaxListDepth)
                throw new ImapParseException("Listas aninhadas demais");
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
                list.Items.Add(Next(depth + 1) ?? throw new ImapParseException("Lista incompleta"));
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
            return new ImapToken { Type = ImapTokenType.Quoted, Text = sb.ToString() };
        }

        if (ch == '\u0001')
        {
            _position++;
            if (_literal >= literals.Count)
                throw new ImapParseException("Literal ausente");
            var bytes = literals[_literal++];
            return new ImapToken { Type = ImapTokenType.Quoted, Text = Utf8.GetString(bytes), Bytes = bytes };
        }

        var start = _position;
        var brackets = 0;
        while (_position < text.Length)
        {
            var c = text[_position];
            if (c == '[')
                brackets++;
            else if (c == ']')
                brackets = Math.Max(0, brackets - 1);
            else if (brackets == 0 && (c == ' ' || c == '(' || c == ')'))
                break;
            _position++;
        }
        return new ImapToken { Type = ImapTokenType.Atom, Text = text[start.._position] };
    }

    private void SkipSpaces()
    {
        while (_position < text.Length && text[_position] == ' ')
            _position++;
    }
}

internal sealed class ImapWriter : IDisposable
{
    private const int MaxQuotedLength = 1024;
    private static readonly UTF8Encoding Utf8 = new(false, false);
    private readonly MemoryStream _buffer = new();

    public ReadOnlySpan<byte> Written => _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length);

    public long Length => _buffer.Length;

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

    public ImapWriter NString(string? value)
    {
        if (value is null)
            return Raw("NIL");

        var safe = value.Length < MaxQuotedLength;
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

    public void Dispose() => _buffer.Dispose();
}

internal sealed class SequenceSet
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
