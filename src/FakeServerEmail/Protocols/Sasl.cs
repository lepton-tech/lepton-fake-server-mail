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
    private const int ResponseLineLimit = 64 * 1024;

    public static IReadOnlyList<string> Mechanisms { get; } = ["PLAIN", "LOGIN", "XOAUTH2", "OAUTHBEARER"];

    public static bool IsSupported(string mechanism) => Mechanisms.Contains(mechanism, StringComparer.OrdinalIgnoreCase);

    public static bool IsOffered(AppSettings settings, string mechanism) => settings.AuthMechanisms.Contains(mechanism, StringComparer.OrdinalIgnoreCase);

    public static async Task<SaslOutcome> ExchangeAsync(MailConnection connection, ServerContext context, string mechanism, string? initialResponse, string challengePrefix, CancellationToken ct)
    {
        var log = connection.Log!;
        var step = initialResponse is null ? 0 : 1;
        SaslOutcome outcome;
        try
        {
            outcome = await AuthenticateAsync(mechanism, initialResponse, async challenge =>
            {
                await connection.SendLineAsync(challengePrefix + challenge, ct);
                var response = await connection.ReadLineAsync(ct, ResponseLineLimit);
                if (response is not null)
                    log.FromClient(DescribeResponse(mechanism, step++, response));
                return response;
            }, context.Authenticator, context.OpenIdConfigurationUrl);
        }
        catch (LineTooLongException)
        {
            outcome = new SaslOutcome(false, false, null, mechanism.ToUpperInvariant(), "Resposta longa demais");
        }

        if (!outcome.Success && !outcome.Cancelled)
            log.Info($"Falha na autenticação {outcome.Mechanism}: {outcome.Error}");
        return outcome;
    }

    public static string MaskArguments(string arguments)
    {
        var parts = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? arguments.Trim() : $"{parts[0]} {DescribeResponse(parts[0], 0, parts[1])}";
    }

    public static string DescribeResponse(string mechanism, int step, string response)
    {
        var trimmed = response.Trim();
        if (trimmed is "" or "=" or "*")
            return trimmed;

        var data = TryDecode(trimmed);
        var name = mechanism.ToUpperInvariant();
        if (name == "LOGIN" && step > 0)
            return "[senha oculta]";
        if (name == "LOGIN")
            return string.IsNullOrEmpty(data) ? "[credenciais ocultas]" : $"[usuário '{data}']";

        var user = step > 0 || data is null ? null : name switch
        {
            "PLAIN" => ParsePlain(data)?.User,
            "XOAUTH2" => ParseXOAuth2(data).User,
            "OAUTHBEARER" => ParseOAuthBearer(data)?.User,
            _ => null,
        };
        return string.IsNullOrEmpty(user) ? "[credenciais ocultas]" : $"[credenciais de '{user}' ocultas]";
    }

    public static async Task<SaslOutcome> AuthenticateAsync(string mechanism, string? initialResponse, Func<string, Task<string?>> challenge, Authenticator authenticator, string openIdConfigurationUrl)
    {
        mechanism = mechanism.ToUpperInvariant();
        try
        {
            return mechanism switch
            {
                "PLAIN" => await PlainAsync(initialResponse, challenge, authenticator),
                "LOGIN" => await LoginAsync(initialResponse, challenge, authenticator),
                "XOAUTH2" => await XOAuth2Async(initialResponse, challenge, authenticator),
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

    private static string? TryDecode(string base64)
    {
        var buffer = new byte[base64.Length * 3 / 4 + 3];
        return Convert.TryFromBase64String(base64, buffer, out var written) ? Encoding.UTF8.GetString(buffer, 0, written) : null;
    }

    private static (string User, string Password)? ParsePlain(string data)
    {
        var parts = data.Split('\0');
        if (parts.Length != 3)
            return null;
        return (string.IsNullOrEmpty(parts[1]) ? parts[0] : parts[1], parts[2]);
    }

    private static (string? User, string? Token) ParseXOAuth2(string data)
    {
        string? user = null;
        string? token = null;
        foreach (var field in data.Split('\u0001', StringSplitOptions.RemoveEmptyEntries))
        {
            if (field.StartsWith("user=", StringComparison.Ordinal))
                user = field[5..];
            else if (field.StartsWith("auth=", StringComparison.Ordinal))
                token = StripBearer(field[5..]);
        }
        return (user, token);
    }

    private static (string? User, string? Token)? ParseOAuthBearer(string data)
    {
        var separator = data.IndexOf('\u0001');
        if (separator < 0)
            return null;

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
        return (user, token);
    }

    private static async Task<SaslOutcome> PlainAsync(string? initial, Func<string, Task<string?>> challenge, Authenticator authenticator)
    {
        var credentials = ParsePlain(Encoding.UTF8.GetString(await NextAsync(initial, "", challenge)));
        if (credentials is not (var user, var password))
            return new SaslOutcome(false, false, null, "PLAIN", "Formato PLAIN inválido");
        var result = authenticator.ValidatePassword(user, password);
        return new SaslOutcome(result.Success, false, result.User ?? user, "PLAIN", result.Error);
    }

    private static async Task<SaslOutcome> LoginAsync(string? initial, Func<string, Task<string?>> challenge, Authenticator authenticator)
    {
        var user = Encoding.UTF8.GetString(await NextAsync(initial, Convert.ToBase64String("Username:"u8), challenge));
        var password = Encoding.UTF8.GetString(await NextAsync(null, Convert.ToBase64String("Password:"u8), challenge));
        var result = authenticator.ValidatePassword(user, password);
        return new SaslOutcome(result.Success, false, result.User ?? user, "LOGIN", result.Error);
    }

    private static async Task<SaslOutcome> XOAuth2Async(string? initial, Func<string, Task<string?>> challenge, Authenticator authenticator)
    {
        var (user, token) = ParseXOAuth2(Encoding.UTF8.GetString(await NextAsync(initial, "", challenge)));
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
        var parsed = ParseOAuthBearer(Encoding.UTF8.GetString(await NextAsync(initial, "", challenge)));
        if (parsed is not (var user, var token))
            return new SaslOutcome(false, false, null, "OAUTHBEARER", "Formato OAUTHBEARER inválido");
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
