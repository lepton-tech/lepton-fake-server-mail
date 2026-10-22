using System.Net;
using System.Net.Sockets;
using System.Reflection;
using FakeServerEmail.Core;
using FakeServerEmail.Mcp;
using FakeServerEmail.OAuth;
using FakeServerEmail.Protocols;
using FakeServerEmail.Protocols.Imap;
using FakeServerEmail.Protocols.Pop3;
using FakeServerEmail.Protocols.Smtp;
using FakeServerEmail.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace FakeServerEmail;

public sealed class FakeMailServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _servicesLock = new();
    private readonly Timer _ping;
    private WebApplication? _web;
    private List<TcpService> _services = [];

    private FakeMailServer(ServerContext context)
    {
        Context = context;
        _ping = new Timer(_ => Events.Publish("ping"), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    public static string Version => typeof(FakeMailServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    public ServerContext Context { get; }

    public EventHub Events { get; } = new();

    public IReadOnlyList<TcpService> Services
    {
        get { lock (_servicesLock) return [.. _services]; }
    }

    public int? HttpPort { get; private set; }

    public int? HttpsPort { get; private set; }

    public string? WebError { get; private set; }

    public string WebUrl => $"http://localhost:{HttpPort}";

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public CancellationToken ShutdownToken => _shutdown.Token;

    public static async Task<FakeMailServer> StartAsync(string dataDirectory, Action<AppSettings>? configure = null)
    {
        Directory.CreateDirectory(dataDirectory);
        var settings = new SettingsStore(dataDirectory, configure);
        var tokens = new TokenService(dataDirectory);
        var context = new ServerContext
        {
            DataDirectory = dataDirectory,
            Settings = settings,
            Store = new MessageStore(dataDirectory),
            Tokens = tokens,
            Authenticator = new Authenticator(settings, tokens),
            Log = new SessionLog(),
            Certificates = new CertificateProvider(dataDirectory),
            HostName = SafeHostName(),
        };

        var server = new FakeMailServer(context);
        context.Store.Changed += _ => server.Events.Publish("messages");
        context.Log.Changed += _ => server.Events.Publish("sessions");
        context.Tokens.Changed += () => server.Events.Publish("tokens");
        settings.Changed += _ => server.Events.Publish("settings");

        server.RestartProtocolServices();
        await server.StartWebAsync();
        return server;
    }

    public void RestartProtocolServices()
    {
        var s = Context.Settings.Current;
        var cert = Context.Certificates.Certificate;
        var log = Context.Log;
        var services = new List<TcpService>
        {
            new("SMTP", s.SmtpPort, false, new SmtpHandler(Context, true), cert, log),
            new("SMTP submissão", s.SmtpSubmissionPort, false, new SmtpHandler(Context, true), cert, log),
            new("SMTPS", s.SmtpsPort, true, new SmtpHandler(Context, false), cert, log),
            new("POP3", s.Pop3Port, false, new Pop3Handler(Context, true), cert, log),
            new("POP3S", s.Pop3sPort, true, new Pop3Handler(Context, false), cert, log),
            new("IMAP", s.ImapPort, false, new ImapHandler(Context, true), cert, log),
            new("IMAPS", s.ImapsPort, true, new ImapHandler(Context, false), cert, log),
        };

        lock (_servicesLock)
        {
            foreach (var old in _services)
                old.Stop();
            foreach (var service in services)
                service.Start(s.AllowNetworkAccess);
            _services = services;
        }
        Events.Publish("status");
    }

    private async Task StartWebAsync(bool enableHttps = true)
    {
        var s = Context.Settings.Current;
        HttpPort = FindFreePort(s.HttpPort, s.AllowNetworkAccess);
        if (HttpPort is null)
            throw new InvalidOperationException($"Não foi possível abrir a porta da interface web ({s.HttpPort}) nem as alternativas.");
        HttpsPort = enableHttps && s.HttpsPort > 0 ? FindFreePort(s.HttpsPort, s.AllowNetworkAccess, HttpPort) : null;

        Context.HttpBaseUrl = $"http://localhost:{HttpPort}";
        Context.HttpsBaseUrl = HttpsPort is null ? null : $"https://localhost:{HttpsPort}";

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Context.DataDirectory,
            EnvironmentName = Environments.Production,
            Args = [],
        });
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);
        builder.Services.AddFakeMailMcp(new MailApiClient($"http://127.0.0.1:{HttpPort}"))
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 200L * 1024 * 1024;
            void Listen(int port, Action<ListenOptions>? configure)
            {
                if (s.AllowNetworkAccess)
                    k.ListenAnyIP(port, o => configure?.Invoke(o));
                else
                    k.ListenLocalhost(port, o => configure?.Invoke(o));
            }

            Listen(HttpPort.Value, null);
            if (HttpsPort is { } https)
                Listen(https, o => o.UseHttps(Context.Certificates.Certificate));
        });

        var app = builder.Build();
        var oauth = new OAuthServer(Context);
        app.Use(async (http, next) =>
        {
            if (!await oauth.TryHandleAsync(http))
                await next(http);
        });

        var files = new ManifestEmbeddedFileProvider(typeof(FakeMailServer).Assembly, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = r => r.Context.Response.Headers.CacheControl = "no-cache",
        });
        ApiEndpoints.Map(app, this);
        app.MapMcp("/mcp");

        try
        {
            await app.StartAsync();
        }
        catch (Exception ex) when (HttpsPort is not null)
        {
            WebError = "HTTPS desativado: " + ex.Message;
            await app.DisposeAsync();
            HttpsPort = null;
            Context.HttpsBaseUrl = null;
            await StartWebAsync(false);
            return;
        }
        _web = app;
    }

    public object Status()
    {
        var s = Context.Settings.Current;
        var baseUrl = Context.HttpsBaseUrl ?? Context.HttpBaseUrl;
        var cert = Context.Certificates.Certificate;
        var all = Context.Store.List();
        return new
        {
            version = Version,
            startedAt = StartedAt,
            serverTime = DateTimeOffset.Now,
            executablePath = Environment.ProcessPath,
            mcp = new
            {
                http = $"{Context.HttpBaseUrl}/mcp",
                stdioCommand = Environment.ProcessPath,
                stdioArgs = new[] { "--mcp" },
            },
            dataDirectory = Context.DataDirectory,
            hostName = Context.HostName,
            allowNetworkAccess = s.AllowNetworkAccess,
            authentication = new
            {
                acceptAnyPassword = s.AcceptAnyPassword,
                smtpRequireAuth = s.SmtpRequireAuth,
                exampleUser = s.AcceptAnyPassword ? "teste@teste.local" : s.Users.FirstOrDefault()?.Username ?? "SEU_USUARIO",
                examplePassword = s.AcceptAnyPassword ? "qualquer" : s.Users.FirstOrDefault()?.Password ?? "SUA_SENHA",
            },
            web = new
            {
                http = Context.HttpBaseUrl,
                https = Context.HttpsBaseUrl,
                httpPort = HttpPort,
                httpsPort = HttpsPort,
                configuredHttpPort = s.HttpPort,
                configuredHttpsPort = s.HttpsPort,
                error = WebError,
            },
            services = Services.Select(x => new
            {
                name = x.Name,
                protocol = x.Protocol,
                configuredPort = x.ConfiguredPort,
                port = x.ActualPort,
                tls = x.ImplicitTls ? "implicit" : "starttls",
                running = x.Running,
                error = x.Error,
                connections = x.ActiveConnections,
            }),
            oauth = new
            {
                discovery = $"{baseUrl}/.well-known/openid-configuration",
                authorize = $"{baseUrl}/oauth2/authorize",
                token = $"{baseUrl}/oauth2/token",
                deviceCode = $"{baseUrl}/oauth2/devicecode",
                jwks = $"{baseUrl}/oauth2/jwks",
                userinfo = $"{baseUrl}/oauth2/userinfo",
                microsoftAuthority = $"{baseUrl}/fake-tenant/v2.0",
                microsoftToken = $"{baseUrl}/fake-tenant/oauth2/v2.0/token",
                httpToken = $"{Context.HttpBaseUrl}/oauth2/token",
            },
            certificate = new
            {
                trusted = Context.Certificates.IsTrusted(),
                thumbprint = cert.Thumbprint,
                subject = cert.Subject,
                notAfter = cert.NotAfter,
            },
            counts = new
            {
                messages = all.Count,
                unread = all.Count(m => !m.HasFlag(ImapFlags.Seen)),
            },
        };
    }

    public void RequestShutdown() => _shutdown.Cancel();

    public async Task WaitForShutdownAsync()
    {
        try
        {
            await Task.Delay(Timeout.Infinite, _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await _ping.DisposeAsync();
        foreach (var service in Services)
            service.Stop();
        if (_web is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _web.StopAsync(cts.Token);
            }
            catch
            {
            }
            await _web.DisposeAsync();
        }
        Context.Store.Dispose();
        Events.Dispose();
        Context.Certificates.Dispose();
        if (RemoveDataOnExit)
            RemovalFailures = DataCleaner.RemoveOwnedData(Context.DataDirectory);
    }

    public bool RemoveDataOnExit { get; set; }

    public IReadOnlyList<string> RemovalFailures { get; private set; } = [];

    private static int? FindFreePort(int configured, bool allowNetwork, int? exclude = null)
    {
        foreach (var port in TcpService.CandidatePorts(configured))
        {
            if (port == exclude)
                continue;
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
                socket.Bind(new IPEndPoint(allowNetwork ? IPAddress.Any : IPAddress.Loopback, port));
                return port;
            }
            catch (SocketException)
            {
            }
        }
        return null;
    }

    private static string SafeHostName()
    {
        try
        {
            var name = Dns.GetHostName().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(name) ? "fakeserver.local" : name;
        }
        catch
        {
            return "fakeserver.local";
        }
    }
}
