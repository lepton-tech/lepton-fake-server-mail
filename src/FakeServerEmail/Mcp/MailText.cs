using System.Net;
using System.Text.RegularExpressions;

namespace FakeServerEmail.Mcp;

internal static partial class MailText
{
    private const int MinTruncateLength = 100;
    private const int MaxLinks = 100;

    public static List<string> Links(string? html, string? text)
    {
        var links = new List<string>();
        if (html is not null)
            links.AddRange(HrefRegex().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value.Trim())));
        if (text is not null)
            links.AddRange(UrlRegex().Matches(text).Select(m => m.Value.TrimEnd('.', ',', ';', ':')));
        return links.Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("/api/", StringComparison.Ordinal)).Distinct().Take(MaxLinks).ToList();
    }

    public static string HtmlToText(string html)
    {
        var text = ScriptStyleRegex().Replace(html, "");
        text = BreakRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        return BlankLinesRegex().Replace(text, "\n\n").Trim();
    }

    public static string Truncate(string value, int max)
    {
        max = Math.Max(MinTruncateLength, max);
        return value.Length <= max ? value : value[..max] + $"\n… (truncado: {value.Length - max} caracteres omitidos)";
    }

    [GeneratedRegex(@"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex(@"https?://[^\s<>""'\)\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<\s*(br|/p|/div|/tr|/h[1-6]|/li)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\n\s*\n\s*\n+")]
    private static partial Regex BlankLinesRegex();
}
