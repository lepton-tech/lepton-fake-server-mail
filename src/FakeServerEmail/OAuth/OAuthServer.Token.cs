using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace FakeServerEmail.OAuth;

public sealed partial class OAuthServer
{
    private sealed record TokenGrant(string Issuer, string ClientId, string? User, string Scope, string GrantType, bool AllowRefresh, string? Nonce = null);

    private async Task TokenAsync(HttpContext http)
    {
        var form = await ReadFormAsync(http);
        var clientId = form["client_id"];
        var auth = http.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim()));
                var colon = decoded.IndexOf(':', StringComparison.Ordinal);
                clientId = Uri.UnescapeDataString(colon < 0 ? decoded : decoded[..colon]);
            }
            catch (FormatException)
            {
                await WriteErrorAsync(http, StatusCodes.Status401Unauthorized, "invalid_client", "Cabeçalho Authorization Basic inválido");
                return;
            }
        }
        if (string.IsNullOrEmpty(clientId))
            clientId = UnknownClientId;

        var grantType = form["grant_type"];
        var task = grantType switch
        {
            "authorization_code" => AuthorizationCodeGrantAsync(http, form, clientId),
            "refresh_token" => RefreshTokenGrantAsync(http, form),
            "client_credentials" => IssueAsync(http, new TokenGrant(Issuer(http), clientId, null, form["scope"], grantType, false)),
            "password" => PasswordGrantAsync(http, form, clientId),
            DeviceGrant or "device_code" => DeviceCodeGrantAsync(http, form),
            _ => WriteErrorAsync(http, StatusCodes.Status400BadRequest, "unsupported_grant_type", $"grant_type '{grantType}' não suportado"),
        };
        await task;
    }

    private Task AuthorizationCodeGrantAsync(HttpContext http, FormValues form, string clientId)
    {
        if (!_codes.TryRemove(form["code"], out var code))
            return WriteErrorAsync(http, StatusCodes.Status400BadRequest, "invalid_grant", "Código de autorização inválido ou já utilizado");
        if (code.ExpiresAt < DateTimeOffset.UtcNow)
            return WriteErrorAsync(http, StatusCodes.Status400BadRequest, "invalid_grant", "Código de autorização expirado");
        var redirect = form["redirect_uri"];
        if (!string.IsNullOrEmpty(redirect) && redirect != code.RedirectUri)
            return WriteErrorAsync(http, StatusCodes.Status400BadRequest, "invalid_grant", "redirect_uri diferente do usado na autorização");
        if (code.Challenge is not null && !VerifierMatches(code, form["code_verifier"]))
            return WriteErrorAsync(http, StatusCodes.Status400BadRequest, "invalid_grant", "code_verifier não confere (PKCE)");
        var owner = string.IsNullOrEmpty(code.ClientId) ? clientId : code.ClientId;
        return IssueAsync(http, new TokenGrant(code.Issuer, owner, code.User, code.Scope, "authorization_code", true, code.Nonce));
    }

    private static bool VerifierMatches(AuthorizationCode code, string verifier)
    {
        if (string.IsNullOrEmpty(verifier))
            return false;
        var expected = (code.ChallengeMethod ?? PlainChallenge) == S256Challenge
            ? Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            : verifier;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(code.Challenge!));
    }

    private Task RefreshTokenGrantAsync(HttpContext http, FormValues form)
    {
        var grant = tokens.FindRefreshToken(form["refresh_token"]);
        if (grant is null)
            return WriteErrorAsync(http, StatusCodes.Status400BadRequest, "invalid_grant", "Refresh token inválido");
        var scope = form.Optional("scope") ?? grant.Scope;
        return IssueAsync(http, new TokenGrant(Issuer(http), grant.ClientId, grant.User, scope, "refresh_token", true));
    }

    private Task PasswordGrantAsync(HttpContext http, FormValues form, string clientId)
    {
        var username = form["username"];
        var result = authenticator.ValidatePassword(username, form["password"]);
        if (string.IsNullOrEmpty(username) || !result.Success)
            return WriteErrorAsync(http, StatusCodes.Status400BadRequest, "invalid_grant", result.Error ?? "Usuário ausente");
        return IssueAsync(http, new TokenGrant(Issuer(http), clientId, result.User ?? username, form["scope"], "password", true));
    }

    private Task IssueAsync(HttpContext http, TokenGrant grant)
    {
        var lifetime = settings.Current.TokenLifetimeSeconds;
        var token = tokens.IssueAccessToken(grant.Issuer, grant.ClientId, grant.User, grant.Scope, grant.GrantType, lifetime);
        var response = new JsonObject
        {
            ["token_type"] = "Bearer",
            ["scope"] = grant.Scope,
            ["expires_in"] = lifetime,
            ["ext_expires_in"] = lifetime,
            ["access_token"] = token.AccessToken,
        };
        if (grant.AllowRefresh && grant.User is not null)
            response["refresh_token"] = tokens.CreateRefreshToken(grant.ClientId, grant.User, grant.Scope);
        if (grant.User is not null && grant.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid"))
            response["id_token"] = tokens.IssueIdToken(grant.Issuer, grant.ClientId, grant.User, grant.Nonce, lifetime);

        log.Report($"OAuth  token emitido ({grant.GrantType}) para {grant.User ?? "aplicativo " + grant.ClientId}");
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        return WriteJsonAsync(http, StatusCodes.Status200OK, response);
    }

    private Task UserInfoAsync(HttpContext http)
    {
        var auth = http.Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : "";
        var result = tokens.Validate(token);
        if (!result.Valid)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
            return WriteJsonAsync(http, StatusCodes.Status401Unauthorized, new JsonObject { ["error"] = "invalid_token", ["error_description"] = result.Error });
        }
        var user = result.User ?? result.ClientId ?? "";
        return WriteJsonAsync(http, StatusCodes.Status200OK, new JsonObject
        {
            ["sub"] = user,
            ["email"] = user,
            ["email_verified"] = true,
            ["name"] = TokenService.DisplayName(user),
            ["preferred_username"] = user,
        });
    }

    private async Task RevokeAsync(HttpContext http)
    {
        var form = await ReadFormAsync(http);
        var parts = form["token"].Split('.');
        if (parts.Length == 3)
        {
            try
            {
                var jti = JsonNode.Parse(Base64Url.DecodeFromChars(parts[1]))?["jti"]?.GetValue<string>();
                if (jti is not null)
                    tokens.Revoke(jti);
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
            {
            }
        }
        http.Response.StatusCode = StatusCodes.Status200OK;
    }
}
