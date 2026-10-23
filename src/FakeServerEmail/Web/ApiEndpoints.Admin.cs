using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using FakeServerEmail.Core;
using FakeServerEmail.OAuth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace FakeServerEmail.Web;

public sealed record GenerateTokenRequest(string? User, string? ClientId, string? Scope, int? LifetimeSeconds);

public static partial class ApiEndpoints
{
    private const string PanelClientId = "painel-fake-server";

    private static void MapOAuthTokens(RouteGroupBuilder api)
    {
        var tokens = api.MapGroup("/oauth/tokens");
        tokens.MapGet("", (TokenService tokenService) => TypedResults.Ok(tokenService.List()));

        tokens.MapPost("", (GenerateTokenRequest request, HttpRequest http, TokenService tokenService, SettingsStore settings, SessionLog log) =>
        {
            var lifetime = request.LifetimeSeconds is > 0 ? request.LifetimeSeconds.Value : settings.Current.TokenLifetimeSeconds;
            var user = string.IsNullOrWhiteSpace(request.User) ? null : request.User.Trim();
            var clientId = string.IsNullOrWhiteSpace(request.ClientId) ? PanelClientId : request.ClientId.Trim();
            var token = tokenService.IssueAccessToken($"{http.Scheme}://{http.Host}", clientId, user, request.Scope?.Trim() ?? "", "manual", lifetime);
            log.Report($"OAuth  token gerado pelo painel para {user ?? "aplicativo"}");
            return TypedResults.Ok(token);
        });

        tokens.MapDelete("/{jti}", Results<NoContent, NotFound> (string jti, TokenService tokenService) =>
            tokenService.Revoke(jti) ? TypedResults.NoContent() : TypedResults.NotFound());

        tokens.MapDelete("", (TokenService tokenService) =>
        {
            tokenService.Clear();
            return TypedResults.NoContent();
        });
    }

    private static void MapCertificate(RouteGroupBuilder api)
    {
        var certificate = api.MapGroup("/certificate");
        certificate.MapGet("", (CertificateProvider certificates) =>
            TypedResults.File(certificates.PublicCertificate, "application/pkix-cert", "fake-server-email-localhost.cer"));

        certificate.MapPost("/trust", async Task<IResult> (string? scope, CertificateProvider certificates, CancellationToken ct) =>
        {
            try
            {
                if (scope == TrustScopes.Computer)
                    await certificates.TrustForComputerAsync(ct);
                else
                    certificates.TrustForUser();
                return TypedResults.Ok(CertificateStatus.From(certificates));
            }
            catch (Exception ex) when (ex is InvalidOperationException or CryptographicException)
            {
                return TypedResults.Problem("Não foi possível instalar o certificado: " + ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        certificate.MapPost("/untrust", async Task<IResult> (string? scope, CertificateProvider certificates, CancellationToken ct) =>
        {
            try
            {
                if (scope == TrustScopes.Computer)
                    await certificates.UntrustForComputerAsync(ct);
                else
                    certificates.UntrustForUser();
                return TypedResults.Ok(CertificateStatus.From(certificates));
            }
            catch (Exception ex) when (ex is InvalidOperationException or CryptographicException)
            {
                return TypedResults.Problem("Não foi possível remover o certificado: " + ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }

    private static void MapData(RouteGroupBuilder api)
    {
        api.MapPost("/open-data-folder", (ServerContext context) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{context.DataDirectory}\"") { UseShellExecute = true })?.Dispose();
            }
            catch (Win32Exception)
            {
            }
            return TypedResults.NoContent();
        });

        api.MapPost("/data/clear", (MessageStore store, TokenService tokens, SessionLog log) =>
        {
            var messages = store.Counts().Messages;
            var tokenCount = tokens.List().Count;
            store.DeleteAll();
            foreach (var folder in store.Folders.Where(f => !MessageStore.DefaultFolders.Contains(f, StringComparer.OrdinalIgnoreCase)))
                store.DeleteFolder(folder);
            tokens.Clear();
            log.Clear();
            log.Report($"Dados limpos pelo painel: {messages} mensagens e {tokenCount} tokens apagados");
            return TypedResults.Ok(new { messagesDeleted = messages, tokensDeleted = tokenCount });
        });

        api.MapPost("/data/remove", async (HttpResponse response, FakeMailServer server, CancellationToken ct) =>
        {
            var warning = await RemoveTrustAsync(server.Context.Certificates, ct);
            server.RemoveDataOnExit = true;
            server.Context.Log.Report("Removendo os dados deste computador e encerrando...");
            ShutdownAfterResponse(response, server);
            return TypedResults.Ok(new { dataDirectory = server.Context.DataDirectory, warning });
        });

        api.MapPost("/shutdown", (HttpResponse response, FakeMailServer server) =>
        {
            ShutdownAfterResponse(response, server);
            return TypedResults.NoContent();
        });
    }

    private static async Task<string?> RemoveTrustAsync(CertificateProvider certificates, CancellationToken ct)
    {
        try
        {
            if (certificates.Trust == CertificateTrust.Computer)
                await certificates.UntrustForComputerAsync(ct);
            if (certificates.Trust == CertificateTrust.User)
                certificates.UntrustForUser();
        }
        catch (Exception ex) when (ex is InvalidOperationException or CryptographicException)
        {
            return "O certificado continua na lista de confiáveis do Windows: " + ex.Message;
        }
        return certificates.Trust == CertificateTrust.None ? null : "O certificado continua na lista de confiáveis do Windows (a remoção foi cancelada).";
    }

    private static void ShutdownAfterResponse(HttpResponse response, FakeMailServer server) =>
        response.OnCompleted(() =>
        {
            server.RequestShutdown();
            return Task.CompletedTask;
        });

    private static void MapEvents(RouteGroupBuilder api)
    {
        api.MapGet("/events", async (HttpContext http, FakeMailServer server, EventHub events) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, server.ShutdownToken);
            var ct = cts.Token;
            try
            {
                await http.Response.WriteAsync(": conectado\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
                await foreach (var name in events.ListenAsync(ct))
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
}
