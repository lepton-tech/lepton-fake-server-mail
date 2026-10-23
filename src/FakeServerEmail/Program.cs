using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FakeServerEmail;
using FakeServerEmail.Core;
using FakeServerEmail.Mcp;

var dataIndex = Array.FindIndex(args, a => a.Equals("--data", StringComparison.OrdinalIgnoreCase));
var dataDirectory = ResolveDataDirectory(dataIndex >= 0 && dataIndex + 1 < args.Length ? args[dataIndex + 1] : null);

if (args.Any(a => a.Equals("--mcp", StringComparison.OrdinalIgnoreCase)))
    return await McpStdioHost.RunAsync(dataDirectory);

Console.OutputEncoding = Encoding.UTF8;
try { Console.Title = "Fake Server Email"; } catch { }

var noBrowser = args.Any(a => a.Equals("--no-browser", StringComparison.OrdinalIgnoreCase));

using var mutex = new Mutex(true, SingleInstance.MutexName(dataDirectory), out var firstInstance);
if (!firstInstance)
{
    var url = SingleInstance.ReadUrl(dataDirectory);
    Write(ConsoleColor.Yellow, "O Fake Server Email já está aberto em outra janela.");
    if (url is not null)
    {
        Console.WriteLine("Abrindo a caixa de entrada: " + url);
        OpenBrowser(url);
    }
    Thread.Sleep(2500);
    return 0;
}

FakeMailServer server;
try
{
    server = await FakeMailServer.StartAsync(dataDirectory);
}
catch (Exception ex)
{
    Write(ConsoleColor.Red, "Não foi possível iniciar o Fake Server Email:");
    Console.WriteLine(ex.Message);
    Console.WriteLine();
    Console.WriteLine("Detalhes: " + ex);
    WaitForKey();
    return 1;
}

SingleInstance.WriteUrl(dataDirectory, server.WebUrl);

using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
{
    ctx.Cancel = true;
    server.RequestShutdown();
});
using var sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ =>
{
    server.Context.Store.Flush();
    server.RequestShutdown();
});
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ =>
{
    server.Context.Store.Flush();
    server.RequestShutdown();
});

PrintBanner(server);
var consoleLock = new Lock();
server.Context.Log.Activity += activity =>
{
    lock (consoleLock)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
        Console.ResetColor();
        Console.WriteLine(activity);
    }
};

if (!noBrowser && server.Context.Settings.Current.OpenBrowserOnStart)
    OpenBrowser(server.WebUrl);

await server.WaitForShutdownAsync();
Console.WriteLine();
Console.WriteLine("Encerrando...");
await server.DisposeAsync();
SingleInstance.Clear(dataDirectory);
if (server.RemoveDataOnExit)
{
    if (server.RemovalFailures.Count == 0)
    {
        Write(ConsoleColor.Green, "Dados removidos deste computador: " + dataDirectory);
        Thread.Sleep(1500);
    }
    else
    {
        Write(ConsoleColor.Yellow, "Alguns arquivos não puderam ser removidos:");
        foreach (var failure in server.RemovalFailures)
            Console.WriteLine("  " + failure);
        WaitForKey();
    }
}
return 0;

static string ResolveDataDirectory(string? requested)
{
    var candidates = new List<string>();
    if (!string.IsNullOrWhiteSpace(requested))
        candidates.Add(Path.GetFullPath(requested));
    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (!string.IsNullOrEmpty(local))
        candidates.Add(Path.Combine(local, "FakeServerEmail"));
    candidates.Add(Path.Combine(AppContext.BaseDirectory, "FakeServerEmail-dados"));
    candidates.Add(Path.Combine(Path.GetTempPath(), "FakeServerEmail"));

    foreach (var candidate in candidates)
    {
        try
        {
            Directory.CreateDirectory(candidate);
            var probe = Path.Combine(candidate, ".teste-escrita");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return candidate;
        }
        catch
        {
        }
    }
    return Path.GetTempPath();
}

static void OpenBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch
    {
        Console.WriteLine("Abra no navegador: " + url);
    }
}

static void Write(ConsoleColor color, string text)
{
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ResetColor();
}

static void WaitForKey()
{
    if (Console.IsInputRedirected)
        return;
    Console.WriteLine();
    Console.WriteLine("Pressione qualquer tecla para fechar.");
    try { Console.ReadKey(true); } catch { }
}

static void PrintBanner(FakeMailServer server)
{
    var context = server.Context;
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"  Fake Server Email {FakeMailServer.Version}");
    Console.ResetColor();
    Console.WriteLine("  Servidor de e-mail falso para testes: SMTP, POP3, IMAP e OAuth 2.0");
    Console.WriteLine();
    Console.Write("  Caixa de entrada: ");
    Write(ConsoleColor.Green, server.WebUrl);
    Console.WriteLine();
    Console.WriteLine("  Serviço           Porta   Segurança");
    var settings = context.Settings.Current;
    foreach (var s in server.Services.Where(s => s.ConfiguredPort > 0))
    {
        var security = s.ImplicitTls ? "TLS" : !settings.OfferStartTls ? "sem TLS (STARTTLS desligado)" : s.Protocol == "POP3" ? "STLS opcional" : "STARTTLS opcional";
        Console.Write($"  {s.Name,-17} ");
        if (s.Running)
        {
            Console.Write($"{s.ActualPort,-7} {security}");
            if (s.Error is not null)
                Write(ConsoleColor.Yellow, $"  ({s.Error})");
            else
                Console.WriteLine();
        }
        else
        {
            Write(ConsoleColor.Yellow, s.Error ?? "parado");
        }
    }
    var off = server.Services.Where(s => s.ConfiguredPort <= 0).Select(s => s.Name).ToList();
    if (off.Count > 0)
        Write(ConsoleColor.DarkGray, $"  Desligados: {string.Join(", ", off)} (dá para ligar em Configurações → Portas)");
    Console.WriteLine($"  {"OAuth 2.0",-17} {context.OpenIdConfigurationUrl}");
    Console.WriteLine($"  {"MCP (agentes)",-17} {context.HttpBaseUrl}/mcp  ou  FakeServerEmail.exe --mcp");
    Console.WriteLine();
    var firstUser = settings.Users.FirstOrDefault();
    Console.WriteLine(settings.AcceptAnyPassword ? "  Qualquer usuário e senha são aceitos."
        : firstUser is null ? "  Somente os usuários cadastrados são aceitos."
        : $"  Somente os usuários cadastrados são aceitos, por exemplo {firstUser.Username} com a senha {firstUser.Password}.");
    if (settings.SmtpRequireAuth)
        Console.WriteLine("  O SMTP exige login para enviar.");
    Console.WriteLine("  Dados em: " + context.DataDirectory);
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("  Feche esta janela (ou pressione Ctrl+C) para encerrar.");
    Console.WriteLine("  " + new string('─', 66));
    Console.ResetColor();
}
