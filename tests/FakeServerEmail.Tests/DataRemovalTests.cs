using System.Net.Http.Json;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace FakeServerEmail.Tests;

public sealed class DataRemovalTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static (string Data, Action<Core.AppSettings> Configure) NewServerSetup()
    {
        var data = Path.Combine(Path.GetTempPath(), "fse-remocao-" + Guid.NewGuid().ToString("N"));
        var smtp = ServerFixture.FreePort();
        var http = ServerFixture.FreePort();
        return (data, s => ServerFixture.OnlySmtpAndWeb(s, smtp, http));
    }

    private static int SmtpOf(FakeMailServer server) => server.Services.Single(s => s.Name == "SMTP").ActualPort!.Value;

    private async Task SendAsync(int port)
    {
        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None, _ct);
        await client.AuthenticateAsync("teste@teste.local", "teste", _ct);
        await client.SendAsync(ServerFixture.Simple("para apagar"), _ct);
        await client.DisconnectAsync(true, _ct);
    }

    [Fact]
    public async Task Limpar_dados_apaga_emails_tokens_e_log_mas_mantem_configuracoes()
    {
        var (data, configure) = NewServerSetup();
        try
        {
            await using var server = await FakeMailServer.StartAsync(data, configure);
            await SendAsync(SmtpOf(server));
            server.Context.Store.CreateFolder("Projetos");
            server.Context.Store.CreateFolder("Projetos/2026");
            server.Context.Tokens.IssueAccessToken("http://x", "app", "a@teste.local", "", "manual", 600);

            using var client = new HttpClient();
            using var response = await client.PostAsync($"http://127.0.0.1:{server.HttpPort}/api/data/clear", null, _ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<Dictionary<string, int>>(_ct);
            Assert.Equal(1, result!["messagesDeleted"]);
            Assert.Equal(1, result["tokensDeleted"]);

            Assert.Empty(server.Context.Store.List());
            Assert.Empty(server.Context.Tokens.List());
            Assert.Empty(server.Context.Log.List());
            Assert.DoesNotContain("Projetos", server.Context.Store.Folders);
            Assert.DoesNotContain("Projetos/2026", server.Context.Store.Folders);
            Assert.Contains("Sent", server.Context.Store.Folders);
            Assert.True(File.Exists(Path.Combine(data, "settings.json")));
            Assert.False(server.ShutdownToken.IsCancellationRequested);
            Assert.Empty(Directory.GetFiles(Path.Combine(data, "mensagens"), "*.eml"));
        }
        finally
        {
            ServerFixture.DeleteDirectory(data);
        }
    }

    [Fact]
    public async Task Remover_dados_apaga_a_pasta_inteira_e_encerra()
    {
        var (data, configure) = NewServerSetup();
        try
        {
            var server = await FakeMailServer.StartAsync(data, configure);
            await SendAsync(SmtpOf(server));
            Assert.NotEmpty(Directory.GetFiles(data));

            using (var client = new HttpClient())
            using (var response = await client.PostAsync($"http://127.0.0.1:{server.HttpPort}/api/data/remove", null, _ct))
                response.EnsureSuccessStatusCode();
            await server.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10), _ct);
            await server.DisposeAsync();

            Assert.Empty(server.RemovalFailures);
            Assert.False(Directory.Exists(data));
        }
        finally
        {
            ServerFixture.DeleteDirectory(data);
        }
    }

    [Fact]
    public async Task Remover_dados_preserva_arquivos_que_nao_sao_do_programa()
    {
        var (data, configure) = NewServerSetup();
        try
        {
            Directory.CreateDirectory(data);
            var foreign = Path.Combine(data, "meu-arquivo.txt");
            await File.WriteAllTextAsync(foreign, "não apagar", _ct);

            var server = await FakeMailServer.StartAsync(data, configure);
            await SendAsync(SmtpOf(server));
            server.RemoveDataOnExit = true;
            server.RequestShutdown();
            await server.DisposeAsync();

            Assert.Empty(server.RemovalFailures);
            Assert.Equal([foreign], Directory.GetFileSystemEntries(data));
        }
        finally
        {
            ServerFixture.DeleteDirectory(data);
        }
    }
}
