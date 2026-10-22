using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FakeServerEmail.OAuth;

public sealed class IssuedToken
{
    public string Jti { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string GrantType { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? User { get; set; }
    public string Scope { get; set; } = "";
    public string Issuer { get; set; } = "";
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Revoked { get; set; }
    public int TimesUsed { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed class RefreshGrant
{
    public string ClientId { get; set; } = "";
    public string? User { get; set; }
    public string Scope { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record TokenValidation(bool Valid, string? User, string? ClientId, string? Scope, string? Error, DateTimeOffset? ExpiresAt);

public sealed class TokenService
{
    private sealed class PersistedState
    {
        public List<IssuedToken> Tokens { get; set; } = [];
        public Dictionary<string, RefreshGrant> RefreshTokens { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Lock _lock = new();
    private readonly RSA _rsa;
    private readonly string _statePath;
    private readonly List<IssuedToken> _tokens = [];
    private readonly Dictionary<string, RefreshGrant> _refreshTokens = [];

    public TokenService(string dataDirectory)
    {
        _statePath = Path.Combine(dataDirectory, "oauth-tokens.json");
        _rsa = LoadOrCreateKey(Path.Combine(dataDirectory, "oauth-chave-privada.pem"));
        var parameters = _rsa.ExportParameters(false);
        KeyId = Base64Url(SHA256.HashData(parameters.Modulus!))[..16];
        LoadState();
    }

    public event Action? Changed;

    public string KeyId { get; }

    public IReadOnlyList<IssuedToken> List()
    {
        lock (_lock)
            return _tokens.OrderByDescending(t => t.IssuedAt).ToList();
    }

    public JsonObject Jwks()
    {
        var p = _rsa.ExportParameters(false);
        return new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject
            {
                ["kty"] = "RSA",
                ["use"] = "sig",
                ["alg"] = "RS256",
                ["kid"] = KeyId,
                ["n"] = Base64Url(p.Modulus!),
                ["e"] = Base64Url(p.Exponent!),
            }),
        };
    }

    public IssuedToken IssueAccessToken(string issuer, string clientId, string? user, string scope, string grantType, int lifetimeSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var jti = Guid.NewGuid().ToString("N");
        var audience = AudienceFromScope(scope);
        var claims = new JsonObject
        {
            ["iss"] = issuer,
            ["aud"] = audience,
            ["sub"] = user ?? clientId,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddSeconds(lifetimeSeconds).ToUnixTimeSeconds(),
            ["jti"] = jti,
            ["client_id"] = clientId,
            ["azp"] = clientId,
            ["appid"] = clientId,
            ["scope"] = scope,
            ["scp"] = scope,
            ["gty"] = grantType,
        };
        if (user is not null)
        {
            claims["upn"] = user;
            claims["email"] = user;
            claims["preferred_username"] = user;
            claims["unique_name"] = user;
            claims["name"] = DisplayName(user);
        }
        else
        {
            claims["roles"] = new JsonArray("SMTP.SendAsApp", "IMAP.AccessAsApp", "POP.AccessAsApp", "Mail.Send");
        }

        var token = new IssuedToken
        {
            Jti = jti,
            AccessToken = Sign(claims),
            GrantType = grantType,
            ClientId = clientId,
            User = user,
            Scope = scope,
            Issuer = issuer,
            IssuedAt = now,
            ExpiresAt = now.AddSeconds(lifetimeSeconds),
        };

        lock (_lock)
        {
            _tokens.Add(token);
            if (_tokens.Count > 500)
                _tokens.RemoveRange(0, _tokens.Count - 500);
        }
        SaveState();
        return token;
    }

    public string IssueIdToken(string issuer, string clientId, string user, string? nonce, int lifetimeSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = new JsonObject
        {
            ["iss"] = issuer,
            ["aud"] = clientId,
            ["sub"] = user,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddSeconds(lifetimeSeconds).ToUnixTimeSeconds(),
            ["email"] = user,
            ["email_verified"] = true,
            ["preferred_username"] = user,
            ["name"] = DisplayName(user),
            ["oid"] = DeterministicGuid(user),
            ["tid"] = "00000000-0000-0000-0000-00000000fa4e",
        };
        if (!string.IsNullOrEmpty(nonce))
            claims["nonce"] = nonce;
        return Sign(claims);
    }

    public string CreateRefreshToken(string clientId, string? user, string scope)
    {
        var value = "frt_" + Base64Url(RandomNumberGenerator.GetBytes(32));
        lock (_lock)
            _refreshTokens[value] = new RefreshGrant { ClientId = clientId, User = user, Scope = scope, CreatedAt = DateTimeOffset.UtcNow };
        SaveState();
        return value;
    }

    public RefreshGrant? FindRefreshToken(string value)
    {
        lock (_lock)
            return _refreshTokens.GetValueOrDefault(value);
    }

    public TokenValidation Validate(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
            return Fail("O token não é um JWT emitido por este servidor (formato inválido)");

        byte[] signature;
        JsonNode? payload;
        try
        {
            signature = FromBase64Url(parts[2]);
            payload = JsonNode.Parse(FromBase64Url(parts[1]));
        }
        catch
        {
            return Fail("Token malformado");
        }

        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        if (!_rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            return Fail("Assinatura inválida: o token não foi emitido por este servidor");

        var exp = DateTimeOffset.FromUnixTimeSeconds(payload?["exp"]?.GetValue<long>() ?? 0);
        var jti = payload?["jti"]?.GetValue<string>();
        var user = payload?["upn"]?.GetValue<string>() ?? payload?["email"]?.GetValue<string>();
        var clientId = payload?["client_id"]?.GetValue<string>();
        var scope = payload?["scope"]?.GetValue<string>();

        IssuedToken? issued;
        lock (_lock)
            issued = _tokens.FirstOrDefault(t => t.Jti == jti);

        if (issued?.Revoked == true)
            return new TokenValidation(false, user, clientId, scope, "Token revogado", exp);
        if (exp <= DateTimeOffset.UtcNow)
            return new TokenValidation(false, user, clientId, scope, $"Token expirado em {exp.ToLocalTime():dd/MM/yyyy HH:mm:ss}", exp);

        if (issued is not null)
        {
            lock (_lock)
            {
                issued.TimesUsed++;
                issued.LastUsedAt = DateTimeOffset.UtcNow;
            }
            SaveState();
        }
        return new TokenValidation(true, user, clientId, scope, null, exp);

        static TokenValidation Fail(string error) => new(false, null, null, null, error, null);
    }

    public bool Revoke(string jti)
    {
        lock (_lock)
        {
            var token = _tokens.FirstOrDefault(t => t.Jti == jti);
            if (token is null)
                return false;
            token.Revoked = true;
        }
        SaveState();
        return true;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _tokens.Clear();
            _refreshTokens.Clear();
        }
        SaveState();
    }

    public static string AudienceFromScope(string scope)
    {
        foreach (var s in scope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                continue;
            if (uri.Host.Equals("mail.google.com", StringComparison.OrdinalIgnoreCase))
                return "https://mail.google.com/";
            return uri.GetLeftPart(UriPartial.Authority);
        }
        return "fake-server-email";
    }

    public static string DisplayName(string user)
    {
        var local = user.Split('@')[0].Replace('.', ' ').Replace('_', ' ');
        return string.Join(' ', local.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    public static string DeterministicGuid(string value) =>
        new Guid(MD5.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()))).ToString();

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private string Sign(JsonObject claims)
    {
        var header = new JsonObject { ["alg"] = "RS256", ["kid"] = KeyId, ["typ"] = "JWT" };
        var unsigned = Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + Base64Url(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var signature = _rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return unsigned + "." + Base64Url(signature);
    }

    private static RSA LoadOrCreateKey(string path)
    {
        var rsa = RSA.Create();
        try
        {
            if (File.Exists(path))
            {
                rsa.ImportFromPem(File.ReadAllText(path));
                return rsa;
            }
        }
        catch
        {
            rsa.Dispose();
            rsa = RSA.Create();
        }

        rsa.KeySize = 2048;
        try { File.WriteAllText(path, rsa.ExportRSAPrivateKeyPem()); } catch { }
        return rsa;
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath))
                return;
            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_statePath), JsonOptions);
            if (state is null)
                return;
            _tokens.AddRange(state.Tokens ?? []);
            foreach (var (k, v) in state.RefreshTokens ?? [])
                _refreshTokens[k] = v;
        }
        catch
        {
        }
    }

    private void SaveState()
    {
        string json;
        lock (_lock)
            json = JsonSerializer.Serialize(new PersistedState { Tokens = _tokens, RefreshTokens = _refreshTokens }, JsonOptions);
        try
        {
            File.WriteAllText(_statePath, json);
        }
        catch
        {
        }
        Changed?.Invoke();
    }
}
