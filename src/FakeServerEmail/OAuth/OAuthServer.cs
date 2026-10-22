using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FakeServerEmail.Core;
using Microsoft.AspNetCore.Http;

namespace FakeServerEmail.OAuth;

public sealed partial class OAuthServer(ServerContext context)
{
    private sealed record AuthorizationCode(string ClientId, string RedirectUri, string Scope, string User, string? Challenge, string? ChallengeMethod, string? Nonce, string Issuer, DateTimeOffset ExpiresAt);

    private sealed class DeviceAuthorization
    {
        public required string DeviceCode { get; init; }
        public required string UserCode { get; init; }
        public required string ClientId { get; init; }
        public required string Scope { get; init; }
        public required string Issuer { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public string Status { get; set; } = "pending";
        public string? User { get; set; }
    }

    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly ConcurrentDictionary<string, AuthorizationCode> _codes = new();
    private readonly ConcurrentDictionary<string, DeviceAuthorization> _devices = new();
    private string _lastUser = "usuario@teste.local";

    public async Task<bool> TryHandleAsync(HttpContext http)
    {
        var path = (http.Request.Path.Value ?? "/").TrimEnd('/');
        var lower = path.ToLowerInvariant();
        if (lower.StartsWith("/api/", StringComparison.Ordinal))
            return false;

        var method = http.Request.Method;
        var isPost = HttpMethods.IsPost(method);
        var isGet = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

        if (isGet && (lower.EndsWith("/.well-known/openid-configuration") || lower.EndsWith("/.well-known/oauth-authorization-server")))
            await Discovery(http, path);
        else if ((isGet || isPost) && (lower.EndsWith("/authorize") || lower.EndsWith("/o/oauth2/v2/auth") || lower.EndsWith("/o/oauth2/auth")))
            await Authorize(http, isPost);
        else if (isPost && lower.EndsWith("/token"))
            await Token(http);
        else if (isPost && (lower.EndsWith("/devicecode") || lower.EndsWith("/device/code")))
            await DeviceCode(http);
        else if ((isGet || isPost) && lower is "/device" or "/devicelogin")
            await DevicePage(http, isPost);
        else if (isGet && (lower.EndsWith("/jwks") || lower.EndsWith("/keys") || lower.EndsWith("/certs") || lower.EndsWith("/jwks.json")))
            await Json(http, 200, context.Tokens.Jwks());
        else if ((isGet || isPost) && lower.EndsWith("/userinfo"))
            await UserInfo(http);
        else if (isPost && lower.EndsWith("/revoke"))
            await Revoke(http);
        else
            return false;
        return true;
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
        var path = http.Request.Path.Value ?? "/";
        var tenant = Tenant(path);
        return tenant is null ? BaseUrl(http) : $"{BaseUrl(http)}/{tenant}/v2.0";
    }

    private async Task Discovery(HttpContext http, string path)
    {
        var baseUrl = BaseUrl(http);
        var tenant = Tenant(path);
        var prefix = tenant is null ? $"{baseUrl}/oauth2" : $"{baseUrl}/{tenant}/oauth2/v2.0";
        var document = new JsonObject
        {
            ["issuer"] = Issuer(http),
            ["authorization_endpoint"] = $"{prefix}/authorize",
            ["token_endpoint"] = $"{prefix}/token",
            ["device_authorization_endpoint"] = $"{prefix}/devicecode",
            ["userinfo_endpoint"] = $"{baseUrl}/oauth2/userinfo",
            ["revocation_endpoint"] = $"{baseUrl}/oauth2/revoke",
            ["jwks_uri"] = tenant is null ? $"{baseUrl}/oauth2/jwks" : $"{baseUrl}/{tenant}/discovery/v2.0/keys",
            ["response_types_supported"] = new JsonArray("code", "id_token", "code id_token"),
            ["response_modes_supported"] = new JsonArray("query", "fragment", "form_post"),
            ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token", "client_credentials", "password", DeviceGrant),
            ["subject_types_supported"] = new JsonArray("public"),
            ["id_token_signing_alg_values_supported"] = new JsonArray("RS256"),
            ["token_endpoint_auth_methods_supported"] = new JsonArray("client_secret_post", "client_secret_basic", "none"),
            ["code_challenge_methods_supported"] = new JsonArray("S256", "plain"),
            ["scopes_supported"] = new JsonArray("openid", "profile", "email", "offline_access", "https://outlook.office.com/SMTP.Send", "https://outlook.office.com/IMAP.AccessAsUser.All", "https://outlook.office.com/POP.AccessAsUser.All", "https://mail.google.com/"),
            ["claims_supported"] = new JsonArray("sub", "iss", "aud", "exp", "iat", "email", "name", "preferred_username", "upn"),
        };
        await Json(http, 200, document);
    }

