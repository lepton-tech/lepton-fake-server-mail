using System.Text;
using System.Text.Json.Nodes;
using FakeServerEmail.Core;
using FakeServerEmail.OAuth;

namespace FakeServerEmail.Protocols;

public sealed record AuthResult(bool Success, string? User, string? Error)
{
    public static AuthResult Ok(string? user) => new(true, user, null);

    public static AuthResult Fail(string error, string? user = null) => new(false, user, error);
}

public sealed class Authenticator(SettingsStore settings, TokenService tokens)
{
    public AuthResult ValidatePassword(string user, string password)
    {
        var current = settings.Current;
        if (current.AcceptAnyPassword)
            return AuthResult.Ok(user);
        var account = current.Users.FirstOrDefault(u => u.Username.Equals(user, StringComparison.OrdinalIgnoreCase));
        if (account is null)
            return AuthResult.Fail($"Usuário '{user}' não cadastrado", user);
        return account.Password == password ? AuthResult.Ok(account.Username) : AuthResult.Fail($"Senha incorreta para '{user}'", user);
    }

    public AuthResult ValidateToken(string? user, string token)
    {
        if (!settings.Current.ValidateOAuthTokens)
            return AuthResult.Ok(user ?? tokens.Validate(token).User ?? "oauth");

        var result = tokens.Validate(token);
        if (!result.Valid)
            return AuthResult.Fail(result.Error ?? "Token inválido", user);
        if (!string.IsNullOrEmpty(user) && result.User is not null && !result.User.Equals(user, StringComparison.OrdinalIgnoreCase))
            return AuthResult.Fail($"O token pertence a '{result.User}', mas o login foi feito como '{user}'", user);
        return AuthResult.Ok(string.IsNullOrEmpty(user) ? result.User ?? result.ClientId : user);
    }
}

public sealed record SaslOutcome(bool Success, bool Cancelled, string? User, string Mechanism, string? Error);

public static class Sasl
{
    public static readonly string[] Mechanisms = ["PLAIN", "LOGIN", "XOAUTH2", "OAUTHBEARER"];

    public static bool IsSupported(string mechanism) => Mechanisms.Contains(mechanism, StringComparer.OrdinalIgnoreCase);

    public static bool IsOffered(AppSettings settings, string mechanism) => settings.AuthMechanisms.Contains(mechanism, StringComparer.OrdinalIgnoreCase);

    public static async Task<SaslOutcome> AuthenticateAsync(string mechanism, string? initialResponse, Func<string, Task<string?>> challenge, Authenticator authenticator, string openIdConfigurationUrl)
    {
        mechanism = mechanism.ToUpperInvariant();
        try
        {
            return mechanism switch
            {
                "PLAIN" => await PlainAsync(initialResponse, challenge, authenticator),
                "LOGIN" => await LoginAsync(initialResponse, challenge, authenticator),
                "XOAUTH2" => await XOAuth2Async(initialResponse, challenge, authenticator, openIdConfigurationUrl),
                "OAUTHBEARER" => await OAuthBearerAsync(initialResponse, challenge, authenticator, openIdConfigurationUrl),
                _ => new SaslOutcome(false, false, null, mechanism, "Mecanismo não suportado"),
            };
        }
        catch (FormatException)
        {
            return new SaslOutcome(false, false, null, mechanism, "Resposta Base64 inválida");
        }
        catch (SaslCancelledException)
        {
            return new SaslOutcome(false, true, null, mechanism, "Autenticação cancelada pelo cliente");
        }
    }

    private sealed class SaslCancelledException : Exception;

    private static async Task<byte[]> NextAsync(string? initial, string challengeBase64, Func<string, Task<string?>> challenge)
    {
        var response = initial ?? await challenge(challengeBase64) ?? throw new SaslCancelledException();
        response = response.Trim();
        if (response == "*")
            throw new SaslCancelledException();
        if (response is "" or "=")
            return [];
        return Convert.FromBase64String(response);
    }

