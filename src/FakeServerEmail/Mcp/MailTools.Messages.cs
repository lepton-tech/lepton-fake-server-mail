using System.ComponentModel;
using System.Globalization;
using System.Text;
using FakeServerEmail.Core;
using FakeServerEmail.Web;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FakeServerEmail.Mcp;

public sealed partial class MailTools
{
    private const string LatestId = "latest";
    private const int DefaultTextChars = 20000;
    private const int DefaultSourceChars = 50000;
    private const int MaxAttachmentTextChars = 100000;
    private const int MaxInlineBytes = 5 * 1024 * 1024;
    private const int DefaultWaitSeconds = 30;
    private const int MaxWaitSeconds = 300;
    private const int RecentOnTimeout = 5;
    private static readonly TimeSpan DefaultWaitLookBack = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromMilliseconds(250);

    [McpServerTool(Name = "list_messages", Title = "Listar e-mails", ReadOnly = true, Idempotent = true)]
    [Description("Lista os e-mails capturados, do mais novo para o mais antigo, com filtros opcionais. Retorna ids para usar em get_message.")]
    public async Task<object> ListMessages(
        [Description("Pasta: INBOX (padrão), Sent, Trash... ou * para todas.")] string? folder = null,
        [Description("Parte do destinatário (envelope RCPT TO, To ou Cc), sem diferenciar maiúsculas.")] string? to = null,
        [Description("Parte do remetente (envelope MAIL FROM ou From).")] string? from = null,
        [Description("Parte do assunto.")] string? subject = null,
        [Description("Somente não lidas.")] bool unreadOnly = false,
        [Description("Somente recebidas a partir deste instante (ISO 8601, ex.: 2026-10-22T14:58:00-03:00).")] string? receivedAfter = null,
        [Description("Máximo de itens (padrão 20, máximo 200).")] int limit = DefaultLimit,
        CancellationToken ct = default)
    {
        var after = ParseInstant(receivedAfter, nameof(receivedAfter));
        var all = await FetchAsync(folder ?? MessageStore.Inbox, ct, after);
        var matches = all.Where(m => Matches(m, to, from, subject, after) && (!unreadOnly || !m.HasFlag(ImapFlags.Seen))).ToList();
        var take = ClampLimit(limit);
        return new
        {
            total = matches.Count,
            returned = Math.Min(matches.Count, take),
            messages = matches.Take(take).Select(Summary),
        };
    }

    [McpServerTool(Name = "get_message", Title = "Ler e-mail", ReadOnly = true, Idempotent = true)]
    [Description("Lê um e-mail completo: remetente, destinatários, envelope SMTP, corpo em texto, links encontrados (úteis para links de confirmação e redefinição de senha) e lista de anexos. Use id = \"latest\" para o mais recente.")]
    public async Task<object> GetMessage(
        [Description("Id da mensagem (de list_messages) ou \"latest\".")] string id,
        [Description("Incluir o HTML do corpo (padrão: não).")] bool includeHtml = false,
        [Description("Incluir todos os cabeçalhos (padrão: não).")] bool includeHeaders = false,
        [Description("Marcar como lida (padrão: não).")] bool markAsRead = false,
        [Description("Limite de caracteres para o texto e o HTML (padrão 20000).")] int maxChars = DefaultTextChars,
        CancellationToken ct = default)
    {
        if (id.Equals(LatestId, StringComparison.OrdinalIgnoreCase))
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
        [Description("Tempo máximo de espera em segundos (padrão 30, máximo 300).")] int timeoutSeconds = DefaultWaitSeconds,
        [Description("Marcar como lida ao encontrar (padrão: não).")] bool markAsRead = false,
        CancellationToken ct = default)
    {
        var after = ParseInstant(receivedAfter, nameof(receivedAfter)) ?? DateTimeOffset.Now - DefaultWaitLookBack;
        var deadline = DateTimeOffset.Now.AddSeconds(Math.Clamp(timeoutSeconds, 1, MaxWaitSeconds));
        while (true)
        {
            var match = (await FetchAsync("*", ct, after)).FirstOrDefault(m => Matches(m, to, from, subject, after));
            if (match is not null)
                return await DescribeAsync(match.Id, false, false, markAsRead, DefaultTextChars, ct);
            if (DateTimeOffset.Now >= deadline)
            {
                var recent = (await FetchAsync("*", ct)).Take(RecentOnTimeout)
                    .Select(m => $"{m.ReceivedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} para {string.Join(", ", m.EnvelopeTo)}: {m.Subject}");
                var since = after.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
                throw new McpException($"Nenhum e-mail combinou com os filtros em {timeoutSeconds}s (recebidos a partir de {since}). Mais recentes: {string.Join(" | ", recent).NullIfEmpty() ?? "nenhum"}. Se o sistema não enviou nada, verifique list_connections.");
            }
            await Task.Delay(WaitPollInterval, ct);
        }
    }

