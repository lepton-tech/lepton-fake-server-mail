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
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace FakeServerEmail;

public sealed class FakeMailServer : IAsyncDisposable
{
    public const string McpArgument = "--mcp";
    private const int MaxWebStartAttempts = 5;
    private const long MaxRequestBodyBytes = 200L * 1024 * 1024;
    private const string FallbackHostName = "fakeserver.local";
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WebStopTimeout = TimeSpan.FromSeconds(3);
    private static readonly string[] LocalHostNames = ["localhost", "127.0.0.1", "[::1]"];

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _servicesLock = new();
    private readonly Timer _ping;
    private WebApplication? _web;
    private List<TcpService> _services = [];
    private int _disposed;

    private FakeMailServer(ServerContext context)
    {
        Context = context;
        _ping = new Timer(_ => Events.Publish("ping"), null, PingInterval, PingInterval);
    }

    public static string Version => typeof(FakeMailServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    public ServerContext Context { get; }

    public EventHub Events { get; } = new();

    public IReadOnlyList<TcpService> Services
    {
        get
        {
            lock (_servicesLock)
                return [.. _services];
        }
    }

    public int? HttpPort { get; private set; }

    public int? HttpsPort { get; private set; }

    public string? WebError { get; private set; }

    public string WebUrl => $"http://localhost:{HttpPort}";

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public CancellationToken ShutdownToken => _shutdown.Token;

    public bool RemoveDataOnExit { get; set; }

    public IReadOnlyList<string> RemovalFailures { get; private set; } = [];

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

        try
        {
            server.RestartProtocolServices();
            await server.StartWebAsync();
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
        return server;
    }

    public void RestartProtocolServices()
    {
        lock (_servicesLock)
        {
            var s = Context.Settings.Current;
            var cert = Context.Certificates.Certificate;
            var log = Context.Log;
            foreach (var old in _services)
                old.Dispose();
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
            foreach (var service in services)
                service.Start(s.AllowNetworkAccess);
            _services = services;
        }
        Events.Publish("status");
    }

    public ServerStatus Status()
    {
        var s = Context.Settings.Current;
        var baseUrl = Context.HttpsBaseUrl ?? Context.HttpBaseUrl;
        var firstUser = s.Users.FirstOrDefault();
        return new ServerStatus(
            Version,
            StartedAt,
            DateTimeOffset.Now,
            Environment.ProcessPath,
            new McpStatus($"{Context.HttpBaseUrl}/mcp", Environment.ProcessPath, [McpArgument]),
            Context.DataDirectory,
            Context.HostName,
            s.AllowNetworkAccess,
            new AuthenticationStatus(
                s.AcceptAnyPassword,
                s.SmtpRequireAuth,
                s.AcceptAnyPassword ? "teste@teste.local" : firstUser?.Username ?? "SEU_USUARIO",
                s.AcceptAnyPassword ? "qualquer" : firstUser?.Password ?? "SUA_SENHA"),
            new WebStatus(Context.HttpBaseUrl, Context.HttpsBaseUrl, HttpPort, HttpsPort, s.HttpPort, s.HttpsPort, WebError),
            Services.Select(x => new ServiceStatus(
                x.Name,
                x.Protocol,
                x.ConfiguredPort,
                x.ActualPort,
                x.ImplicitTls ? TlsModes.Implicit : s.OfferStartTls ? TlsModes.StartTls : TlsModes.None,
                x.Running,
                x.Error,
                x.ActiveConnections)).ToList(),
            new OAuthEndpoints(
                $"{baseUrl}/.well-known/openid-configuration",
                $"{baseUrl}/oauth2/authorize",
                $"{baseUrl}/oauth2/token",
                $"{baseUrl}/oauth2/devicecode",
                $"{baseUrl}/oauth2/jwks",
                $"{baseUrl}/oauth2/userinfo",
                $"{baseUrl}/fake-tenant/v2.0",
                $"{baseUrl}/fake-tenant/oauth2/v2.0/token",
                $"{Context.HttpBaseUrl}/oauth2/token"),
            CertificateStatus.From(Context.Certificates),
            Context.Store.Counts());
    }

    public void RequestShutdown()
    {
        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task WaitForShutdownAsync() =>
        await Task.Delay(Timeout.Infinite, _shutdown.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        RequestShutdown();
        await _ping.DisposeAsync();
        foreach (var service in Services)
            service.Dispose();
        if (_web is not null)
        {
            using var cts = new CancellationTokenSource(WebStopTimeout);
            try
            {
                await _web.StopAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            await _web.DisposeAsync();
        }
        Context.Store.Dispose();
        Context.Tokens.Dispose();
        Events.Dispose();
        Context.Certificates.Dispose();
        _shutdown.Dispose();
        if (RemoveDataOnExit)
            RemovalFailures = await DataCleaner.RemoveOwnedDataAsync(Context.DataDirectory);
    }

    private async Task StartWebAsync()
    {
        var s = Context.Settings.Current;
        var excluded = new HashSet<int>();
        var httpsEnabled = s.HttpsPort > 0;
        for (var attempt = 1; ; attempt++)
        {
            HttpPort = FindFreePort(s.HttpPort, s.AllowNetworkAccess, excluded)
                ?? throw new InvalidOperationException($"Não foi possível abrir a porta da interface web ({s.HttpPort}) nem as alternativas.");
            HttpsPort = httpsEnabled ? FindFreePort(s.HttpsPort, s.AllowNetworkAccess, [.. excluded, HttpPort.Value]) : null;
            Context.HttpBaseUrl = $"http://localhost:{HttpPort}";
            Context.HttpsBaseUrl = HttpsPort is null ? null : $"https://localhost:{HttpsPort}";

            var app = BuildWebApplication(s, HttpPort.Value, HttpsPort);
            try
            {
                await app.StartAsync();
                _web = app;
                return;
            }
            catch (IOException) when (attempt < MaxWebStartAttempts)
            {
                await app.DisposeAsync();
                excluded.Add(HttpPort.Value);
                if (HttpsPort is int busyHttps)
                    excluded.Add(busyHttps);
            }
            catch (Exception ex) when (httpsEnabled && HttpsPort is not null && ex is not IOException)
            {
                await app.DisposeAsync();
                WebError = "HTTPS desativado: " + ex.Message;
                httpsEnabled = false;
            }
        }
    }

    private WebApplication BuildWebApplication(AppSettings s, int httpPort, int? httpsPort)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Context.DataDirectory,
            EnvironmentName = Environments.Production,
            Args = [],
        });
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.Services.Configure<HostFilteringOptions>(o =>
        {
            o.AllowedHosts = s.AllowNetworkAccess ? ["*"] : LocalHostNames;
            o.IncludeFailureMessage = false;
        });
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);
        AddSharedServices(builder.Services);
        builder.Services.AddSingleton<OAuthServer>();
        builder.Services.AddFakeMailMcp(new MailApiClient($"http://127.0.0.1:{httpPort}"))
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
            void Listen(int port, Action<ListenOptions>? configure)
            {
                if (s.AllowNetworkAccess)
                    k.ListenAnyIP(port, o => configure?.Invoke(o));
                else
                    k.ListenLocalhost(port, o => configure?.Invoke(o));
            }

