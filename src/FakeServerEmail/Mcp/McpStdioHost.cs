using FakeServerEmail.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FakeServerEmail.Mcp;

public static class McpStdioHost
{
    public static async Task<int> RunAsync(string dataDirectory)
    {
        FakeMailServer? owned = null;
        Mutex? mutex = null;
        var gate = new SemaphoreSlim(1, 1);
        using var stop = new CancellationTokenSource();

        async Task<string?> ResolveAsync()
        {
            await gate.WaitAsync();
            try
            {
                if (owned is not null)
                    return owned.WebUrl;

                for (var attempt = 0; attempt < 60; attempt++)
                {
                    var url = SingleInstance.ReadUrl(dataDirectory);
                    if (url is not null && await SingleInstance.IsAliveAsync(url))
                        return url;

                    var candidate = new Mutex(true, SingleInstance.MutexName(dataDirectory), out var created);
                    if (created)
                    {
                        mutex = candidate;
                        owned = await FakeMailServer.StartAsync(dataDirectory);
                        owned.ShutdownToken.Register(stop.Cancel);
                        SingleInstance.WriteUrl(dataDirectory, owned.WebUrl);
                        return owned.WebUrl;
                    }
                    candidate.Dispose();
                    await Task.Delay(250);
                }
                return null;
            }
            finally
            {
                gate.Release();
            }
        }

        try
        {
            var url = await ResolveAsync();
            if (url is null)
            {
                await Console.Error.WriteLineAsync("Fake Server Email: não foi possível iniciar nem encontrar um servidor em execução.");
                return 1;
            }

            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, Args = [] });
            builder.Services.AddFakeMailMcp(new MailApiClient(url, ResolveAsync)).WithStdioServerTransport();
            using var host = builder.Build();
            await host.RunAsync(stop.Token);
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Fake Server Email (MCP): " + ex.Message);
            return 1;
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
                SingleInstance.Clear(dataDirectory);
            }
            mutex?.Dispose();
        }
    }
}
