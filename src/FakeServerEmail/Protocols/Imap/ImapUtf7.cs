using System.Text;

namespace FakeServerEmail.Protocols.Imap;

public static class ImapUtf7
{
    public static string Encode(string value)
    {
        var sb = new StringBuilder();
        var pending = new StringBuilder();

        void FlushPending()
        {
            if (pending.Length == 0)
                return;
            var bytes = Encoding.BigEndianUnicode.GetBytes(pending.ToString());
            sb.Append('&').Append(Convert.ToBase64String(bytes).TrimEnd('=').Replace('/', ',')).Append('-');
            pending.Clear();
        }

        foreach (var ch in value)
        {
            if (ch is >= ' ' and <= '~')
            {
                FlushPending();
                sb.Append(ch == '&' ? "&-" : ch.ToString());
            }
            else
            {
                pending.Append(ch);
            }
        }
        FlushPending();
        return sb.ToString();
    }

    public static string Decode(string value)
    {
        if (!value.Contains('&'))
            return value;

        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '&')
            {
                sb.Append(value[i]);
                continue;
            }

            var end = value.IndexOf('-', i + 1);
            if (end < 0)
            {
                sb.Append(value[i..]);
                break;
            }
            if (end == i + 1)
            {
                sb.Append('&');
                i = end;
                continue;
            }

            var b64 = value[(i + 1)..end].Replace(',', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            try
            {
                sb.Append(Encoding.BigEndianUnicode.GetString(Convert.FromBase64String(b64)));
            }
            catch (FormatException)
            {
                sb.Append(value[i..(end + 1)]);
            }
            i = end;
        }
        return sb.ToString();
    }
}
