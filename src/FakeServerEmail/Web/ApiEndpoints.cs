using System.Diagnostics;
using System.Text;
using FakeServerEmail.Core;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MimeKit;

namespace FakeServerEmail.Web;

public sealed record SendTestRequest(string? From, string? To, string? Subject, string? Text, string? Html, bool AttachSample);

public sealed record GenerateTokenRequest(string? User, string? ClientId, string? Scope, int? LifetimeSeconds);

public sealed record SeenRequest(bool Seen);

public static class ApiEndpoints
{
    public static void Map(WebApplication app, FakeMailServer server)
    {
        var context = server.Context;
        var store = context.Store;

        app.MapGet("/api/status", (HttpContext http) => Results.Json(server.Status()));

        app.MapGet("/api/folders", () => Results.Json(store.Folders.Select(f => new
        {
            name = f,
            total = store.List(f).Count,
            unread = store.List(f).Count(m => !m.HasFlag(ImapFlags.Seen)),
        })));

        app.MapGet("/api/messages", (string? folder) =>
        {
            var list = string.IsNullOrEmpty(folder) || folder == "*" ? store.List() : store.List(folder);
            return Results.Json(list.OrderByDescending(m => m.ReceivedAt));
        });

        app.MapGet("/api/messages/{id}", (string id) =>
        {
            var message = store.Get(id);
            var raw = store.GetRaw(id);
            return message is null || raw is null ? Results.NotFound() : Results.Json(MessageView.Build(message, raw));
        });

        app.MapGet("/api/messages/{id}/raw", (string id, bool? download) =>
        {
            var message = store.Get(id);
            var raw = store.GetRaw(id);
            if (message is null || raw is null)
                return Results.NotFound();
            return download == true
                ? Results.File(raw, "message/rfc822", SafeFileName(message.Subject, "mensagem") + ".eml")
                : Results.File(raw, "text/plain; charset=utf-8");
        });

        app.MapGet("/api/messages/{id}/html", (string id, HttpContext http) =>
        {
            var raw = store.GetRaw(id);
            var html = raw is null ? null : MessageView.Html(id, raw);
            if (html is null)
                return Results.NotFound();
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src * data: blob:; style-src 'unsafe-inline' *; font-src * data:; media-src * data:";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapGet("/api/messages/{id}/parts/{index:int}", (string id, int index, bool? download) =>
        {
            var raw = store.GetRaw(id);
            var part = raw is null ? null : MessageView.Part(raw, index);
            if (part is null)
                return Results.NotFound();
            return download == true
                ? Results.File(part.Value.Content, part.Value.ContentType, part.Value.FileName)
                : Results.File(part.Value.Content, part.Value.ContentType);
        });

        app.MapGet("/api/messages/{id}/cid/{cid}", (string id, string cid) =>
        {
            var raw = store.GetRaw(id);
            var part = raw is null ? null : MessageView.ByContentId(raw, Uri.UnescapeDataString(cid));
            return part is null ? Results.NotFound() : Results.File(part.Value.Content, part.Value.ContentType);
        });

        app.MapPost("/api/messages/{id}/seen", (string id, SeenRequest request) =>
        {
            store.SetSeen([id], request.Seen);
            return Results.NoContent();
        });

        app.MapPost("/api/messages/seen-all", (string? folder) =>
        {
            var list = string.IsNullOrEmpty(folder) || folder == "*" ? store.List() : store.List(folder);
            store.SetSeen(list.Select(m => m.Id), true);
            return Results.NoContent();
        });

        app.MapDelete("/api/messages/{id}", (string id) =>
        {
            store.Delete([id]);
            return Results.NoContent();
        });

        app.MapDelete("/api/messages", (string? folder) =>
        {
            store.DeleteAll(string.IsNullOrEmpty(folder) || folder == "*" ? null : folder);
            return Results.NoContent();
        });

        app.MapPost("/api/send-test", async (SendTestRequest request) =>
        {
            var smtp = server.Services.FirstOrDefault(s => s.Protocol == "SMTP" && !s.ImplicitTls && s.Running);
            if (smtp is null)
                return Results.Problem("Nenhum serviço SMTP está rodando.", statusCode: 503);

            var message = new MimeMessage();
            try
            {
                message.From.Add(MailboxAddress.Parse(string.IsNullOrWhiteSpace(request.From) ? "remetente@teste.local" : request.From.Trim()));
                foreach (var to in (request.To ?? "destinatario@teste.local").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    message.To.Add(MailboxAddress.Parse(to));
            }
            catch (ParseException ex)
            {
                return Results.Problem("Endereço inválido: " + ex.Message, statusCode: 400);
            }
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
                using var client = new SmtpClient();
                await client.ConnectAsync("127.0.0.1", smtp.ActualPort!.Value, SecureSocketOptions.None);
                await LocalSmtpLogin.AuthenticateIfRequiredAsync(client, context.Settings.Current, message.From.Mailboxes.First().Address, CancellationToken.None);
                await client.SendAsync(message);
                await client.DisconnectAsync(true);
                return Results.Json(new { ok = true, port = smtp.ActualPort });
            }
            catch (Exception ex)
            {
                return Results.Problem("Falha ao enviar: " + ex.Message, statusCode: 500);
            }
        });

        app.MapGet("/api/sessions", () => Results.Json(context.Log.List().Select(SessionSummary)));

        app.MapGet("/api/sessions/{id:int}", (int id) =>
        {
            var session = context.Log.Get(id);
            return session is null ? Results.NotFound() : Results.Json(new { session = SessionSummary(session), lines = session.Lines });
        });

        app.MapDelete("/api/sessions", () =>
        {
            context.Log.Clear();
            return Results.NoContent();
        });

        app.MapGet("/api/settings", () => Results.Json(context.Settings.Current));

        app.MapPut("/api/settings", async (AppSettings settings) =>
        {
            var before = context.Settings.Current;
            context.Settings.Update(settings);
            var webChanged = before.HttpPort != settings.HttpPort || before.HttpsPort != settings.HttpsPort || before.AllowNetworkAccess != settings.AllowNetworkAccess;
            var servicesChanged = before.SmtpPort != settings.SmtpPort || before.SmtpSubmissionPort != settings.SmtpSubmissionPort
                || before.SmtpsPort != settings.SmtpsPort || before.Pop3Port != settings.Pop3Port || before.Pop3sPort != settings.Pop3sPort
                || before.ImapPort != settings.ImapPort || before.ImapsPort != settings.ImapsPort || before.AllowNetworkAccess != settings.AllowNetworkAccess;
            if (servicesChanged)
                server.RestartProtocolServices();
            return Results.Json(new { saved = true, servicesRestarted = servicesChanged, restartRequired = webChanged, status = server.Status() });
        });

        app.MapGet("/api/oauth/tokens", () => Results.Json(context.Tokens.List()));

        app.MapPost("/api/oauth/tokens", (GenerateTokenRequest request, HttpContext http) =>
        {
            var lifetime = request.LifetimeSeconds is > 0 ? request.LifetimeSeconds.Value : context.Settings.Current.TokenLifetimeSeconds;
            var user = string.IsNullOrWhiteSpace(request.User) ? null : request.User.Trim();
            var token = context.Tokens.IssueAccessToken($"{http.Request.Scheme}://{http.Request.Host}", string.IsNullOrWhiteSpace(request.ClientId) ? "painel-fake-server" : request.ClientId.Trim(),
                user, request.Scope?.Trim() ?? "", "manual", lifetime);
            context.Log.Report($"OAuth  token gerado pelo painel para {user ?? "aplicativo"}");
            return Results.Json(token);
        });

        app.MapDelete("/api/oauth/tokens/{jti}", (string jti) => context.Tokens.Revoke(jti) ? Results.NoContent() : Results.NotFound());

        app.MapDelete("/api/oauth/tokens", () =>
        {
            context.Tokens.Clear();
            return Results.NoContent();
        });

        app.MapGet("/api/certificate", () => Results.File(context.Certificates.PublicCertificate, "application/pkix-cert", "fake-server-email-localhost.cer"));

        app.MapPost("/api/certificate/trust", () =>
        {
            try
            {
                context.Certificates.Trust();
                return Results.Json(new { trusted = context.Certificates.IsTrusted() });
            }
            catch (Exception ex)
            {
                return Results.Problem("Não foi possível instalar o certificado: " + ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/certificate/untrust", () =>
        {
            try
            {
                context.Certificates.Untrust();
                return Results.Json(new { trusted = context.Certificates.IsTrusted() });
            }
            catch (Exception ex)
            {
                return Results.Problem("Não foi possível remover o certificado: " + ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/open-data-folder", () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{context.DataDirectory}\"") { UseShellExecute = true });
            }
            catch
            {
            }
            return Results.NoContent();
        });

        app.MapPost("/api/data/clear", () =>
        {
            var messages = store.List().Count;
            var tokens = context.Tokens.List().Count;
            store.DeleteAll();
            foreach (var folder in store.Folders.Where(f => !MessageStore.DefaultFolders.Contains(f, StringComparer.OrdinalIgnoreCase)))
                store.DeleteFolder(folder);
            context.Tokens.Clear();
            context.Log.Clear();
            context.Log.Report($"Dados limpos pelo painel: {messages} mensagens e {tokens} tokens apagados");
            return Results.Json(new { messagesDeleted = messages, tokensDeleted = tokens });
        });

        app.MapPost("/api/data/remove", () =>
        {
            string? warning = null;
            if (context.Certificates.IsTrusted())
            {
                try
                {
                    context.Certificates.Untrust();
                }
                catch (Exception ex)
                {
                    warning = "O certificado continua na lista de confiáveis do Windows: " + ex.Message;
                }
                if (warning is null && context.Certificates.IsTrusted())
                    warning = "O certificado continua na lista de confiáveis do Windows (a remoção foi cancelada).";
            }
            server.RemoveDataOnExit = true;
            context.Log.Report("Removendo os dados deste computador e encerrando...");
            _ = Task.Delay(300).ContinueWith(_ => server.RequestShutdown());
            return Results.Json(new { dataDirectory = context.DataDirectory, warning });
        });

        app.MapPost("/api/shutdown", () =>
        {
            _ = Task.Delay(300).ContinueWith(_ => server.RequestShutdown());
            return Results.NoContent();
        });

        app.MapGet("/api/events", async (HttpContext http) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, server.ShutdownToken);
            var ct = cts.Token;
            var reader = server.Events.Subscribe(ct);
            try
            {
                await http.Response.WriteAsync(": conectado\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
                await foreach (var name in reader.ReadAllAsync(ct))
                {
                    await http.Response.WriteAsync($"event: {name}\ndata: {{}}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private static object SessionSummary(ProtocolSession s)
    {
        var lines = s.Lines;
        return new
        {
            s.Id,
            s.Protocol,
            s.Port,
            s.Client,
            s.Secure,
            s.StartedAt,
            s.EndedAt,
            s.User,
            s.AuthMechanism,
            s.Error,
            s.MessagesReceived,
            s.LineCount,
            Failures = lines.Count(IsFailure),
            LastLines = lines.TakeLast(3).Select(l => $"{l.Direction}: {l.Text}").ToList(),
        };
    }

    private static bool IsFailure(LogLine line)
    {
        if (line.Direction == '*')
            return line.Text.StartsWith("Falha", StringComparison.Ordinal) || line.Text.StartsWith("Erro", StringComparison.Ordinal);
        if (line.Direction != 'S' || line.Text.Length < 3)
            return false;
        if (line.Text[0] is '4' or '5' && char.IsAsciiDigit(line.Text[1]) && char.IsAsciiDigit(line.Text[2]))
            return true;
        if (line.Text.StartsWith("-ERR", StringComparison.Ordinal))
            return true;
        var parts = line.Text.Split(' ', 3);
        return parts.Length >= 2 && parts[0] != "*" && parts[1] is "NO" or "BAD";
    }

    private static string SafeFileName(string value, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(value.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        if (clean.Length > 80)
            clean = clean[..80];
        return clean.Length == 0 ? fallback : clean;
    }
}