    private async Task Authorize(HttpContext http, bool isPost)
    {
        var p = isPost ? await FormAsync(http) : http.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString());
        string Get(string key) => p.TryGetValue(key, out var v) ? v : "";

        var clientId = Get("client_id");
        var redirectUri = Get("redirect_uri");
        var responseType = Get("response_type");
        var state = Get("state");
        var scope = Get("scope");
        var responseMode = Get("response_mode");

        if (string.IsNullOrEmpty(redirectUri) || !Uri.TryCreate(redirectUri, UriKind.Absolute, out _))
        {
            await Html(http, 400, Page("Requisição inválida", "<p>O parâmetro <code>redirect_uri</code> está ausente ou inválido.</p>"));
            return;
        }

        if (!responseType.Split(' ').Contains("code"))
        {
            await RedirectWith(http, redirectUri, responseMode, new() { ["error"] = "unsupported_response_type", ["error_description"] = "Use response_type=code", ["state"] = state });
            return;
        }

        if (!isPost)
        {
            var hint = Get("login_hint");
            var user = string.IsNullOrEmpty(hint) ? _lastUser : hint;
            var hidden = new StringBuilder();
            foreach (var (key, value) in p)
                hidden.Append($"<input type=\"hidden\" name=\"{E(key)}\" value=\"{E(value)}\">");
            var body = $"""
                <p class="lead">O aplicativo <b>{E(string.IsNullOrEmpty(clientId) ? "(sem client_id)" : clientId)}</b> quer acessar sua conta.</p>
                <form method="post">
                  {hidden}
                  <label>E-mail do usuário simulado</label>
                  <input type="email" name="fake_user" value="{E(user)}" required autofocus>
                  <div class="meta"><span>Escopos</span><code>{E(string.IsNullOrEmpty(scope) ? "(nenhum)" : scope)}</code></div>
                  <div class="meta"><span>Redirecionar para</span><code>{E(redirectUri)}</code></div>
                  <div class="actions">
                    <button type="submit" name="fake_decision" value="deny" class="secondary">Negar</button>
                    <button type="submit" name="fake_decision" value="allow">Autorizar</button>
                  </div>
                </form>
                <p class="note">Nenhuma senha é necessária: este é um servidor OAuth falso para testes.</p>
                """;
            await Html(http, 200, Page("Entrar", body));
            return;
        }

        if (Get("fake_decision") != "allow")
        {
            context.Log.Report($"OAuth  autorização negada para o cliente {clientId}");
            await RedirectWith(http, redirectUri, responseMode, new() { ["error"] = "access_denied", ["error_description"] = "O usuário negou o acesso", ["state"] = state });
            return;
        }

        var fakeUser = Get("fake_user").Trim();
        if (fakeUser.Length == 0)
            fakeUser = _lastUser;
        _lastUser = fakeUser;

        var code = "fac_" + TokenService.Base64Url(RandomNumberGenerator.GetBytes(24));
        _codes[code] = new AuthorizationCode(clientId, redirectUri, scope, fakeUser, NullIfEmpty(Get("code_challenge")), NullIfEmpty(Get("code_challenge_method")), NullIfEmpty(Get("nonce")), Issuer(http), DateTimeOffset.UtcNow.AddMinutes(10));
        context.Log.Report($"OAuth  código de autorização emitido para {fakeUser} (cliente {clientId})");
        await RedirectWith(http, redirectUri, responseMode, new() { ["code"] = code, ["state"] = state });
    }

