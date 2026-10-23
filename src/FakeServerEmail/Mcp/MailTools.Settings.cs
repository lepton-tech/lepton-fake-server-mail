using System.ComponentModel;
using FakeServerEmail.Core;
using FakeServerEmail.Protocols;
using FakeServerEmail.Web;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FakeServerEmail.Mcp;

public sealed partial class MailTools
{
    [McpServerTool(Name = "update_settings", Title = "Alterar configurações", Idempotent = true)]
    [Description("Altera regras do servidor para simular cenários: exigir senha correta, exigir autenticação no SMTP, desligar o STARTTLS, escolher os mecanismos de AUTH anunciados, validar tokens OAuth, validade dos tokens, separar caixas por destinatário, tamanho máximo e as portas de SMTP/POP3/IMAP (0 desliga o serviço). Só muda os campos informados. Retorna as portas reais depois da mudança.")]
    public async Task<object> UpdateSettings(
        [Description("Aceitar qualquer usuário e senha. Padrão: false.")] bool? acceptAnyPassword = null,
        [Description("Usuários aceitos quando acceptAnyPassword = false, no formato \"usuario:senha\". Substitui a lista inteira. Padrão: [\"teste@teste.local:teste\"].")] string[]? users = null,
        [Description("Exigir AUTH antes de MAIL FROM no SMTP.")] bool? smtpRequireAuth = null,
        [Description("Anunciar e aceitar STARTTLS (STLS no POP3) nas portas sem TLS. false simula um servidor sem STARTTLS; as portas de TLS direto continuam iguais.")] bool? offerStartTls = null,
        [Description("Mecanismos SASL anunciados e aceitos no AUTH do SMTP, POP3 e IMAP, entre PLAIN, LOGIN, XOAUTH2 e OAUTHBEARER. Ex.: [\"PLAIN\"]. Lista vazia: o servidor não anuncia AUTH. O comando LOGIN do IMAP e o USER/PASS do POP3 continuam aceitos.")] string[]? authMechanisms = null,
        [Description("Validar assinatura, validade, revogação e dono dos tokens OAuth.")] bool? validateOAuthTokens = null,
        [Description("Validade padrão dos tokens OAuth em segundos.")] int? tokenLifetimeSeconds = null,
        [Description("No POP3/IMAP, login com e-mail vê só as mensagens endereçadas a ele.")] bool? separateMailboxesByRecipient = null,
        [Description("Tamanho máximo de mensagem em MB.")] int? maxMessageSizeMb = null,
        [Description("Porta do SMTP sem TLS (padrão 25). 0 desliga.")] int? smtpPort = null,
        [Description("Porta do SMTP de submissão (padrão 587). 0 desliga.")] int? smtpSubmissionPort = null,
        [Description("Porta do SMTPS, TLS direto. Desligado por padrão; porta sugerida 465. 0 desliga.")] int? smtpsPort = null,
        [Description("Porta do POP3 sem TLS. Desligado por padrão; porta sugerida 110. 0 desliga.")] int? pop3Port = null,
        [Description("Porta do POP3S, TLS direto. Desligado por padrão; porta sugerida 995. 0 desliga.")] int? pop3sPort = null,
        [Description("Porta do IMAP sem TLS. Desligado por padrão; porta sugerida 143. 0 desliga.")] int? imapPort = null,
        [Description("Porta do IMAPS, TLS direto. Desligado por padrão; porta sugerida 993. 0 desliga.")] int? imapsPort = null,
        CancellationToken ct = default)
    {
        var settings = await api.GetAsync<AppSettings>("/api/settings", ct);
        if (acceptAnyPassword is not null) settings.AcceptAnyPassword = acceptAnyPassword.Value;
        if (users is not null)
            settings.Users = users.Select(u => u.Split(':', 2)).Select(p => new UserAccount { Username = p[0].Trim(), Password = p.Length > 1 ? p[1] : "" }).ToList();
        if (smtpRequireAuth is not null) settings.SmtpRequireAuth = smtpRequireAuth.Value;
        if (offerStartTls is not null) settings.OfferStartTls = offerStartTls.Value;
        if (authMechanisms is not null)
        {
            var unknown = authMechanisms.Where(m => !Sasl.IsSupported(m.Trim())).ToList();
            if (unknown.Count > 0)
                throw new McpException($"Mecanismo desconhecido: {string.Join(", ", unknown)}. Use {string.Join(", ", Sasl.Mechanisms)}.");
            settings.AuthMechanisms = authMechanisms.Select(m => m.Trim()).ToList();
        }
        if (validateOAuthTokens is not null) settings.ValidateOAuthTokens = validateOAuthTokens.Value;
        if (tokenLifetimeSeconds is not null) settings.TokenLifetimeSeconds = tokenLifetimeSeconds.Value;
        if (separateMailboxesByRecipient is not null) settings.SeparateMailboxesByRecipient = separateMailboxesByRecipient.Value;
        if (maxMessageSizeMb is not null) settings.MaxMessageSizeMb = maxMessageSizeMb.Value;

        var ports = new Dictionary<string, int?>
        {
            ["smtpPort"] = smtpPort,
            ["smtpSubmissionPort"] = smtpSubmissionPort,
            ["smtpsPort"] = smtpsPort,
            ["pop3Port"] = pop3Port,
            ["pop3sPort"] = pop3sPort,
            ["imapPort"] = imapPort,
            ["imapsPort"] = imapsPort,
        };
        foreach (var port in PortSetting.All)
        {
            if (!ports.TryGetValue(port.Name, out var value) || value is null)
                continue;
            if (!port.IsValid(value.Value))
                throw new McpException(port.InvalidMessage);
            port.Set(settings, value.Value);
        }

        var result = await api.SendAsync<SettingsUpdateResult>(HttpMethod.Put, "/api/settings", settings, ct);
        var saved = await api.GetAsync<AppSettings>("/api/settings", ct);
        return new
        {
            saved.AcceptAnyPassword,
            users = saved.Users.Select(u => $"{u.Username}:{u.Password}"),
            saved.SmtpRequireAuth,
            saved.OfferStartTls,
            saved.AuthMechanisms,
            saved.ValidateOAuthTokens,
            saved.TokenLifetimeSeconds,
            saved.SeparateMailboxesByRecipient,
            saved.MaxMessageSizeMb,
            services = Services(result.Status),
        };
    }
}
