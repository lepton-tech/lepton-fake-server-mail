using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FakeServerEmail.Core;
using FakeServerEmail.Protocols;
using Microsoft.AspNetCore.Http;

namespace FakeServerEmail.OAuth;

public sealed partial class OAuthServer(TokenService tokens, SettingsStore settings, Authenticator authenticator, SessionLog log) : IMiddleware
{
    private const string UnknownClientId = "cliente-sem-id";

    private sealed record Route(string[] Paths, bool ExactPath, bool AllowGet, bool AllowPost, Func<OAuthServer, HttpContext, string, bool, Task> Handle)
    {
        public bool Matches(string path) => ExactPath
            ? Paths.Contains(path, StringComparer.OrdinalIgnoreCase)
            : Paths.Any(p => path.EndsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Route[] Routes =
    [
        new(["/.well-known/openid-configuration", "/.well-known/oauth-authorization-server"], false, true, false, (_, h, path, _) => DiscoveryAsync(h, path)),
        new(["/authorize", "/o/oauth2/v2/auth", "/o/oauth2/auth"], false, true, true, (s, h, _, post) => s.AuthorizeAsync(h, post)),
        new(["/token"], false, false, true, (s, h, _, _) => s.TokenAsync(h)),
        new(["/devicecode", "/device/code"], false, false, true, (s, h, _, _) => s.DeviceCodeAsync(h)),
        new(["/device", "/devicelogin"], true, true, true, (s, h, _, post) => s.DevicePageAsync(h, post)),
        new(["/jwks", "/keys", "/certs", "/jwks.json"], false, true, false, (s, h, _, _) => s.JwksAsync(h)),
        new(["/userinfo"], false, true, true, (s, h, _, _) => s.UserInfoAsync(h)),
        new(["/revoke"], false, false, true, (s, h, _, _) => s.RevokeAsync(h)),
    ];

    private readonly ConcurrentDictionary<string, AuthorizationCode> _codes = new();
    private readonly ConcurrentDictionary<string, DeviceAuthorization> _devices = new();
    private volatile string _lastUser = "usuario@teste.local";

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var path = (context.Request.Path.Value ?? "/").TrimEnd('/');
        var method = context.Request.Method;
        var isPost = HttpMethods.IsPost(method);
        var isGet = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        var route = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            ? null
            : Array.Find(Routes, r => ((isGet && r.AllowGet) || (isPost && r.AllowPost)) && r.Matches(path));
        if (route is null)
        {
            await next(context);
            return;
        }

        ForgetExpired();
        await route.Handle(this, context, path, isPost);
    }

    private Task JwksAsync(HttpContext http) => WriteJsonAsync(http, StatusCodes.Status200OK, tokens.Jwks());

    private void ForgetExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, code) in _codes)
            if (code.ExpiresAt < now)
                _codes.TryRemove(key, out _);
        foreach (var (key, device) in _devices)
            if (device.ExpiresAt < now)
                _devices.TryRemove(key, out _);
    }

    private static string BaseUrl(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}";

    private static string? Tenant(string path)
    {
        var match = TenantRegex().Match(path);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^/([^/]+)/(?:oauth2/v2\.0|v2\.0|discovery/v2\.0)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex TenantRegex();

    private static string Issuer(HttpContext http)
    {
        var tenant = Tenant(http.Request.Path.Value ?? "/");
        return tenant is null ? BaseUrl(http) : $"{BaseUrl(http)}/{tenant}/v2.0";
    }

    private static async Task<FormValues> ReadFormAsync(HttpContext http)
    {
        var request = http.Request;
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(http.RequestAborted);
            return new FormValues(form.ToDictionary(f => f.Key, f => f.Value.ToString()));
        }

        if (request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                if (await JsonNode.ParseAsync(request.Body, cancellationToken: http.RequestAborted) is JsonObject obj)
                    return new FormValues(obj.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? ""));
            }
            catch (JsonException)
            {
            }
        }
        return QueryValues(http);
    }

    private static FormValues QueryValues(HttpContext http) => new(http.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString()));

    private static Task WriteErrorAsync(HttpContext http, int status, string error, string description) =>
        WriteJsonAsync(http, status, new JsonObject { ["error"] = error, ["error_description"] = description });

    private static async Task WriteJsonAsync(HttpContext http, int status, JsonNode body)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.WriteAsync(body.ToJsonString(), http.RequestAborted);
    }

    private static async Task WriteHtmlAsync(HttpContext http, int status, string html)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "text/html; charset=utf-8";
        http.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        await http.Response.WriteAsync(html, http.RequestAborted);
    }

    private sealed class FormValues(Dictionary<string, string> values)
    {
        public string this[string key] => values.TryGetValue(key, out var value) ? value : "";

        public IEnumerable<KeyValuePair<string, string>> All => values;

        public string? Optional(string key) => values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
    }
}
