using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace FakeServerEmail.OAuth;

public sealed partial class OAuthServer
{
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private const string FormPost = "form_post";
    private const string PlainChallenge = "plain";
    private const string S256Challenge = "S256";
    private static readonly TimeSpan AuthorizationCodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly string[] BlockedRedirectSchemes = ["javascript", "data", "vbscript", "file", "blob", "about"];

    private sealed record AuthorizationCode(string ClientId, string RedirectUri, string Scope, string User, string? Challenge, string? ChallengeMethod, string? Nonce, string Issuer, DateTimeOffset ExpiresAt);

    private static Task DiscoveryAsync(HttpContext http, string path)
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
            ["response_modes_supported"] = new JsonArray("query", "fragment", FormPost),
            ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token", "client_credentials", "password", DeviceGrant),
            ["subject_types_supported"] = new JsonArray("public"),
            ["id_token_signing_alg_values_supported"] = new JsonArray("RS256"),
            ["token_endpoint_auth_methods_supported"] = new JsonArray("client_secret_post", "client_secret_basic", "none"),
            ["code_challenge_methods_supported"] = new JsonArray(S256Challenge, PlainChallenge),
            ["scopes_supported"] = new JsonArray("openid", "profile", "email", "offline_access", "https://outlook.office.com/SMTP.Send", "https://outlook.office.com/IMAP.AccessAsUser.All", "https://outlook.office.com/POP.AccessAsUser.All", "https://mail.google.com/"),
            ["claims_supported"] = new JsonArray("sub", "iss", "aud", "exp", "iat", "email", "name", "preferred_username", "upn"),
        };
        return WriteJsonAsync(http, StatusCodes.Status200OK, document);
    }

    private async Task AuthorizeAsync(HttpContext http, bool isPost)
    {
        var p = isPost ? await ReadFormAsync(http) : QueryValues(http);
        var clientId = p["client_id"];
        var redirectUri = p["redirect_uri"];
        var state = p["state"];
        var scope = p["scope"];
        var responseMode = p["response_mode"];

        if (!IsAllowedRedirect(redirectUri, responseMode))
        {
            await WriteHtmlAsync(http, StatusCodes.Status400BadRequest, OAuthPages.Page("Requisição inválida", "<p>O parâmetro <code>redirect_uri</code> está ausente, é inválido ou usa um endereço que não é permitido.</p>"));
            return;
        }

        if (!p["response_type"].Split(' ').Contains("code"))
        {
            await RedirectWithAsync(http, redirectUri, responseMode, new() { ["error"] = "unsupported_response_type", ["error_description"] = "Use response_type=code", ["state"] = state });
            return;
        }

        var challengeMethod = p.Optional("code_challenge_method");
        if (p.Optional("code_challenge") is not null && challengeMethod is not (null or PlainChallenge or S256Challenge))
        {
            await RedirectWithAsync(http, redirectUri, responseMode, new() { ["error"] = "invalid_request", ["error_description"] = "code_challenge_method não suportado: use S256 ou plain", ["state"] = state });
            return;
        }

        if (!isPost)
        {
            await ShowConsentAsync(http, p, clientId, scope, redirectUri);
            return;
        }

        if (p["fake_decision"] != "allow")
        {
            log.Report($"OAuth  autorização negada para o cliente {clientId}");
            await RedirectWithAsync(http, redirectUri, responseMode, new() { ["error"] = "access_denied", ["error_description"] = "O usuário negou o acesso", ["state"] = state });
            return;
        }

        var fakeUser = p["fake_user"].Trim();
        if (fakeUser.Length == 0)
            fakeUser = _lastUser;
        _lastUser = fakeUser;

        var code = "fac_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
        _codes[code] = new AuthorizationCode(clientId, redirectUri, scope, fakeUser, p.Optional("code_challenge"), challengeMethod, p.Optional("nonce"), Issuer(http), DateTimeOffset.UtcNow.Add(AuthorizationCodeLifetime));
        log.Report($"OAuth  código de autorização emitido para {fakeUser} (cliente {clientId})");
        await RedirectWithAsync(http, redirectUri, responseMode, new() { ["code"] = code, ["state"] = state });
    }

    private Task ShowConsentAsync(HttpContext http, FormValues p, string clientId, string scope, string redirectUri)
    {
        var hint = p["login_hint"];
        var user = string.IsNullOrEmpty(hint) ? _lastUser : hint;
        var body = $"""
            <p class="lead">O aplicativo <b>{OAuthPages.E(string.IsNullOrEmpty(clientId) ? "(sem client_id)" : clientId)}</b> quer acessar sua conta.</p>
            <form method="post">
              {OAuthPages.HiddenInputs(p.All)}
              <label>E-mail do usuário simulado</label>
              <input type="email" name="fake_user" value="{OAuthPages.E(user)}" required autofocus>
              <div class="meta"><span>Escopos</span><code>{OAuthPages.E(string.IsNullOrEmpty(scope) ? "(nenhum)" : scope)}</code></div>
              <div class="meta"><span>Redirecionar para</span><code>{OAuthPages.E(redirectUri)}</code></div>
              <div class="actions">
                <button type="submit" name="fake_decision" value="deny" class="secondary">Negar</button>
                <button type="submit" name="fake_decision" value="allow">Autorizar</button>
              </div>
            </form>
            <p class="note">Nenhuma senha é necessária: este é um servidor OAuth falso para testes.</p>
            """;
        return WriteHtmlAsync(http, StatusCodes.Status200OK, OAuthPages.Page("Entrar", body));
    }

    private static bool IsAllowedRedirect(string redirectUri, string responseMode)
    {
        if (string.IsNullOrEmpty(redirectUri) || !Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
            return false;
        if (BlockedRedirectSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
            return false;
        return responseMode != FormPost || uri.Scheme is "http" or "https";
    }

    private static Task RedirectWithAsync(HttpContext http, string redirectUri, string responseMode, Dictionary<string, string> values)
    {
        var pairs = values.Where(v => !string.IsNullOrEmpty(v.Value)).ToList();
        if (responseMode == FormPost)
            return WriteHtmlAsync(http, StatusCodes.Status200OK, OAuthPages.AutoSubmitForm(redirectUri, pairs));

        var query = string.Join('&', pairs.Select(v => $"{Uri.EscapeDataString(v.Key)}={Uri.EscapeDataString(v.Value)}"));
        var separator = responseMode == "fragment" ? "#" : redirectUri.Contains('?') ? "&" : "?";
        http.Response.Redirect(redirectUri + separator + query);
        return Task.CompletedTask;
    }
}
