using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FakeServerEmail.Core;
using FakeServerEmail.OAuth;
using FakeServerEmail.Web;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FakeServerEmail.Mcp;

public sealed record SessionInfo(int Id, string Protocol, int Port, string Client, bool Secure, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    string? User, string? AuthMechanism, string? Error, int MessagesReceived, int LineCount, int Failures, List<string> LastLines);

public sealed record SessionDetail(SessionInfo Session, List<LogLine> Lines);

[McpServerToolType]
public sealed partial class MailTools(MailApiClient api)
{
    private const int MaxInlineBytes = 5 * 1024 * 1024;

    [McpServerTool(Name = "server_info", Title = "Informações do servidor", ReadOnly = true, Idempotent = true)]
    [Description("Mostra como conectar ao servidor de e-mail falso: host, portas reais de SMTP/POP3/IMAP (podem mudar se a padrão estiver ocupada), regras de autenticação, endpoints OAuth 2.0, URL da interface web e hora atual do servidor. Chame primeiro para configurar o aplicativo que está sendo testado.")]
    public async Task<object> ServerInfo(CancellationToken ct)
    {
        var status = await api.GetAsync<JsonElement>("/api/status", ct);
        var settings = await api.GetAsync<AppSettings>("/api/settings", ct);
        return new
        {
            host = "localhost",
            serverTime = status.GetProperty("serverTime"),
            services = status.GetProperty("services").EnumerateArray().Select(s => new
            {
                name = s.GetProperty("name").GetString(),
                protocol = s.GetProperty("protocol").GetString(),
                port = s.GetProperty("port").ValueKind == JsonValueKind.Number ? s.GetProperty("port").GetInt32() : (int?)null,
                security = s.GetProperty("tls").GetString() switch
                {
                    "implicit" => "TLS direto (SSL)",
                    "none" => "texto puro, sem STARTTLS (desligado em offerStartTls)",
                    _ => "texto puro, STARTTLS opcional",
                },
                running = s.GetProperty("running").GetBoolean(),
                note = s.GetProperty("error").GetString(),
            }),
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
                counts = status.GetProperty("counts"),
            },
            oauth = status.GetProperty("oauth"),
            oauthTokenLifetimeSeconds = settings.TokenLifetimeSeconds,
            webInterface = status.GetProperty("web").GetProperty("http").GetString(),
            mcp = status.GetProperty("mcp"),
            dataDirectory = status.GetProperty("dataDirectory").GetString(),
        };
    }

    [McpServerTool(Name = "list_messages", Title = "Listar e-mails", ReadOnly = true, Idempotent = true)]
    [Description("Lista os e-mails capturados, do mais novo para o mais antigo, com filtros opcionais. Retorna ids para usar em get_message.")]
    public async Task<object> ListMessages(
        [Description("Pasta: INBOX (padrão), Sent, Trash... ou * para todas.")] string? folder = null,
        [Description("Parte do destinatário (envelope RCPT TO, To ou Cc), sem diferenciar maiúsculas.")] string? to = null,
        [Description("Parte do remetente (envelope MAIL FROM ou From).")] string? from = null,
        [Description("Parte do assunto.")] string? subject = null,
        [Description("Somente não lidas.")] bool unreadOnly = false,
        [Description("Somente recebidas a partir deste instante (ISO 8601, ex.: 2026-10-22T14:58:00-03:00).")] string? receivedAfter = null,
        [Description("Máximo de itens (padrão 20, máximo 200).")] int limit = 20,
        CancellationToken ct = default)
    {
        var after = ParseInstant(receivedAfter, nameof(receivedAfter));
        var all = await FetchAsync(folder ?? MessageStore.Inbox, ct);
        var matches = all.Where(m => Matches(m, to, from, subject, after) && (!unreadOnly || !m.HasFlag(ImapFlags.Seen))).ToList();
        return new
        {
            total = matches.Count,
            returned = Math.Min(matches.Count, Math.Clamp(limit, 1, 200)),
            messages = matches.Take(Math.Clamp(limit, 1, 200)).Select(Summary),
        };
    }

    [McpServerTool(Name = "get_message", Title = "Ler e-mail", ReadOnly = true, Idempotent = true)]
    [Description("Lê um e-mail completo: remetente, destinatários, envelope SMTP, corpo em texto, links encontrados (úteis para links de confirmação e redefinição de senha) e lista de anexos. Use id = \"latest\" para o mais recente.")]
    public async Task<object> GetMessage(
        [Description("Id da mensagem (de list_messages) ou \"latest\".")] string id,
        [Description("Incluir o HTML do corpo (padrão: não).")] bool includeHtml = false,
        [Description("Incluir todos os cabeçalhos (padrão: não).")] bool includeHeaders = false,
        [Description("Marcar como lida (padrão: não).")] bool markAsRead = false,
        [Description("Limite de caracteres para o texto e o HTML (padrão 20000).")] int maxChars = 20000,
        CancellationToken ct = default)
    {
        if (id.Equals("latest", StringComparison.OrdinalIgnoreCase))
            id = (await FetchAsync("*", ct)).FirstOrDefault()?.Id ?? throw new McpException("Nenhuma mensagem capturada ainda.");
        return await DescribeAsync(id, includeHtml, includeHeaders, markAsRead, maxChars, ct);
    }

    [McpServerTool(Name = "wait_for_message", Title = "Esperar e-mail", ReadOnly = true)]
    [Description("Espera chegar um e-mail que combine com os filtros e devolve o conteúdo completo, como get_message. Use logo depois de disparar a ação no sistema testado (cadastro, recuperação de senha etc.). Considera mensagens recebidas a partir de receivedAfter; sem esse parâmetro, os últimos 60 segundos.")]
    public async Task<object> WaitForMessage(
        [Description("Parte do destinatário.")] string? to = null,
        [Description("Parte do remetente.")] string? from = null,
        [Description("Parte do assunto.")] string? subject = null,
        [Description("Considerar mensagens recebidas a partir deste instante (ISO 8601). Padrão: 60 segundos antes da chamada.")] string? receivedAfter = null,
        [Description("Tempo máximo de espera em segundos (padrão 30, máximo 300).")] int timeoutSeconds = 30,
        [Description("Marcar como lida ao encontrar (padrão: não).")] bool markAsRead = false,
        CancellationToken ct = default)
    {
        var after = ParseInstant(receivedAfter, nameof(receivedAfter)) ?? DateTimeOffset.Now.AddSeconds(-60);
        var deadline = DateTimeOffset.Now.AddSeconds(Math.Clamp(timeoutSeconds, 1, 300));
        while (true)
        {
            var match = (await FetchAsync("*", ct)).FirstOrDefault(m => Matches(m, to, from, subject, after));
            if (match is not null)
                return await DescribeAsync(match.Id, false, false, markAsRead, 20000, ct);
            if (DateTimeOffset.Now >= deadline)
            {
                var recent = (await FetchAsync("*", ct)).Take(5).Select(m => $"{m.ReceivedAt:HH:mm:ss} para {string.Join(", ", m.EnvelopeTo)}: {m.Subject}");
                throw new McpException($"Nenhum e-mail combinou com os filtros em {timeoutSeconds}s (recebidos a partir de {after:yyyy-MM-ddTHH:mm:sszzz}). Mais recentes: {string.Join(" | ", recent).NullIfEmpty() ?? "nenhum"}. Se o sistema não enviou nada, verifique list_connections.");
            }
            await Task.Delay(250, ct);
        }
    }

    [McpServerTool(Name = "get_message_source", Title = "Código-fonte do e-mail", ReadOnly = true, Idempotent = true)]
    [Description("Devolve o e-mail bruto (formato .eml/RFC 5322), com todos os cabeçalhos e partes MIME.")]
    public async Task<string> GetMessageSource(
        [Description("Id da mensagem.")] string id,
        [Description("Limite de caracteres (padrão 50000).")] int maxChars = 50000,
        CancellationToken ct = default)
    {
        var raw = await api.GetStringAsync($"/api/messages/{Uri.EscapeDataString(id)}/raw", ct) ?? throw new McpException("Mensagem não encontrada.");
        return Truncate(raw, maxChars);
    }

    [McpServerTool(Name = "get_attachment", Title = "Baixar anexo", ReadOnly = true, Idempotent = true)]
    [Description("Obtém um anexo ou parte inline de um e-mail. Texto vem como texto, imagens vêm como imagem e outros tipos vêm como recurso binário. Se saveTo for informado, grava o arquivo nesse caminho.")]
    public async Task<CallToolResult> GetAttachment(
        [Description("Id da mensagem.")] string id,
        [Description("Índice do anexo (campo index em get_message).")] int? index = null,
        [Description("Nome do arquivo, como alternativa ao índice.")] string? fileName = null,
        [Description("Caminho completo para gravar o arquivo no disco (opcional).")] string? saveTo = null,
        CancellationToken ct = default)
    {
        var detail = await api.GetAsync<MessageDetail>($"/api/messages/{Uri.EscapeDataString(id)}", ct);
        var attachment = index is not null
            ? detail.Attachments.FirstOrDefault(a => a.Index == index)
            : detail.Attachments.FirstOrDefault(a => string.Equals(a.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        if (attachment is null)
            throw new McpException("Anexo não encontrado. Anexos disponíveis: " + (string.Join(", ", detail.Attachments.Select(a => $"[{a.Index}] {a.FileName}")).NullIfEmpty() ?? "nenhum"));

        var part = await api.GetBytesAsync($"/api/messages/{Uri.EscapeDataString(id)}/parts/{attachment.Index}", ct) ?? throw new McpException("Anexo não encontrado.");
        var info = $"{attachment.FileName} ({attachment.ContentType}, {part.Data.Length} bytes)";
        var content = new List<ContentBlock>();

        if (!string.IsNullOrWhiteSpace(saveTo))
        {
            var path = Path.GetFullPath(saveTo);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, part.Data, ct);
            content.Add(new TextContentBlock { Text = $"Anexo {info} gravado em {path}" });
            return new CallToolResult { Content = content };
        }

        if (attachment.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || attachment.ContentType is "application/json" or "application/xml")
        {
            content.Add(new TextContentBlock { Text = $"Anexo {info}:\n\n" + Truncate(Encoding.UTF8.GetString(part.Data), 100000) });
        }
        else if (part.Data.Length > MaxInlineBytes)
        {
            content.Add(new TextContentBlock { Text = $"Anexo {info} é grande demais para devolver inline; use saveTo para gravar em disco." });
        }
        else if (attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            content.Add(new TextContentBlock { Text = $"Anexo {info}:" });
            content.Add(ImageContentBlock.FromBytes(part.Data, attachment.ContentType));
        }
        else
        {
            content.Add(new TextContentBlock { Text = $"Anexo {info}:" });
            content.Add(new EmbeddedResourceBlock { Resource = BlobResourceContents.FromBytes(part.Data, $"fakeserveremail://messages/{id}/parts/{attachment.Index}/{Uri.EscapeDataString(attachment.FileName)}", attachment.ContentType) });
        }
        return new CallToolResult { Content = content };
    }

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
        var status = await api.GetAsync<JsonElement>("/api/status", ct);
        var smtp = status.GetProperty("services").EnumerateArray()
            .FirstOrDefault(s => s.GetProperty("protocol").GetString() == "SMTP" && s.GetProperty("tls").GetString() != "implicit" && s.GetProperty("running").GetBoolean());
        if (smtp.ValueKind != JsonValueKind.Object)
            throw new McpException("Nenhum serviço SMTP está rodando.");
        var port = smtp.GetProperty("port").GetInt32();

        var message = new MimeMessage();
        try
        {
            message.From.Add(MailboxAddress.Parse(string.IsNullOrWhiteSpace(from) ? "agente@teste.local" : from));
            AddAddresses(message.To, to);
            AddAddresses(message.Cc, cc);
            AddAddresses(message.Bcc, bcc);
            AddAddresses(message.ReplyTo, replyTo);
        }
        catch (ParseException ex)
        {
            throw new McpException("Endereço de e-mail inválido: " + ex.Message);
        }
        if (message.To.Count + message.Cc.Count + message.Bcc.Count == 0)
            throw new McpException("Informe pelo menos um destinatário.");
        message.Subject = subject;

        var builder = new BodyBuilder { TextBody = text ?? (html is null ? "" : null), HtmlBody = html };
        foreach (var path in attachmentPaths ?? [])
        {
            if (!File.Exists(path))
                throw new McpException($"Arquivo para anexar não encontrado: {path}");
            await builder.Attachments.AddAsync(path, ct);
        }
        message.Body = builder.ToMessageBody();

        using (var client = new SmtpClient())
        {
            await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
        }

        var stored = (await FetchAsync("*", ct)).Take(10).FirstOrDefault(m => m.Subject == subject);
        return new
        {
            sent = true,
            smtpPort = port,
            messageId = message.MessageId,
            storedId = stored?.Id,
            recipients = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes).Select(m => m.Address),
        };
    }

    [McpServerTool(Name = "delete_messages", Title = "Apagar e-mails", Destructive = true)]
    [Description("Apaga e-mails capturados: os ids informados, ou todos de uma pasta com all = true. Bom para começar um teste com a caixa limpa.")]
    public async Task<object> DeleteMessages(
        [Description("Ids das mensagens a apagar.")] string[]? ids = null,
        [Description("Apagar todas as mensagens da pasta indicada.")] bool all = false,
        [Description("Pasta usada com all = true: INBOX (padrão) ou * para todas.")] string? folder = null,
        CancellationToken ct = default)
    {
        if (all)
        {
            var target = folder ?? MessageStore.Inbox;
            var count = (await FetchAsync(target, ct)).Count;
            await api.SendAsync(HttpMethod.Delete, "/api/messages?folder=" + Uri.EscapeDataString(target), null, ct);
            return new { deleted = count, folder = target };
        }
        if (ids is null || ids.Length == 0)
            throw new McpException("Informe ids ou use all = true.");
        foreach (var id in ids)
            await api.SendAsync(HttpMethod.Delete, $"/api/messages/{Uri.EscapeDataString(id)}", null, ct);
        return new { deleted = ids.Length };
    }

    [McpServerTool(Name = "mark_messages", Title = "Marcar como lida/não lida", Idempotent = true)]
    [Description("Marca e-mails como lidos ou não lidos (flag \\Seen, a mesma vista pelo IMAP).")]
    public async Task<object> MarkMessages(
        [Description("Ids das mensagens.")] string[] ids,
        [Description("true = lida, false = não lida.")] bool seen,
        CancellationToken ct = default)
    {
        foreach (var id in ids)
            await api.SendAsync(HttpMethod.Post, $"/api/messages/{Uri.EscapeDataString(id)}/seen", new { seen }, ct);
        return new { updated = ids.Length, seen };
    }

    [McpServerTool(Name = "create_oauth_token", Title = "Gerar token OAuth")]
    [Description("Emite um access token OAuth 2.0 (JWT) aceito pelo SMTP, POP3 e IMAP via XOAUTH2 ou OAUTHBEARER. Também devolve as strings SASL prontas, em Base64. Para testar expiração, use lifetimeSeconds pequeno.")]
    public async Task<object> CreateOAuthToken(
        [Description("E-mail do usuário dono do token. Vazio = token de aplicativo (client credentials), aceito com qualquer usuário.")] string? user = null,
        [Description("Escopos separados por espaço (padrão: SMTP.Send e IMAP da Microsoft).")] string? scope = null,
        [Description("Validade em segundos (padrão: configuração do servidor).")] int? lifetimeSeconds = null,
        [Description("client_id registrado no token (padrão agente-mcp).")] string? clientId = null,
        CancellationToken ct = default)
    {
        var body = new
        {
            user,
            clientId = clientId ?? "agente-mcp",
            scope = scope ?? "https://outlook.office.com/SMTP.Send https://outlook.office.com/IMAP.AccessAsUser.All",
            lifetimeSeconds,
        };
        var token = (await api.SendAsync(HttpMethod.Post, "/api/oauth/tokens", body, ct))!.Value.Deserialize<IssuedToken>(MailApiClient.Json)!;
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
        [Description("Máximo de itens (padrão 20).")] int limit = 20,
        [Description("Incluir o access token completo (padrão: não).")] bool includeAccessToken = false,
        CancellationToken ct = default)
    {
        var tokens = await api.GetAsync<List<IssuedToken>>("/api/oauth/tokens", ct);
        var now = DateTimeOffset.UtcNow;
        return tokens.Take(Math.Clamp(limit, 1, 500)).Select(t => new
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
        await api.SendAsync(HttpMethod.Delete, $"/api/oauth/tokens/{Uri.EscapeDataString(jti)}", null, ct);
        return new { revoked = true, jti };
    }

    [McpServerTool(Name = "list_connections", Title = "Listar conexões", ReadOnly = true, Idempotent = true)]
    [Description("Lista as conexões SMTP, POP3 e IMAP recentes, com cliente, usuário, mecanismo de autenticação, TLS, mensagens recebidas, falhas e as últimas linhas do diálogo. Use para descobrir por que o sistema testado não conseguiu enviar ou autenticar.")]
    public async Task<object> ListConnections(
        [Description("Filtrar por protocolo: SMTP, POP3 ou IMAP.")] string? protocol = null,
        [Description("Somente conexões com falha (erro de autenticação, comando recusado etc.).")] bool onlyFailures = false,
        [Description("Máximo de itens (padrão 20).")] int limit = 20,
        CancellationToken ct = default)
    {
        var sessions = await api.GetAsync<List<SessionInfo>>("/api/sessions", ct);
        return sessions
            .Where(s => protocol is null || s.Protocol.Equals(protocol, StringComparison.OrdinalIgnoreCase))
            .Where(s => !onlyFailures || s.Failures > 0 || s.Error is not null)
            .Take(Math.Clamp(limit, 1, 200));
    }

    [McpServerTool(Name = "get_connection_log", Title = "Diálogo da conexão", ReadOnly = true, Idempotent = true)]
    [Description("Mostra o diálogo completo de uma conexão (C: cliente, S: servidor, *: evento), para depurar SMTP, POP3, IMAP e autenticação.")]
    public async Task<string> GetConnectionLog(
        [Description("Id da conexão (de list_connections).")] int id,
        [Description("Máximo de linhas (padrão 400).")] int maxLines = 400,
        CancellationToken ct = default)
    {
        var detail = await api.GetAsync<SessionDetail>($"/api/sessions/{id}", ct);
        var s = detail.Session;
        var sb = new StringBuilder();
        sb.AppendLine($"{s.Protocol} porta {s.Port} · cliente {s.Client} · {(s.Secure ? "TLS" : "sem TLS")} · usuário {s.User ?? "(nenhum)"} · início {s.StartedAt:yyyy-MM-dd HH:mm:ss}");
        var lines = detail.Lines;
        if (lines.Count > maxLines)
            sb.AppendLine($"(mostrando as últimas {maxLines} de {lines.Count} linhas)");
        foreach (var line in lines.TakeLast(Math.Max(1, maxLines)))
            sb.AppendLine($"{line.Time:HH:mm:ss.fff} {line.Direction}: {line.Text}");
        return sb.ToString();
    }

    [McpServerTool(Name = "update_settings", Title = "Alterar configurações", Idempotent = true)]
    [Description("Altera regras do servidor para simular cenários: exigir senha correta, exigir autenticação no SMTP, desligar o STARTTLS, escolher os mecanismos de AUTH anunciados, validar tokens OAuth, validade dos tokens, separar caixas por destinatário e tamanho máximo. Só muda os campos informados.")]
    public async Task<object> UpdateSettings(
        [Description("Aceitar qualquer usuário e senha.")] bool? acceptAnyPassword = null,
        [Description("Usuários aceitos quando acceptAnyPassword = false, no formato \"usuario:senha\".")] string[]? users = null,
        [Description("Exigir AUTH antes de MAIL FROM no SMTP.")] bool? smtpRequireAuth = null,
        [Description("Anunciar e aceitar STARTTLS (STLS no POP3) nas portas sem TLS. false simula um servidor sem STARTTLS; as portas de TLS direto continuam iguais.")] bool? offerStartTls = null,
        [Description("Mecanismos SASL anunciados e aceitos no AUTH do SMTP, POP3 e IMAP, entre PLAIN, LOGIN, XOAUTH2 e OAUTHBEARER. Ex.: [\"PLAIN\"]. Lista vazia: o servidor não anuncia AUTH. O comando LOGIN do IMAP e o USER/PASS do POP3 continuam aceitos.")] string[]? authMechanisms = null,
        [Description("Validar assinatura, validade, revogação e dono dos tokens OAuth.")] bool? validateOAuthTokens = null,
        [Description("Validade padrão dos tokens OAuth em segundos.")] int? tokenLifetimeSeconds = null,
        [Description("No POP3/IMAP, login com e-mail vê só as mensagens endereçadas a ele.")] bool? separateMailboxesByRecipient = null,
        [Description("Tamanho máximo de mensagem em MB.")] int? maxMessageSizeMb = null,
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
            var unknown = authMechanisms.Where(m => !Protocols.Sasl.IsSupported(m.Trim())).ToList();
            if (unknown.Count > 0)
                throw new McpException($"Mecanismo desconhecido: {string.Join(", ", unknown)}. Use {string.Join(", ", Protocols.Sasl.Mechanisms)}.");
            settings.AuthMechanisms = authMechanisms.Select(m => m.Trim()).ToList();
        }
        if (validateOAuthTokens is not null) settings.ValidateOAuthTokens = validateOAuthTokens.Value;
        if (tokenLifetimeSeconds is not null) settings.TokenLifetimeSeconds = tokenLifetimeSeconds.Value;
        if (separateMailboxesByRecipient is not null) settings.SeparateMailboxesByRecipient = separateMailboxesByRecipient.Value;
        if (maxMessageSizeMb is not null) settings.MaxMessageSizeMb = maxMessageSizeMb.Value;
        await api.SendAsync(HttpMethod.Put, "/api/settings", settings, ct);
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
        };
    }

    private async Task<List<StoredMessage>> FetchAsync(string folder, CancellationToken ct) =>
        await api.GetAsync<List<StoredMessage>>("/api/messages?folder=" + Uri.EscapeDataString(folder), ct);

    private async Task<object> DescribeAsync(string id, bool includeHtml, bool includeHeaders, bool markAsRead, int maxChars, CancellationToken ct)
    {
        var detail = await api.GetAsync<MessageDetail>($"/api/messages/{Uri.EscapeDataString(id)}", ct);
        var html = detail.HasHtml ? await api.GetStringAsync($"/api/messages/{Uri.EscapeDataString(id)}/html", ct) : null;
        if (markAsRead)
            await api.SendAsync(HttpMethod.Post, $"/api/messages/{Uri.EscapeDataString(id)}/seen", new { seen = true }, ct);

        var s = detail.Summary;
        var text = detail.Text ?? (html is null ? "" : HtmlToText(html));
        return new
        {
            id = s.Id,
            subject = detail.Subject,
            from = detail.From,
            to = detail.To,
            cc = detail.Cc.NullIfEmpty(),
            bcc = detail.Bcc.NullIfEmpty(),
            replyTo = detail.ReplyTo.NullIfEmpty(),
            date = detail.Date,
            receivedAt = s.ReceivedAt,
            envelope = new { mailFrom = s.EnvelopeFrom, rcptTo = s.EnvelopeTo },
            authenticatedUser = s.AuthenticatedUser,
            folder = s.Folder,
            seen = markAsRead || s.HasFlag(ImapFlags.Seen),
            flags = s.Flags,
            messageId = detail.MessageId,
            sizeBytes = s.Size,
            text = Truncate(text, maxChars),
            textIsFromHtml = detail.Text is null && html is not null,
            html = includeHtml && html is not null ? Truncate(html, maxChars) : null,
            links = Links(html, detail.Text),
            attachments = detail.Attachments.Select(a => new { a.Index, a.FileName, a.ContentType, sizeBytes = a.Size, a.ContentId, a.Inline }),
            headers = includeHeaders ? detail.Headers : null,
            warning = detail.ParseError,
        };
    }

    private static object Summary(StoredMessage m) => new
    {
        id = m.Id,
        subject = m.Subject,
        from = m.From,
        to = m.To,
        cc = m.Cc.NullIfEmpty(),
        envelope = new { mailFrom = m.EnvelopeFrom, rcptTo = m.EnvelopeTo },
        receivedAt = m.ReceivedAt,
        folder = m.Folder,
        seen = m.HasFlag(ImapFlags.Seen),
        hasAttachments = m.HasAttachments,
        sizeBytes = m.Size,
        authenticatedUser = m.AuthenticatedUser,
    };

    private static bool Matches(StoredMessage m, string? to, string? from, string? subject, DateTimeOffset? after)
    {
        static bool Has(string? haystack, string needle) => haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
        if (after is not null && m.ReceivedAt < after)
            return false;
        if (!string.IsNullOrWhiteSpace(to) && !(m.EnvelopeTo.Any(r => Has(r, to)) || Has(m.To, to) || Has(m.Cc, to)))
            return false;
        if (!string.IsNullOrWhiteSpace(from) && !(Has(m.EnvelopeFrom, from) || Has(m.From, from)))
            return false;
        return string.IsNullOrWhiteSpace(subject) || Has(m.Subject, subject);
    }

    private static DateTimeOffset? ParseInstant(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            return parsed;
        throw new McpException($"{name} inválido: use ISO 8601, por exemplo {DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}");
    }

    private static void AddAddresses(InternetAddressList list, string? value)
    {
        foreach (var item in (value ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            list.Add(MailboxAddress.Parse(item));
    }

    private static List<string> Links(string? html, string? text)
    {
        var links = new List<string>();
        if (html is not null)
            links.AddRange(HrefRegex().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value.Trim())));
        if (text is not null)
            links.AddRange(UrlRegex().Matches(text).Select(m => m.Value.TrimEnd('.', ',', ';', ':')));
        return links.Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("/api/", StringComparison.Ordinal)).Distinct().Take(100).ToList();
    }

    private static string HtmlToText(string html)
    {
        var text = ScriptStyleRegex().Replace(html, "");
        text = BreakRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        return BlankLinesRegex().Replace(text, "\n\n").Trim();
    }

    private static string Truncate(string value, int max)
    {
        max = Math.Max(100, max);
        return value.Length <= max ? value : value[..max] + $"\n… (truncado: {value.Length - max} caracteres omitidos)";
    }

    [GeneratedRegex(@"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex(@"https?://[^\s<>""'\)\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<\s*(br|/p|/div|/tr|/h[1-6]|/li)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\n\s*\n\s*\n+")]
    private static partial Regex BlankLinesRegex();
}

internal static class StringExtensions
{
    public static string? NullIfEmpty(this string? value) => string.IsNullOrEmpty(value) ? null : value;
}
