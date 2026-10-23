using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeServerEmail.Core;

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

public sealed class TokenService : IDisposable
{
    public const string FakeTenantId = "00000000-0000-0000-0000-00000000fa4e";
    private const int MaxKeptTokens = 500;
    private const int MaxRefreshTokens = 500;
    private const int RsaKeySize = 2048;
    private const int KeyIdLength = 16;
    private const int RefreshTokenBytes = 32;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);
    private static readonly string[] AppRoles = ["SMTP.SendAsApp", "IMAP.AccessAsApp", "POP.AccessAsApp", "Mail.Send"];

    private sealed class PersistedState
    {
        public List<IssuedToken> Tokens { get; set; } = [];
        public Dictionary<string, RefreshGrant> RefreshTokens { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Lock _lock = new();
    private readonly Lock _saveLock = new();
    private readonly RSA _rsa;
    private readonly string _statePath;
    private readonly List<IssuedToken> _tokens = [];
    private readonly Dictionary<string, RefreshGrant> _refreshTokens = [];
    private readonly Timer _saveTimer;
    private bool _dirty;

    public TokenService(string dataDirectory)
    {
        _statePath = Path.Combine(dataDirectory, DataFiles.OAuthTokens);
        _rsa = LoadOrCreateKey(Path.Combine(dataDirectory, DataFiles.OAuthKey));
        KeyId = Base64Url.EncodeToString(SHA256.HashData(_rsa.ExportParameters(false).Modulus!))[..KeyIdLength];
        _saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
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
                ["n"] = Base64Url.EncodeToString(p.Modulus!),
                ["e"] = Base64Url.EncodeToString(p.Exponent!),
            }),
        };
    }

    public IssuedToken IssueAccessToken(string issuer, string clientId, string? user, string scope, string grantType, int lifetimeSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var jti = Guid.NewGuid().ToString("N");
        var claims = new JsonObject
        {
            ["iss"] = issuer,
            ["aud"] = AudienceFromScope(scope),
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
            claims["roles"] = new JsonArray([.. AppRoles.Select(r => (JsonNode)r)]);
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
            ForgetExpiredTokens(now);
            MarkDirty();
        }
        Changed?.Invoke();
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
            ["tid"] = FakeTenantId,
        };
        if (!string.IsNullOrEmpty(nonce))
            claims["nonce"] = nonce;
        return Sign(claims);
    }

    public string CreateRefreshToken(string clientId, string? user, string scope)
    {
        var value = "frt_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RefreshTokenBytes));
        lock (_lock)
        {
            _refreshTokens[value] = new RefreshGrant { ClientId = clientId, User = user, Scope = scope, CreatedAt = DateTimeOffset.UtcNow };
            if (_refreshTokens.Count > MaxRefreshTokens)
                foreach (var oldest in _refreshTokens.OrderBy(r => r.Value.CreatedAt).Take(_refreshTokens.Count - MaxRefreshTokens).Select(r => r.Key).ToList())
                    _refreshTokens.Remove(oldest);
            MarkDirty();
        }
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
            signature = Base64Url.DecodeFromChars(parts[2]);
            payload = JsonNode.Parse(Base64Url.DecodeFromChars(parts[1]));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
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

        if (jti is null)
            return new TokenValidation(false, user, clientId, scope, "Este é um id_token, que só identifica o usuário. Para entrar no e-mail, use o access_token.", exp);

        IssuedToken? issued;
        lock (_lock)
            issued = _tokens.FirstOrDefault(t => t.Jti == jti);

        if (issued is null)
            return new TokenValidation(false, user, clientId, scope, "Token desconhecido: ele não está mais na lista de tokens deste servidor (a lista foi limpa?)", exp);
        if (issued.Revoked)
            return new TokenValidation(false, user, clientId, scope, "Token revogado", exp);
        if (exp <= DateTimeOffset.UtcNow)
            return new TokenValidation(false, user, clientId, scope, $"Token expirado em {exp.ToLocalTime():dd/MM/yyyy HH:mm:ss}", exp);

        lock (_lock)
        {
            issued.TimesUsed++;
            issued.LastUsedAt = DateTimeOffset.UtcNow;
            MarkDirty();
        }
        Changed?.Invoke();
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
            MarkDirty();
        }
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _tokens.Clear();
            _refreshTokens.Clear();
            MarkDirty();
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        using (var callbacksDone = new ManualResetEvent(false))
        {
            if (_saveTimer.Dispose(callbacksDone))
                callbacksDone.WaitOne();
        }
        Save();
        _rsa.Dispose();
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

    public static string DeterministicGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString();
    }

    private void ForgetExpiredTokens(DateTimeOffset now)
    {
        if (_tokens.Count <= MaxKeptTokens)
            return;
        var expired = _tokens.Where(t => t.ExpiresAt <= now).OrderBy(t => t.ExpiresAt).Take(_tokens.Count - MaxKeptTokens).ToHashSet();
        _tokens.RemoveAll(expired.Contains);
    }

    private string Sign(JsonObject claims)
    {
        var header = new JsonObject { ["alg"] = "RS256", ["kid"] = KeyId, ["typ"] = "JWT" };
        var unsigned = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var signature = _rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return unsigned + "." + Base64Url.EncodeToString(signature);
    }

    private static RSA LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            var existing = RSA.Create();
            try
            {
                existing.ImportFromPem(File.ReadAllText(path));
                return existing;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or CryptographicException)
            {
                existing.Dispose();
            }
        }

        var rsa = RSA.Create(RsaKeySize);
        try
        {
            AtomicFile.WriteAllText(path, rsa.ExportRSAPrivateKeyPem());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return rsa;
    }

    private void LoadState()
    {
        if (!File.Exists(_statePath))
            return;
        try
        {
            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_statePath), JsonOptions);
            if (state is null)
                return;
            _tokens.AddRange(state.Tokens ?? []);
            foreach (var (k, v) in state.RefreshTokens ?? [])
                _refreshTokens[k] = v;
        }
        catch (JsonException)
        {
            try
            {
                File.Copy(_statePath, _statePath + DataFiles.InvalidSuffix, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void MarkDirty()
    {
        _dirty = true;
        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    private void Save()
    {
        lock (_saveLock)
        {
            string json;
            lock (_lock)
            {
                if (!_dirty)
                    return;
                _dirty = false;
                json = JsonSerializer.Serialize(new PersistedState { Tokens = _tokens, RefreshTokens = _refreshTokens }, JsonOptions);
            }
            try
            {
                AtomicFile.WriteAllText(_statePath, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (_lock)
                    MarkDirty();
            }
        }
    }
}
