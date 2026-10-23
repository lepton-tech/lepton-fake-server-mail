using System.ComponentModel;
using System.Globalization;
using System.Text;
using FakeServerEmail.OAuth;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FakeServerEmail.Mcp;

public sealed partial class MailTools
{
    private const string DefaultClientId = "agente-mcp";
    private const string DefaultScope = "https://outlook.office.com/SMTP.Send https://outlook.office.com/IMAP.AccessAsUser.All";
    private const int MaxTokenListLimit = 500;

    [McpServerTool(Name = "create_oauth_token", Title = "Gerar token OAuth")]
    [Description("Emite um access token OAuth 2.0 (JWT) aceito pelo SMTP, POP3 e IMAP via XOAUTH2 ou OAUTHBEARER. Também devolve as strings SASL prontas, em Base64. Para testar expiração, use lifetimeSeconds pequeno.")]
    public async Task<object> CreateOAuthToken(
        [Description("E-mail do usuário dono do token. Vazio = token de aplicativo (client credentials), aceito com qualquer usuário.")] string? user = null,
        [Description("Escopos separados por espaço (padrão: SMTP.Send e IMAP da Microsoft).")] string? scope = null,
        [Description("Validade em segundos (padrão: configuração do servidor).")] int? lifetimeSeconds = null,
        [Description("client_id registrado no token (padrão agente-mcp).")] string? clientId = null,
        CancellationToken ct = default)
    {
        var body = new { user, clientId = clientId ?? DefaultClientId, scope = scope ?? DefaultScope, lifetimeSeconds };
        var token = await api.SendAsync<IssuedToken>(HttpMethod.Post, "/api/oauth/tokens", body, ct);
        string? Sasl(string format) => user is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Format(CultureInfo.InvariantCulture, format, user, token.AccessToken)));
        return new
        {
            accessToken = token.AccessToken,
            tokenType = "Bearer",
            expiresAt = token.ExpiresAt,
            jti = token.Jti,
            user = token.User,
            scope = token.Scope,
            xoauth2 = Sasl("user={0}\u0001auth=Bearer {1}\u0001\u0001"),
            oauthBearer = Sasl("n,a={0},\u0001auth=Bearer {1}\u0001\u0001"),
            usage = user is null
                ? "Token de aplicativo: use com qualquer usuário no XOAUTH2/OAUTHBEARER."
                : $"Use o usuário {user} junto com o token; outro usuário será recusado se a validação estiver ligada.",
        };
    }

    [McpServerTool(Name = "list_oauth_tokens", Title = "Listar tokens OAuth", ReadOnly = true, Idempotent = true)]
    [Description("Lista os tokens OAuth emitidos (pelo fluxo OAuth do aplicativo ou manualmente), com situação, usuário, escopo e quantas vezes foram usados para autenticar.")]
    public async Task<object> ListOAuthTokens(
        [Description("Máximo de itens (padrão 20).")] int limit = DefaultLimit,
        [Description("Incluir o access token completo (padrão: não).")] bool includeAccessToken = false,
        CancellationToken ct = default)
    {
        var tokens = await api.GetAsync<List<IssuedToken>>("/api/oauth/tokens", ct);
        var now = DateTimeOffset.UtcNow;
        return tokens.Take(ClampLimit(limit, MaxTokenListLimit)).Select(t => new
        {
            t.Jti,
            t.GrantType,
            t.ClientId,
            t.User,
            t.Scope,
            t.IssuedAt,
            t.ExpiresAt,
            status = t.Revoked ? "revogado" : t.ExpiresAt <= now ? "expirado" : "válido",
            t.TimesUsed,
            t.LastUsedAt,
            accessToken = includeAccessToken ? t.AccessToken : null,
        });
    }

    [McpServerTool(Name = "revoke_oauth_token", Title = "Revogar token OAuth", Destructive = true, Idempotent = true)]
    [Description("Revoga um token OAuth para testar como o sistema reage a token recusado. Informe jti ou o próprio accessToken.")]
    public async Task<object> RevokeOAuthToken(
        [Description("jti do token (de list_oauth_tokens ou create_oauth_token).")] string? jti = null,
        [Description("O access token completo, como alternativa ao jti.")] string? accessToken = null,
        CancellationToken ct = default)
    {
        if (jti is null && accessToken is not null)
        {
            var tokens = await api.GetAsync<List<IssuedToken>>("/api/oauth/tokens", ct);
            jti = tokens.FirstOrDefault(t => t.AccessToken == accessToken)?.Jti;
        }
        if (jti is null)
            throw new McpException("Token não encontrado. Informe jti ou accessToken.");
        await api.SendAsync(HttpMethod.Delete, "/api/oauth/tokens/" + Uri.EscapeDataString(jti), null, ct, "Token não encontrado (confira o jti em list_oauth_tokens).");
        return new { revoked = true, jti };
    }
}
