using System.Buffers;
using System.Text;
using FakeServerEmail.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using MimeKit;

namespace FakeServerEmail.Web;

public sealed record SendTestRequest(string? From, string? To, string? Subject, string? Text, string? Html, bool AttachSample);

public sealed record SeenRequest(bool Seen);

public static partial class ApiEndpoints
{
    private const string EmailHtmlPolicy = "default-src 'none'; img-src * data: blob:; style-src 'unsafe-inline' *; font-src * data:; media-src * data:";
    private const string ActiveAttachmentPolicy = "sandbox; default-src 'none'; img-src * data:; style-src 'unsafe-inline' *";
    private const string DefaultTestSender = "remetente@teste.local";
    private const string DefaultTestRecipient = "destinatario@teste.local";
    private const int MaxFileNameLength = 80;
    private static readonly string[] ActiveContentTypes = ["text/html", "application/xhtml+xml", "image/svg+xml", "text/xml", "application/xml"];
    private static readonly SearchValues<char> InvalidFileNameChars = SearchValues.Create(Path.GetInvalidFileNameChars());

    private static void MapMessages(RouteGroupBuilder api)
    {
        api.MapGet("/folders", (MessageStore store) => TypedResults.Ok(store.FolderSummaries()));

        var messages = api.MapGroup("/messages");
        messages.MapGet("", (string? folder, DateTimeOffset? since, MessageStore store) =>
            TypedResults.Ok(store.List(FolderOrAll(folder)).Where(m => since is null || m.ReceivedAt >= since).OrderByDescending(m => m.ReceivedAt)));

        messages.MapGet("/{id}", Results<Ok<MessageDetail>, NotFound> (string id, MessageStore store) =>
            store.Get(id) is { } message && store.GetRaw(id) is { } raw ? TypedResults.Ok(MessageView.Build(message, raw)) : TypedResults.NotFound());

        messages.MapGet("/{id}/raw", IResult (string id, bool? download, MessageStore store) =>
        {
            if (store.Get(id) is not { } message || store.GetRaw(id) is not { } raw)
                return TypedResults.NotFound();
            return download == true
                ? TypedResults.File(raw, "message/rfc822", SafeFileName(message.Subject, "mensagem") + ".eml")
                : TypedResults.File(raw, "text/plain; charset=utf-8");
        });

        messages.MapGet("/{id}/html", IResult (string id, HttpResponse response, MessageStore store) =>
        {
            if (store.GetRaw(id) is not { } raw || MessageView.Html(id, raw) is not { } html)
                return TypedResults.NotFound();
            response.Headers.ContentSecurityPolicy = EmailHtmlPolicy;
            response.Headers.XContentTypeOptions = "nosniff";
            return TypedResults.Content(html, "text/html; charset=utf-8");
        });

        messages.MapGet("/{id}/parts/{index:int}", IResult (string id, int index, bool? download, HttpResponse response, MessageStore store) =>
        {
            if (store.GetRaw(id) is not { } raw || MessageView.Part(raw, index) is not { } part)
                return TypedResults.NotFound();
            ProtectAttachment(response, part.ContentType);
            return download == true
                ? TypedResults.File(part.Content, part.ContentType, part.FileName)
                : TypedResults.File(part.Content, part.ContentType);
        });

        messages.MapGet("/{id}/cid/{cid}", IResult (string id, string cid, HttpResponse response, MessageStore store) =>
        {
            if (store.GetRaw(id) is not { } raw || MessageView.ByContentId(raw, Uri.UnescapeDataString(cid)) is not { } part)
                return TypedResults.NotFound();
            ProtectAttachment(response, part.ContentType);
            return TypedResults.File(part.Content, part.ContentType);
        });

        messages.MapPost("/{id}/seen", (string id, SeenRequest request, MessageStore store) =>
        {
            store.SetSeen([id], request.Seen);
            return TypedResults.NoContent();
        });

        messages.MapPost("/seen-all", (string? folder, MessageStore store) =>
        {
            store.SetSeen(store.List(FolderOrAll(folder)).Select(m => m.Id), true);
            return TypedResults.NoContent();
        });

        messages.MapDelete("/{id}", (string id, MessageStore store) =>
        {
            store.Delete([id]);
            return TypedResults.NoContent();
        });

        messages.MapDelete("", (string? folder, MessageStore store) =>
        {
            store.DeleteAll(FolderOrAll(folder));
            return TypedResults.NoContent();
        });

        api.MapPost("/send-test", async Task<IResult> (SendTestRequest request, FakeMailServer server, SettingsStore settings, CancellationToken ct) =>
        {
            if (server.Services.FirstOrDefault(s => s.Protocol == SmtpProtocol && !s.ImplicitTls && s.Running)?.ActualPort is not int port)
                return TypedResults.Problem("Nenhum serviço SMTP está ligado. Ligue o SMTP em Configurações → Portas.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var message = new MimeMessage();
            try
            {
                message.From.Add(MailboxAddress.Parse(string.IsNullOrWhiteSpace(request.From) ? DefaultTestSender : request.From.Trim()));
                LocalSmtpSender.AddAddresses(message.To, string.IsNullOrWhiteSpace(request.To) ? DefaultTestRecipient : request.To);
            }
            catch (ParseException ex)
            {
                return TypedResults.Problem("Endereço inválido: " + ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            if (message.To.Count == 0)
                return TypedResults.Problem("Informe pelo menos um destinatário.", statusCode: StatusCodes.Status400BadRequest);
            message.Subject = string.IsNullOrWhiteSpace(request.Subject) ? "Mensagem de teste" : request.Subject;

            var builder = new BodyBuilder
            {
                TextBody = request.Text ?? "Olá! Esta é uma mensagem de teste enviada pelo Fake Server Email.",
                HtmlBody = request.Html,
            };
            if (request.AttachSample)
                builder.Attachments.Add("exemplo.txt", Encoding.UTF8.GetBytes("Arquivo de exemplo gerado pelo Fake Server Email.\r\n"), new ContentType("text", "plain"));
            message.Body = builder.ToMessageBody();

            try
            {
                await LocalSmtpSender.SendAsync(port, settings.Current, message, ct);
                return TypedResults.Ok(new { ok = true, port });
            }
            catch (LocalSendException ex)
            {
                return TypedResults.Problem("Falha ao enviar: " + ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }

    private static void ProtectAttachment(HttpResponse response, string contentType)
    {
        response.Headers.XContentTypeOptions = "nosniff";
        var mediaType = contentType.Split(';')[0].Trim();
        if (ActiveContentTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase) || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
            response.Headers.ContentSecurityPolicy = ActiveAttachmentPolicy;
    }

    private static string SafeFileName(string value, string fallback)
    {
        var clean = string.Concat(value.Where(c => !InvalidFileNameChars.Contains(c))).Trim();
        if (clean.Length > MaxFileNameLength)
            clean = clean[..MaxFileNameLength];
        return clean.Length == 0 ? fallback : clean;
    }
}