    private static async Task RedirectWith(HttpContext http, string redirectUri, string responseMode, Dictionary<string, string> values)
    {
        var pairs = values.Where(v => !string.IsNullOrEmpty(v.Value)).ToList();
        if (responseMode == "form_post")
        {
            var inputs = string.Concat(pairs.Select(v => $"<input type=\"hidden\" name=\"{E(v.Key)}\" value=\"{E(v.Value)}\">"));
            await Html(http, 200, $"<!doctype html><html><body onload=\"document.forms[0].submit()\"><form method=\"post\" action=\"{E(redirectUri)}\">{inputs}<noscript><button>Continuar</button></noscript></form></body></html>");
            return;
        }
        var query = string.Join('&', pairs.Select(v => $"{Uri.EscapeDataString(v.Key)}={Uri.EscapeDataString(v.Value)}"));
        var separator = responseMode == "fragment" ? "#" : redirectUri.Contains('?') ? "&" : "?";
        http.Response.Redirect(redirectUri + separator + query);
        await Task.CompletedTask;
    }

    private async Task Token(HttpContext http)
    {
        var form = await FormAsync(http);
        string Get(string key) => form.TryGetValue(key, out var v) ? v : "";

        var clientId = Get("client_id");
        var auth = http.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim()));
                var colon = decoded.IndexOf(':');
                clientId = Uri.UnescapeDataString(colon < 0 ? decoded : decoded[..colon]);
            }
            catch (FormatException)
            {
                await Error(http, 401, "invalid_client", "Cabeçalho Authorization Basic inválido");
                return;
            }
        }
        if (string.IsNullOrEmpty(clientId))
            clientId = "cliente-sem-id";

        var lifetime = context.Settings.Current.TokenLifetimeSeconds;
        var grantType = Get("grant_type");
        switch (grantType)
        {
            case "authorization_code":
                {
                    if (!_codes.TryRemove(Get("code"), out var code))
                    {
                        await Error(http, 400, "invalid_grant", "Código de autorização inválido ou já utilizado");
                        return;
                    }
                    if (code.ExpiresAt < DateTimeOffset.UtcNow)
                    {
                        await Error(http, 400, "invalid_grant", "Código de autorização expirado");
                        return;
                    }
                    var redirect = Get("redirect_uri");
                    if (!string.IsNullOrEmpty(redirect) && redirect != code.RedirectUri)
                    {
                        await Error(http, 400, "invalid_grant", "redirect_uri diferente do usado na autorização");
                        return;
                    }
                    if (code.Challenge is not null)
                    {
                        var verifier = Get("code_verifier");
                        var expected = code.ChallengeMethod?.Equals("plain", StringComparison.OrdinalIgnoreCase) == true
                            ? verifier
                            : TokenService.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
                        if (string.IsNullOrEmpty(verifier) || expected != code.Challenge)
                        {
                            await Error(http, 400, "invalid_grant", "code_verifier não confere (PKCE)");
                            return;
                        }
                    }
                    await IssueAsync(http, code.Issuer, string.IsNullOrEmpty(code.ClientId) ? clientId : code.ClientId, code.User, code.Scope, grantType, lifetime, true, code.Nonce);
                    return;
                }

            case "refresh_token":
                {
                    var grant = context.Tokens.FindRefreshToken(Get("refresh_token"));
                    if (grant is null)
                    {
                        await Error(http, 400, "invalid_grant", "Refresh token inválido");
                        return;
                    }
                    var scope = string.IsNullOrEmpty(Get("scope")) ? grant.Scope : Get("scope");
                    await IssueAsync(http, Issuer(http), grant.ClientId, grant.User, scope, grantType, lifetime, true, null);
                    return;
                }

            case "client_credentials":
                await IssueAsync(http, Issuer(http), clientId, null, Get("scope"), grantType, lifetime, false, null);
                return;

            case "password":
                {
                    var username = Get("username");
                    var result = context.Authenticator.ValidatePassword(username, Get("password"));
                    if (string.IsNullOrEmpty(username) || !result.Success)
                    {
                        await Error(http, 400, "invalid_grant", result.Error ?? "Usuário ausente");
                        return;
                    }
                    await IssueAsync(http, Issuer(http), clientId, result.User ?? username, Get("scope"), grantType, lifetime, true, null);
                    return;
                }

            case DeviceGrant:
            case "device_code":
                {
                    if (!_devices.TryGetValue(Get("device_code"), out var device))
                    {
                        await Error(http, 400, "invalid_grant", "device_code inválido");
                        return;
                    }
                    if (device.ExpiresAt < DateTimeOffset.UtcNow)
                    {
                        _devices.TryRemove(device.DeviceCode, out _);
                        await Error(http, 400, "expired_token", "O código de dispositivo expirou");
                        return;
                    }
                    if (device.Status == "pending")
                    {
                        await Error(http, 400, "authorization_pending", "Aguardando o usuário autorizar no navegador");
                        return;
                    }
                    _devices.TryRemove(device.DeviceCode, out _);
                    if (device.Status == "denied")
                    {
                        await Error(http, 400, "access_denied", "O usuário negou o acesso");
                        return;
                    }
                    await IssueAsync(http, device.Issuer, device.ClientId, device.User, device.Scope, "device_code", lifetime, true, null);
                    return;
                }

            default:
                await Error(http, 400, "unsupported_grant_type", $"grant_type '{grantType}' não suportado");
                return;
        }
    }

    private async Task IssueAsync(HttpContext http, string issuer, string clientId, string? user, string scope, string grantType, int lifetime, bool allowRefresh, string? nonce)
    {
        var token = context.Tokens.IssueAccessToken(issuer, clientId, user, scope, grantType, lifetime);
        var response = new JsonObject
        {
            ["token_type"] = "Bearer",
            ["scope"] = scope,
            ["expires_in"] = lifetime,
            ["ext_expires_in"] = lifetime,
            ["access_token"] = token.AccessToken,
        };
        var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (allowRefresh && user is not null)
            response["refresh_token"] = context.Tokens.CreateRefreshToken(clientId, user, scope);
        if (user is not null && scopes.Contains("openid"))
            response["id_token"] = context.Tokens.IssueIdToken(issuer, clientId, user, nonce, lifetime);

        context.Log.Report($"OAuth  token emitido ({grantType}) para {user ?? "aplicativo " + clientId}");
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        await Json(http, 200, response);
    }

    private async Task DeviceCode(HttpContext http)
    {
        var form = await FormAsync(http);
        var clientId = form.GetValueOrDefault("client_id") ?? "cliente-sem-id";
        var scope = form.GetValueOrDefault("scope") ?? "";
        const string alphabet = "BCDFGHJKLMNPQRSTVWXZ";
        var chars = RandomNumberGenerator.GetItems<char>(alphabet, 8);
        var userCode = new string(chars, 0, 4) + "-" + new string(chars, 4, 4);
        var device = new DeviceAuthorization
        {
            DeviceCode = "fdc_" + TokenService.Base64Url(RandomNumberGenerator.GetBytes(24)),
            UserCode = userCode,
            ClientId = clientId,
            Scope = scope,
            Issuer = Issuer(http),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        };
        _devices[device.DeviceCode] = device;

        var verification = $"{BaseUrl(http)}/device";
        context.Log.Report($"OAuth  código de dispositivo {userCode} gerado para o cliente {clientId}");
        await Json(http, 200, new JsonObject
        {
            ["device_code"] = device.DeviceCode,
            ["user_code"] = userCode,
            ["verification_uri"] = verification,
            ["verification_url"] = verification,
            ["verification_uri_complete"] = $"{verification}?user_code={userCode}",
            ["expires_in"] = 900,
            ["interval"] = 2,
            ["message"] = $"Para entrar, abra {verification} e digite o código {userCode}.",
        });
    }

    private async Task DevicePage(HttpContext http, bool isPost)
    {
        if (!isPost)
        {
            var code = E(http.Request.Query["user_code"].ToString());
            var body = $"""
                <p class="lead">Digite o código exibido pelo aplicativo.</p>
                <form method="post">
                  <label>Código</label>
                  <input name="user_code" value="{code}" placeholder="ABCD-EFGH" required autofocus style="text-transform:uppercase;letter-spacing:.15em">
                  <label>E-mail do usuário simulado</label>
                  <input type="email" name="fake_user" value="{E(_lastUser)}" required>
                  <div class="actions">
                    <button type="submit" name="fake_decision" value="deny" class="secondary">Negar</button>
                    <button type="submit" name="fake_decision" value="allow">Autorizar</button>
                  </div>
                </form>
                """;
            await Html(http, 200, Page("Entrar em um dispositivo", body));
            return;
        }

        var form = await FormAsync(http);
        var userCode = (form.GetValueOrDefault("user_code") ?? "").Trim().ToUpperInvariant();
        if (userCode.Length == 8)
            userCode = userCode[..4] + "-" + userCode[4..];
        var device = _devices.Values.FirstOrDefault(d => d.UserCode == userCode && d.Status == "pending" && d.ExpiresAt > DateTimeOffset.UtcNow);
        if (device is null)
        {
            await Html(http, 400, Page("Código inválido", "<p>Código não encontrado ou expirado.</p><p><a href=\"/device\">Tentar novamente</a></p>"));
            return;
        }

        if (form.GetValueOrDefault("fake_decision") == "allow")
        {
            device.User = string.IsNullOrWhiteSpace(form.GetValueOrDefault("fake_user")) ? _lastUser : form["fake_user"].Trim();
            _lastUser = device.User;
            device.Status = "approved";
            await Html(http, 200, Page("Pronto", $"<p class=\"lead\">Acesso autorizado para <b>{E(device.User)}</b>.</p><p>Pode voltar ao aplicativo.</p>"));
        }
        else
        {
            device.Status = "denied";
            await Html(http, 200, Page("Acesso negado", "<p>O aplicativo receberá <code>access_denied</code>.</p>"));
        }
    }

    private async Task UserInfo(HttpContext http)
    {
        var auth = http.Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : "";
        var result = context.Tokens.Validate(token);
        if (!result.Valid)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
            await Json(http, 401, new JsonObject { ["error"] = "invalid_token", ["error_description"] = result.Error });
            return;
        }
        var user = result.User ?? result.ClientId ?? "";
        await Json(http, 200, new JsonObject
        {
            ["sub"] = user,
            ["email"] = user,
            ["email_verified"] = true,
            ["name"] = TokenService.DisplayName(user),
            ["preferred_username"] = user,
        });
    }

    private async Task Revoke(HttpContext http)
    {
        var form = await FormAsync(http);
        var token = form.GetValueOrDefault("token") ?? "";
        var parts = token.Split('.');
        if (parts.Length == 3)
        {
            try
            {
                var payload = JsonNode.Parse(TokenService.FromBase64Url(parts[1]));
                var jti = payload?["jti"]?.GetValue<string>();
                if (jti is not null)
                    context.Tokens.Revoke(jti);
            }
            catch
            {
            }
        }
        http.Response.StatusCode = 200;
    }

    private static async Task<Dictionary<string, string>> FormAsync(HttpContext http)
    {
        if (!http.Request.HasFormContentType)
        {
            if (http.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    var node = await JsonNode.ParseAsync(http.Request.Body);
                    if (node is JsonObject obj)
                        return obj.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
                }
                catch
                {
                }
            }
            return http.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString());
        }
        var form = await http.Request.ReadFormAsync();
        return form.ToDictionary(f => f.Key, f => f.Value.ToString());
    }

    private static Task Error(HttpContext http, int status, string error, string description) =>
        Json(http, status, new JsonObject { ["error"] = error, ["error_description"] = description });

    private static async Task Json(HttpContext http, int status, JsonNode body)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.WriteAsync(body.ToJsonString());
    }

    private static async Task Html(HttpContext http, int status, string html)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "text/html; charset=utf-8";
        await http.Response.WriteAsync(html);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static string Page(string title, string body) => $$$"""
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
