using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace FakeServerEmail.Tests;

public sealed class ServerFixture : IAsyncLifetime
{
    public FakeMailServer Server { get; private set; } = null!;
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "fse-testes-" + Guid.NewGuid().ToString("N"));
    public int Smtp { get; private set; }
    public int Smtps { get; private set; }
    public int Pop3 { get; private set; }
    public int Pop3s { get; private set; }
    public int Imap { get; private set; }
    public int Imaps { get; private set; }
    public int Http { get; private set; }
    public int Https { get; private set; }

    public string HttpBase => $"http://127.0.0.1:{Http}";
    public string HttpsBase => $"https://localhost:{Https}";

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask InitializeAsync()
    {
        Smtp = FreePort();
        Smtps = FreePort();
        Pop3 = FreePort();
        Pop3s = FreePort();
        Imap = FreePort();
        Imaps = FreePort();
        Http = FreePort();
        Https = FreePort();
        Server = await FakeMailServer.StartAsync(DataDirectory, s =>
        {
            s.SmtpPort = Smtp;
            s.SmtpSubmissionPort = 0;
            s.SmtpsPort = Smtps;
            s.Pop3Port = Pop3;
            s.Pop3sPort = Pop3s;
            s.ImapPort = Imap;
            s.ImapsPort = Imaps;
            s.HttpPort = Http;
            s.HttpsPort = Https;
            s.OpenBrowserOnStart = false;
        });
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        try { Directory.Delete(DataDirectory, true); } catch { }
    }

    public HttpClient CreateHttpClient(bool followRedirects = true) => new(new HttpClientHandler
    {
        AllowAutoRedirect = followRedirects,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    public async Task<string> ClientCredentialsTokenAsync(string scope = "https://outlook.office.com/.default")
    {
        using var http = CreateHttpClient();
        var response = await http.PostAsync($"{HttpBase}/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "app-de-teste",
            ["client_secret"] = "segredo",
            ["scope"] = scope,
        }));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    public async Task<string> ManualTokenAsync(string? user, int lifetimeSeconds = 600)
    {
        using var http = CreateHttpClient();
        var response = await http.PostAsJsonAsync($"{HttpBase}/api/oauth/tokens", new { user, scope = "https://mail.google.com/", lifetimeSeconds });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    public async Task SendAsync(MimeMessage message, Func<SmtpClient, Task>? authenticate = null)
    {
        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", Smtp, SecureSocketOptions.None);
        if (authenticate is not null)
            await authenticate(client);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    public static MimeMessage Simple(string subject, string to = "destino@teste.local", string from = "origem@teste.local", string text = "Corpo de teste")
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = text };
        return message;
    }

    public static MimeMessage Rich(string subject, string to = "destino@teste.local")
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("José da Silva", "jose@teste.local"));
        message.To.Add(MailboxAddress.Parse(to));
        message.Cc.Add(new MailboxAddress("Cópia", "copia@teste.local"));
        message.Subject = subject;
        var builder = new BodyBuilder
        {
            TextBody = "Olá, versão texto com acentuação: ação, maçã.",
            HtmlBody = "<p>Olá, <b>versão HTML</b> <img src=\"cid:logo@teste\"></p>",
        };
        var image = builder.LinkedResources.Add("logo.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="), new ContentType("image", "png"));
        image.ContentId = "logo@teste";
        builder.Attachments.Add("relatório.txt", "conteúdo do anexo\r\n"u8.ToArray(), new ContentType("text", "plain"));
        message.Body = builder.ToMessageBody();
        return message;
    }

    public async Task<T> WaitForAsync<T>(Func<T?> probe, TimeSpan? timeout = null) where T : class
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < until)
        {
            var value = probe();
            if (value is not null)
                return value;
            await Task.Delay(50);
        }
        throw new TimeoutException("Condição não satisfeita a tempo");
    }
}

[CollectionDefinition("servidor")]
public sealed class ServerCollection : ICollectionFixture<ServerFixture>;