            Listen(httpPort, null);
            if (httpsPort is { } https)
                Listen(https, o => o.UseHttps(Context.Certificates.Certificate));
        });

        var app = builder.Build();
        app.UseMiddleware<OAuthServer>();

        var files = new ManifestEmbeddedFileProvider(typeof(FakeMailServer).Assembly, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = r => r.Context.Response.Headers.CacheControl = "no-cache",
        });
        app.MapFakeMailApi();
        app.MapMcp("/mcp");
        return app;
    }

    private void AddSharedServices(IServiceCollection services)
    {
        services.AddSingleton(this);
        services.AddSingleton(Context);
        services.AddSingleton(Context.Settings);
        services.AddSingleton(Context.Store);
        services.AddSingleton(Context.Tokens);
        services.AddSingleton(Context.Log);
        services.AddSingleton(Context.Certificates);
        services.AddSingleton(Context.Authenticator);
        services.AddSingleton(Events);
    }

    private static int? FindFreePort(int configured, bool allowNetwork, HashSet<int> excluded)
    {
        foreach (var port in TcpService.CandidatePorts(configured))
        {
            if (!excluded.Contains(port) && IsFree(port, allowNetwork))
                return port;
        }
        return null;
    }

    private static bool IsFree(int port, bool allowNetwork)
    {
        try
        {
            if (allowNetwork)
            {
                using var any = Socket.OSSupportsIPv6
                    ? new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true, ExclusiveAddressUse = true }
                    : new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
                any.Bind(new IPEndPoint(Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any, port));
                return true;
            }

            using var v4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            v4.Bind(new IPEndPoint(IPAddress.Loopback, port));
            if (!Socket.OSSupportsIPv6)
                return true;
            using var v6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            try
            {
                v6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
            }
            catch (SocketException ex) when (ex.SocketErrorCode is not (SocketError.AddressAlreadyInUse or SocketError.AccessDenied))
            {
            }
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string SafeHostName()
    {
        try
        {
            var name = Dns.GetHostName().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(name) ? FallbackHostName : name;
        }
        catch (SocketException)
        {
            return FallbackHostName;
        }
    }
}
