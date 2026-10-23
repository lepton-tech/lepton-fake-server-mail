using System.ComponentModel;
using FakeServerEmail.Core;
using FakeServerEmail.Web;
using MimeKit;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FakeServerEmail.Mcp;

public sealed partial class MailTools
{
    private const string DefaultSender = "agente@teste.local";
    private const string SmtpProtocol = "SMTP";

    [McpServerTool(Name = "send_email", Title = "Enviar e-mail", Destructive = false)]
    [Description("Envia um e-mail pelo SMTP local, que fica capturado no servidor. Útil para simular a chegada de mensagens que o sistema testado lê via IMAP ou POP3. Nada sai para a internet.")]
    public async Task<object> SendEmail(
        [Description("Destinatários separados por vírgula ou ponto e vírgula.")] string to,
        [Description("Assunto.")] string subject,
        [Description("Corpo em texto puro.")] string? text = null,
        [Description("Corpo em HTML (opcional).")] string? html = null,
        [Description("Remetente (padrão agente@teste.local). Aceita \"Nome <email>\".")] string? from = null,
        [Description("Cópia (opcional).")] string? cc = null,
        [Description("Cópia oculta (opcional).")] string? bcc = null,
        [Description("Responder para (opcional).")] string? replyTo = null,
        [Description("Caminhos completos de arquivos locais para anexar (opcional).")] string[]? attachmentPaths = null,
        CancellationToken ct = default)
    {
        var status = await StatusAsync(ct);
        var port = status.Services.FirstOrDefault(s => s.Protocol == SmtpProtocol && s.Tls != TlsModes.Implicit && s.Running)?.Port
            ?? throw new McpException("Nenhum serviço SMTP está ligado. Ligue o SMTP com update_settings (smtpPort) ou em Configurações → Portas.");

        var message = new MimeMessage();
        try
        {
            message.From.Add(MailboxAddress.Parse(string.IsNullOrWhiteSpace(from) ? DefaultSender : from));
            LocalSmtpSender.AddAddresses(message.To, to);
            LocalSmtpSender.AddAddresses(message.Cc, cc);
            LocalSmtpSender.AddAddresses(message.Bcc, bcc);
            LocalSmtpSender.AddAddresses(message.ReplyTo, replyTo);
        }
        catch (ParseException ex)
        {
            throw new McpException("Endereço de e-mail inválido: " + ex.Message, ex);
        }
        if (message.To.Count + message.Cc.Count + message.Bcc.Count == 0)
            throw new McpException("Informe pelo menos um destinatário.");
        message.Subject = subject;
        message.Body = await BuildBodyAsync(text, html, attachmentPaths, ct);

        var settings = await api.GetAsync<AppSettings>("/api/settings", ct);
        var sentAt = status.ServerTime;
        try
        {
            await LocalSmtpSender.SendAsync(port, settings, message, ct);
        }
        catch (LocalSendException ex)
        {
            throw new McpException(ex.Message, ex);
        }

        var stored = (await FetchAsync("*", ct, sentAt)).LastOrDefault(m => m.Subject == subject);
        return new
        {
            sent = true,
            smtpPort = port,
            messageId = message.MessageId,
            storedId = stored?.Id,
            recipients = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes).Select(m => m.Address),
        };
    }

    private static async Task<MimeEntity> BuildBodyAsync(string? text, string? html, string[]? attachmentPaths, CancellationToken ct)
    {
        var builder = new BodyBuilder { TextBody = text ?? (html is null ? "" : null), HtmlBody = html };
        foreach (var path in attachmentPaths ?? [])
        {
            if (!File.Exists(path))
                throw new McpException($"Arquivo para anexar não encontrado: {path}");
            try
            {
                await builder.Attachments.AddAsync(path, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new McpException($"Não foi possível ler o arquivo para anexar '{path}': {ex.Message}", ex);
            }
        }
        return builder.ToMessageBody();
    }
}
