using System.Text;
using FakeServerEmail.Core;

namespace FakeServerEmail.Hosting;

public static class ConsoleHost
{
    private const string Title = "Fake Server Email";
    private static readonly TimeSpan AlreadyRunningDelay = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan RemovedDelay = TimeSpan.FromSeconds(1.5);

    public static async Task<int> RunAsync(string dataDirectory, bool openBrowser)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ConsoleOutput.TrySetTitle(Title);

        using var mutex = new Mutex(false, SingleInstance.MutexName(dataDirectory), out var firstInstance);
        if (!firstInstance)
            return await ShowRunningInstanceAsync(dataDirectory);

        FakeMailServer server;
        try
        {
            server = await FakeMailServer.StartAsync(dataDirectory);
        }
        catch (Exception ex)
        {
            ConsoleOutput.Write(ConsoleColor.Red, "Não foi possível iniciar o Fake Server Email:");
            Console.WriteLine(ex.Message);
            Console.WriteLine();
            Console.WriteLine("Detalhes: " + ex);
            ConsoleOutput.WaitForKey();
            return 1;
        }

        SingleInstance.WriteUrl(dataDirectory, server.WebUrl);
        using var signals = new ShutdownSignals(server);
        ConsoleBanner.Print(server);
        await using (ActivityPrinter.Start(server.Context.Log))
        {
            if (openBrowser && server.Context.Settings.Current.OpenBrowserOnStart)
                Browser.Open(server.WebUrl);
            await server.WaitForShutdownAsync();
        }

        Console.WriteLine();
        Console.WriteLine("Encerrando...");
        await server.DisposeAsync();
        SingleInstance.Clear(dataDirectory);
        signals.MarkStopped();
        await ReportRemovalAsync(server, dataDirectory);
        return 0;
    }

    private static async Task<int> ShowRunningInstanceAsync(string dataDirectory)
    {
        var url = SingleInstance.ReadUrl(dataDirectory);
        ConsoleOutput.Write(ConsoleColor.Yellow, "O Fake Server Email já está aberto em outra janela.");
        if (url is not null)
        {
            Console.WriteLine("Abrindo a caixa de entrada: " + url);
            Browser.Open(url);
        }
        await Task.Delay(AlreadyRunningDelay);
        return 0;
    }

    private static async Task ReportRemovalAsync(FakeMailServer server, string dataDirectory)
    {
        if (!server.RemoveDataOnExit)
            return;
        if (server.RemovalFailures.Count == 0)
        {
            ConsoleOutput.Write(ConsoleColor.Green, "Dados removidos deste computador: " + dataDirectory);
            await Task.Delay(RemovedDelay);
            return;
        }
        ConsoleOutput.Write(ConsoleColor.Yellow, "Alguns arquivos não puderam ser removidos:");
        foreach (var failure in server.RemovalFailures)
            Console.WriteLine("  " + failure);
        ConsoleOutput.WaitForKey();
    }
}
