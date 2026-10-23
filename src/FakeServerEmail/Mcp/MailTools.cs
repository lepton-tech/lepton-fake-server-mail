using System.ComponentModel;
using System.Globalization;
using FakeServerEmail.Core;
using FakeServerEmail.Web;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FakeServerEmail.Mcp;

[McpServerToolType]
public sealed partial class MailTools(MailApiClient api)
{
    private const int DefaultLimit = 20;
    private const int MaxListLimit = 200;

    [McpServerTool(Name = "server_info", Title = "Informações do servidor", ReadOnly = true, Idempotent = true)]
    [Description("Mostra como conectar ao servidor de e-mail falso: host, portas reais de SMTP/POP3/IMAP (podem mudar se a padrão estiver ocupada), regras de autenticação, endpoints OAuth 2.0, URL da interface web e hora atual do servidor. Chame primeiro para configurar o aplicativo que está sendo testado.")]
    public async Task<object> ServerInfo(CancellationToken ct)
    {
        var status = await StatusAsync(ct);
        var settings = await api.GetAsync<AppSettings>("/api/settings", ct);
        return new
        {
            host = "localhost",
            serverTime = status.ServerTime,
            services = Services(status),
            authentication = new
            {
                acceptAnyPassword = settings.AcceptAnyPassword,
                allowedUsers = settings.AcceptAnyPassword ? null : settings.Users.Select(u => $"{u.Username}:{u.Password}"),
                smtpRequiresAuth = settings.SmtpRequireAuth,
                validateOAuthTokens = settings.ValidateOAuthTokens,
                mechanisms = settings.AuthMechanisms,
                certificate = "autoassinado para localhost; o cliente precisa aceitar certificado inválido ao usar TLS",
            },
            mailboxes = new
            {
                separateByRecipient = settings.SeparateMailboxesByRecipient,
                maxMessageSizeMb = settings.MaxMessageSizeMb,
                counts = status.Counts,
            },
            oauth = status.OAuth,
            oauthTokenLifetimeSeconds = settings.TokenLifetimeSeconds,
            webInterface = status.Web.Http,
            mcp = status.Mcp,
            dataDirectory = status.DataDirectory,
        };
    }

    private Task<ServerStatus> StatusAsync(CancellationToken ct) => api.GetAsync<ServerStatus>("/api/status", ct);

    private Task<List<StoredMessage>> FetchAsync(string folder, CancellationToken ct, DateTimeOffset? since = null)
    {
        var path = "/api/messages?folder=" + Uri.EscapeDataString(folder);
        if (since is not null)
            path += "&since=" + Uri.EscapeDataString(since.Value.ToString("O", CultureInfo.InvariantCulture));
        return api.GetAsync<List<StoredMessage>>(path, ct);
    }

    private static string MessagePath(string id) => "/api/messages/" + Uri.EscapeDataString(id);

    private static int ClampLimit(int limit, int max = MaxListLimit) => Math.Clamp(limit, 1, max);

    private static List<object> Services(ServerStatus status) => status.Services.Select(s => (object)new
    {
        name = s.Name,
        protocol = s.Protocol,
        port = s.Port,
        security = s.Tls switch
        {
            TlsModes.Implicit => "TLS direto (SSL)",
            TlsModes.None => "texto puro, sem STARTTLS (desligado em offerStartTls)",
            _ => "texto puro, STARTTLS opcional",
        },
        running = s.Running,
        note = s.Error,
    }).ToList();

    private static DateTimeOffset? ParseInstant(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            return parsed;
        throw new McpException($"{name} inválido: use ISO 8601, por exemplo {DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)}");
    }
}
