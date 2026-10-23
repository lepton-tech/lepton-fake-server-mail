using System.Net;
using System.Text;

namespace FakeServerEmail.OAuth;

internal static class OAuthPages
{
    public static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    public static string HiddenInputs(IEnumerable<KeyValuePair<string, string>> values)
    {
        var html = new StringBuilder();
        foreach (var (key, value) in values)
            html.Append("<input type=\"hidden\" name=\"").Append(E(key)).Append("\" value=\"").Append(E(value)).Append("\">");
        return html.ToString();
    }

    public static string AutoSubmitForm(string action, IEnumerable<KeyValuePair<string, string>> values) =>
        $"<!doctype html><html><body onload=\"document.forms[0].submit()\"><form method=\"post\" action=\"{E(action)}\">{HiddenInputs(values)}<noscript><button>Continuar</button></noscript></form></body></html>";

    public static string Page(string title, string body) => $$$"""
        <!doctype html>
        <html lang="pt-BR">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{{E(title)}}} · Fake Server Email</title>
        <link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'%3E%3Crect x='2' y='6' width='28' height='20' rx='4' fill='%232f6fde'/%3E%3Cpath d='M5 10l11 8 11-8' stroke='white' stroke-width='2.4' fill='none' stroke-linecap='round' stroke-linejoin='round'/%3E%3C/svg%3E">
        <style>
        :root{color-scheme:light dark;--bg:#f4f5f7;--card:#fff;--text:#1d2330;--muted:#5d6473;--line:#dfe2e8;--accent:#2f6fde;--accent-text:#fff}
        @media (prefers-color-scheme:dark){:root{--bg:#14171c;--card:#1d2128;--text:#e6e8ec;--muted:#9aa1ad;--line:#2e333c;--accent:#5b8ff0;--accent-text:#0d1117}}
        *{box-sizing:border-box}
        body{margin:0;min-height:100vh;display:grid;place-items:center;background:var(--bg);color:var(--text);font:15px/1.5 "Segoe UI",system-ui,sans-serif;padding:16px}
        main{width:100%;max-width:420px;background:var(--card);border:1px solid var(--line);border-radius:12px;padding:28px}
        .brand{font-size:13px;color:var(--muted);margin-bottom:6px}
        h1{font-size:22px;margin:0 0 12px}
        .lead{margin:0 0 18px}
        label{display:block;font-size:13px;color:var(--muted);margin:14px 0 6px}
        input{width:100%;padding:10px 12px;border:1px solid var(--line);border-radius:8px;background:transparent;color:inherit;font:inherit}
        .meta{display:flex;flex-direction:column;gap:2px;margin-top:14px;font-size:13px}
        .meta span{color:var(--muted)}
        code{font-family:Consolas,monospace;font-size:12.5px;word-break:break-all}
        .actions{display:flex;gap:10px;justify-content:flex-end;margin-top:22px}
        button{border:0;border-radius:8px;padding:10px 18px;font:inherit;font-weight:600;cursor:pointer;background:var(--accent);color:var(--accent-text)}
        button.secondary{background:transparent;color:var(--text);border:1px solid var(--line)}
        .note{font-size:12.5px;color:var(--muted);margin:18px 0 0}
        a{color:var(--accent)}
        </style>
        </head>
        <body><main><div class="brand">Fake Server Email · OAuth de teste</div><h1>{{{E(title)}}}</h1>{{{body}}}</main></body>
        </html>
        """;
}