    private static async Task<SaslOutcome> PlainAsync(string? initial, Func<string, Task<string?>> challenge, Authenticator authenticator)
    {
        var data = Encoding.UTF8.GetString(await NextAsync(initial, "", challenge));
        var parts = data.Split('\0');
        if (parts.Length != 3)
            return new SaslOutcome(false, false, null, "PLAIN", "Formato PLAIN inválido");
        var user = string.IsNullOrEmpty(parts[1]) ? parts[0] : parts[1];
        var result = authenticator.ValidatePassword(user, parts[2]);
        return new SaslOutcome(result.Success, false, result.User ?? user, "PLAIN", result.Error);
    }

    private static async Task<SaslOutcome> LoginAsync(string? initial, Func<string, Task<string?>> challenge, Authenticator authenticator)
    {
        var user = Encoding.UTF8.GetString(await NextAsync(initial, Convert.ToBase64String("Username:"u8), challenge));
        var password = Encoding.UTF8.GetString(await NextAsync(null, Convert.ToBase64String("Password:"u8), challenge));
        var result = authenticator.ValidatePassword(user, password);
        return new SaslOutcome(result.Success, false, result.User ?? user, "LOGIN", result.Error);
    }

    private static async Task<SaslOutcome> XOAuth2Async(string? initial, Func<string, Task<string?>> challenge, Authenticator authenticator, string openIdConfigurationUrl)
    {
        var data = Encoding.UTF8.GetString(await NextAsync(initial, "", challenge));
        string? user = null;
        string? token = null;
        foreach (var field in data.Split('\u0001', StringSplitOptions.RemoveEmptyEntries))
        {
            if (field.StartsWith("user=", StringComparison.Ordinal))
                user = field[5..];
            else if (field.StartsWith("auth=", StringComparison.Ordinal))
                token = StripBearer(field[5..]);
        }

        if (token is null)
            return new SaslOutcome(false, false, user, "XOAUTH2", "Formato XOAUTH2 inválido (faltou auth=Bearer)");

        var result = authenticator.ValidateToken(user, token);
        if (result.Success)
            return new SaslOutcome(true, false, result.User, "XOAUTH2", null);

        var error = new JsonObject { ["status"] = "401", ["schemes"] = "bearer", ["scope"] = "https://mail.google.com/" };
        await challenge(Convert.ToBase64String(Encoding.UTF8.GetBytes(error.ToJsonString())));
        return new SaslOutcome(false, false, user, "XOAUTH2", result.Error);
    }

    private static async Task<SaslOutcome> OAuthBearerAsync(string? initial, Func<string, Task<string?>> challenge, Authenticator authenticator, string openIdConfigurationUrl)
    {
        var data = Encoding.UTF8.GetString(await NextAsync(initial, "", challenge));
        var separator = data.IndexOf('\u0001');
        if (separator < 0)
            return new SaslOutcome(false, false, null, "OAUTHBEARER", "Formato OAUTHBEARER inválido");

        string? user = null;
        foreach (var attr in data[..separator].Split(','))
        {
            if (attr.StartsWith("a=", StringComparison.Ordinal))
                user = attr[2..].Replace("=2C", ",").Replace("=3D", "=");
        }

        string? token = null;
        foreach (var field in data[(separator + 1)..].Split('\u0001', StringSplitOptions.RemoveEmptyEntries))
        {
            if (field.StartsWith("auth=", StringComparison.Ordinal))
                token = StripBearer(field[5..]);
        }

        if (token is null)
            return new SaslOutcome(false, false, user, "OAUTHBEARER", "Formato OAUTHBEARER inválido (faltou auth=Bearer)");

        var result = authenticator.ValidateToken(user, token);
        if (result.Success)
            return new SaslOutcome(true, false, result.User, "OAUTHBEARER", null);

        var error = new JsonObject { ["status"] = "invalid_token", ["scope"] = "", ["openid-configuration"] = openIdConfigurationUrl };
        await challenge(Convert.ToBase64String(Encoding.UTF8.GetBytes(error.ToJsonString())));
        return new SaslOutcome(false, false, user, "OAUTHBEARER", result.Error);
    }

    private static string StripBearer(string value)
    {
        value = value.Trim();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim() : value;
    }
}
