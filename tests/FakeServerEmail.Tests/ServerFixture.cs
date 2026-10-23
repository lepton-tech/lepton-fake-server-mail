using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FakeServerEmail.Core;
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

    public static void OnlySmtpAndWeb(AppSettings s, int smtpPort, int httpPort)
    {
        s.SmtpPort = smtpPort;
        s.SmtpSubmissionPort = 0;
        s.SmtpsPort = 0;
        s.Pop3Port = 0;
        s.Pop3sPort = 0;
        s.ImapPort = 0;
        s.ImapsPort = 0;
        s.HttpPort = httpPort;
        s.HttpsPort = 0;
        s.OpenBrowserOnStart = false;
    }

    public static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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
            s.AcceptAnyPassword = true;
            s.SmtpRequireAuth = false;
        });
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        DeleteDirectory(DataDirectory);
    }

    public bool IsServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        certificate is not null && certificate.GetCertHashString() == Server.Context.Certificates.Certificate.Thumbprint;

    public HttpClient CreateHttpClient(bool followRedirects = true) => new(new HttpClientHandler
    {
        AllowAutoRedirect = followRedirects,
        ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) => IsServerCertificate(this, certificate, chain, errors),
    });

    public SettingsOverride OverrideSettings(Action<AppSettings>? change = null) => new(Server, change);

    public async Task<string> ClientCredentialsTokenAsync(CancellationToken ct, string scope = "https://outlook.office.com/.default")
    {
        using var http = CreateHttpClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "app-de-teste",
            ["client_secret"] = "segredo",
            ["scope"] = scope,
        });
        using var response = await http.PostAsync($"{HttpBase}/oauth2/token", form, ct);
        response.EnsureSuccessStatusCode();
        return JsonElement.Parse(await response.Content.ReadAsStringAsync(ct)).GetProperty("access_token").GetString()!;
    }

    public async Task<string> ManualTokenAsync(string? user, CancellationToken ct, int lifetimeSeconds = 600)
    {
        using var http = CreateHttpClient();
        using var response = await http.PostAsJsonAsync($"{HttpBase}/api/oauth/tokens", new { user, scope = "https://mail.google.com/", lifetimeSeconds }, ct);
        response.EnsureSuccessStatusCode();
        return JsonElement.Parse(await response.Content.ReadAsStringAsync(ct)).GetProperty("accessToken").GetString()!;
    }

    public async Task SendAsync(MimeMessage message, CancellationToken ct, Func<SmtpClient, Task>? authenticate = null)
    {
        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", Smtp, SecureSocketOptions.None, ct);
        if (authenticate is not null)
            await authenticate(client);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string what, CancellationToken ct, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition() && DateTime.UtcNow < until)
            await Task.Delay(50, ct);
        Assert.True(condition(), what);
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
}

public sealed class SettingsOverride : IDisposable
{
    private readonly FakeMailServer _server;
    private readonly AppSettings _original;

    internal SettingsOverride(FakeMailServer server, Action<AppSettings>? change)
    {
        _server = server;
        _original = server.Context.Settings.Current.Clone();
        if (change is not null)
            Apply(change);
    }

    public void Apply(Action<AppSettings> change)
    {
        var next = _server.Context.Settings.Current.Clone();
        change(next);
        _server.Context.Settings.Update(next);
    }

    public void Dispose()
    {
        var current = _server.Context.Settings.Current;
        var portsChanged = PortSetting.All.Any(p => !p.IsWeb && p.Get(current) != p.Get(_original)) || current.AllowNetworkAccess != _original.AllowNetworkAccess;
        _server.Context.Settings.Update(_original);
        if (portsChanged)
            _server.RestartProtocolServices();
    }
}

[CollectionDefinition("servidor")]
public sealed class SharedServerDefinition : ICollectionFixture<ServerFixture>;
