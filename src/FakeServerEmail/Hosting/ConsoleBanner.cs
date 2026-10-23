using FakeServerEmail.Core;
using FakeServerEmail.Protocols;

namespace FakeServerEmail.Hosting;

public static class ConsoleBanner
{
    private const int NameColumn = 17;
    private const int PortColumn = 7;
    private const int RuleLength = 66;

    public static void Print(FakeMailServer server)
    {
        var context = server.Context;
        var settings = context.Settings.Current;
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  Fake Server Email {FakeMailServer.Version}");
        Console.ResetColor();
        Console.WriteLine("  Servidor de e-mail falso para testes: SMTP, POP3, IMAP e OAuth 2.0");
        Console.WriteLine();
        Console.Write("  Caixa de entrada: ");
        ConsoleOutput.Write(ConsoleColor.Green, server.WebUrl);
        Console.WriteLine();
        PrintServices(server.Services, settings);
        Console.WriteLine($"  {"OAuth 2.0",-NameColumn} {context.OpenIdConfigurationUrl}");
        Console.WriteLine($"  {"MCP (agentes)",-NameColumn} {context.HttpBaseUrl}/mcp  ou  FakeServerEmail.exe {FakeMailServer.McpArgument}");
        Console.WriteLine();
        PrintAuthentication(settings);
        Console.WriteLine("  Dados em: " + context.DataDirectory);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  Feche esta janela (ou pressione Ctrl+C) para encerrar.");
        Console.WriteLine("  " + new string('─', RuleLength));
        Console.ResetColor();
    }

    private static void PrintServices(IReadOnlyList<TcpService> services, AppSettings settings)
    {
        Console.WriteLine("  Serviço           Porta   Segurança");
        foreach (var s in services.Where(s => s.ConfiguredPort > 0))
        {
            Console.Write($"  {s.Name,-NameColumn} ");
            if (!s.Running)
            {
                ConsoleOutput.Write(ConsoleColor.Yellow, s.Error ?? "parado");
                continue;
            }
            Console.Write($"{s.ActualPort,-PortColumn} {Security(s, settings)}");
            if (s.Error is not null)
                ConsoleOutput.Write(ConsoleColor.Yellow, $"  ({s.Error})");
            else
                Console.WriteLine();
        }

        var off = services.Where(s => s.ConfiguredPort <= 0).Select(s => s.Name).ToList();
        if (off.Count > 0)
            ConsoleOutput.Write(ConsoleColor.DarkGray, $"  Desligados: {string.Join(", ", off)} (dá para ligar em Configurações → Portas)");
    }

    private static string Security(TcpService service, AppSettings settings)
    {
        if (service.ImplicitTls)
            return "TLS";
        if (!settings.OfferStartTls)
            return "sem TLS (STARTTLS desligado)";
        return service.Protocol == "POP3" ? "STLS opcional" : "STARTTLS opcional";
    }

    private static void PrintAuthentication(AppSettings settings)
    {
        var firstUser = settings.Users.FirstOrDefault();
        if (settings.AcceptAnyPassword)
            Console.WriteLine("  Qualquer usuário e senha são aceitos.");
        else if (firstUser is null)
            Console.WriteLine("  Somente os usuários cadastrados são aceitos.");
        else
            Console.WriteLine($"  Somente os usuários cadastrados são aceitos, por exemplo {firstUser.Username} com a senha {firstUser.Password}.");
        if (settings.SmtpRequireAuth)
            Console.WriteLine("  O SMTP exige login para enviar.");
    }
}