    [McpServerTool(Name = "get_message_source", Title = "Código-fonte do e-mail", ReadOnly = true, Idempotent = true)]
    [Description("Devolve o e-mail bruto (formato .eml/RFC 5322), com todos os cabeçalhos e partes MIME.")]
    public async Task<string> GetMessageSource(
        [Description("Id da mensagem.")] string id,
        [Description("Limite de caracteres (padrão 50000).")] int maxChars = DefaultSourceChars,
        CancellationToken ct = default)
    {
        var raw = await api.GetStringAsync(MessagePath(id) + "/raw", ct) ?? throw new McpException("Mensagem não encontrada.");
        return MailText.Truncate(raw, maxChars);
    }

    [McpServerTool(Name = "get_attachment", Title = "Baixar anexo", Idempotent = true)]
    [Description("Obtém um anexo ou parte inline de um e-mail. Texto vem como texto, imagens vêm como imagem e outros tipos vêm como recurso binário. Se saveTo for informado, grava o arquivo nesse caminho.")]
    public async Task<CallToolResult> GetAttachment(
        [Description("Id da mensagem.")] string id,
        [Description("Índice do anexo (campo index em get_message).")] int? index = null,
        [Description("Nome do arquivo, como alternativa ao índice.")] string? fileName = null,
        [Description("Caminho completo para gravar o arquivo no disco (opcional).")] string? saveTo = null,
        CancellationToken ct = default)
    {
        var detail = await api.GetAsync<MessageDetail>(MessagePath(id), ct);
        var attachment = index is not null
            ? detail.Attachments.FirstOrDefault(a => a.Index == index)
            : detail.Attachments.FirstOrDefault(a => string.Equals(a.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        if (attachment is null)
            throw new McpException("Anexo não encontrado. Anexos disponíveis: " + (string.Join(", ", detail.Attachments.Select(a => $"[{a.Index}] {a.FileName}")).NullIfEmpty() ?? "nenhum"));

        var part = await api.GetBytesAsync($"{MessagePath(id)}/parts/{attachment.Index}", ct) ?? throw new McpException("Anexo não encontrado.");
        var info = $"{attachment.FileName} ({attachment.ContentType}, {part.Data.Length} bytes)";

        if (!string.IsNullOrWhiteSpace(saveTo))
        {
            var path = await SaveAsync(saveTo, part.Data, ct);
            return new CallToolResult { Content = [new TextContentBlock { Text = $"Anexo {info} gravado em {path}" }] };
        }

        var content = new List<ContentBlock>();
        if (attachment.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || attachment.ContentType is "application/json" or "application/xml")
        {
            content.Add(new TextContentBlock { Text = $"Anexo {info}:\n\n" + MailText.Truncate(Encoding.UTF8.GetString(part.Data), MaxAttachmentTextChars) });
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
        var (found, notFound) = await SplitExistingAsync(ids, ct);
        foreach (var id in found)
            await api.SendAsync(HttpMethod.Delete, MessagePath(id), null, ct);
        return new { deleted = found.Count, notFound };
    }

    [McpServerTool(Name = "mark_messages", Title = "Marcar como lida/não lida", Idempotent = true)]
    [Description("Marca e-mails como lidos ou não lidos (flag \\Seen, a mesma vista pelo IMAP).")]
    public async Task<object> MarkMessages(
        [Description("Ids das mensagens.")] string[] ids,
        [Description("true = lida, false = não lida.")] bool seen,
        CancellationToken ct = default)
    {
        var (found, notFound) = await SplitExistingAsync(ids, ct);
        foreach (var id in found)
            await api.SendAsync(HttpMethod.Post, MessagePath(id) + "/seen", new { seen }, ct);
        return new { updated = found.Count, seen, notFound };
    }

    private async Task<(List<string> Found, List<string>? NotFound)> SplitExistingAsync(string[] ids, CancellationToken ct)
    {
        var existing = (await FetchAsync("*", ct)).Select(m => m.Id).ToHashSet();
        var found = ids.Where(existing.Contains).Distinct().ToList();
        var notFound = ids.Except(found).ToList();
        return (found, notFound.Count > 0 ? notFound : null);
    }

    private static async Task<string> SaveAsync(string saveTo, byte[] data, CancellationToken ct)
    {
        try
        {
            var path = Path.GetFullPath(saveTo);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(path, data, ct);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new McpException($"Não foi possível gravar o anexo em '{saveTo}': {ex.Message}", ex);
        }
    }

    private async Task<object> DescribeAsync(string id, bool includeHtml, bool includeHeaders, bool markAsRead, int maxChars, CancellationToken ct)
    {
        var detail = await api.GetAsync<MessageDetail>(MessagePath(id), ct);
        var html = detail.HasHtml ? await api.GetStringAsync(MessagePath(id) + "/html", ct) : null;
        if (markAsRead)
            await api.SendAsync(HttpMethod.Post, MessagePath(id) + "/seen", new { seen = true }, ct);

        var s = detail.Summary;
        var text = detail.Text ?? (html is null ? "" : MailText.HtmlToText(html));
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
            text = MailText.Truncate(text, maxChars),
            textIsFromHtml = detail.Text is null && html is not null,
            html = includeHtml && html is not null ? MailText.Truncate(html, maxChars) : null,
            links = MailText.Links(html, detail.Text),
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
}
