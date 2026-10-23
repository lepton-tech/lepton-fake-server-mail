using FakeServerEmail.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;

namespace FakeServerEmail.Mcp;

public static class McpStdioHost
{
    private const int ResolveAttempts = 60;
    private static readonly TimeSpan ResolveRetryDelay = TimeSpan.FromMilliseconds(250);

    public static async Task<int> RunAsync(string dataDirectory)
    {
        FakeMailServer? owned = null;
        Mutex? mutex = null;
        using var gate = new SemaphoreSlim(1, 1);
        using var stop = new CancellationTokenSource();

        async Task<string?> ResolveAsync(CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                if (owned is not null)
                    return owned.WebUrl;

                for (var attempt = 0; attempt < ResolveAttempts; attempt++)
                {
                    var url = SingleInstance.ReadUrl(dataDirectory);
                    if (url is not null && await SingleInstance.IsAliveAsync(url, ct))
                        return url;

                    var candidate = new Mutex(false, SingleInstance.MutexName(dataDirectory), out var created);
                    if (created)
                    {
                        try
                        {
                            owned = await FakeMailServer.StartAsync(dataDirectory);
                        }
                        catch (Exception ex)
                        {
                            candidate.Dispose();
                            throw new McpException("Não foi possível iniciar o Fake Server Email: " + ex.Message, ex);
                        }
                        mutex = candidate;
                        owned.ShutdownToken.Register(stop.Cancel);
                        SingleInstance.WriteUrl(dataDirectory, owned.WebUrl);
                        return owned.WebUrl;
                    }
                    candidate.Dispose();
                    await Task.Delay(ResolveRetryDelay, ct);
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
            var url = await ResolveAsync(stop.Token);
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
