using System.Diagnostics;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using ModelContextProtocol.Client;

namespace FakeServerEmail.Tests;

public sealed class McpStdioTests : IAsyncDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _data = Path.Combine(Path.GetTempPath(), "fse-stdio-" + Guid.NewGuid().ToString("N"));
    private readonly int _smtp = ServerFixture.FreePort();
    private readonly int _http = ServerFixture.FreePort();
    private readonly List<Process> _processes = [];

    private static string Executable => Path.Combine(AppContext.BaseDirectory, "FakeServerEmail.exe");

    public McpStdioTests()
    {
        Directory.CreateDirectory(_data);
        File.WriteAllText(Path.Combine(_data, "settings.json"), JsonSerializer.Serialize(new
        {
            smtpPort = _smtp,
            smtpSubmissionPort = 0,
            smtpsPort = 0,
            pop3Port = 0,
            pop3sPort = 0,
            imapPort = 0,
            imapsPort = 0,
            httpPort = _http,
            httpsPort = 0,
            openBrowserOnStart = false,
        }));
    }

    private Task<McpClient> ConnectStdioAsync() =>
        McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "fake-email",
            Command = Executable,
            Arguments = ["--mcp", "--data", _data],
        }), cancellationToken: _ct);

    private async Task SendAsync(string subject)
    {
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync("127.0.0.1", _smtp, SecureSocketOptions.None, _ct);
        await smtp.AuthenticateAsync("teste@teste.local", "teste", _ct);
        await smtp.SendAsync(ServerFixture.Simple(subject), _ct);
        await smtp.DisconnectAsync(true, _ct);
    }

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

    private Task WaitUntilAsync(Func<bool> condition, string what) => ServerFixture.WaitUntilAsync(condition, what, _ct, ProcessTimeout);

    private int RunningPid() => JsonElement.Parse(File.ReadAllText(Path.Combine(_data, "em-execucao.json"))).GetProperty("pid").GetInt32();

    [Fact]
    public async Task Stdio_sem_instancia_aberta_sobe_servidor_proprio_e_sobrevive_a_encerramento_forcado()
    {
        var subject = "stdio-proprio-" + Guid.NewGuid();
        int pid;
        await using (var client = await ConnectStdioAsync())
        {
            var info = await client.CallJsonAsync("server_info", null, _ct);
            Assert.Equal($"http://localhost:{_http}", info.GetProperty("webInterface").GetString());
            pid = RunningPid();
            await SendAsync(subject);
            var list = await client.CallJsonAsync("list_messages", new() { ["subject"] = subject }, _ct);
            Assert.Equal(1, list.GetProperty("total").GetInt32());
        }

        await WaitUntilAsync(() => ProcessGone(pid), "o processo MCP deveria terminar junto com o cliente");

        await using (var again = await ConnectStdioAsync())
        {
            var list = await again.CallJsonAsync("list_messages", new() { ["subject"] = subject }, _ct);
            Assert.Equal(1, list.GetProperty("total").GetInt32());
            var newPid = RunningPid();
            Assert.NotEqual(pid, newPid);
        }
    }

    [Fact]
    public async Task Stdio_encerra_limpo_quando_o_cliente_fecha_a_entrada()
    {
        var process = Process.Start(new ProcessStartInfo(Executable, ["--mcp", "--data", _data])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        _processes.Add(process);
        await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"teste","version":"1"}}}""");
        await process.StandardInput.FlushAsync(_ct);
        var initialize = await process.StandardOutput.ReadLineAsync(_ct);
        Assert.Contains("fake-server-email", initialize, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_data, "em-execucao.json")));

        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, process.ExitCode);
        Assert.False(File.Exists(Path.Combine(_data, "em-execucao.json")));
    }

    private static bool ProcessGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    [Fact]
    public async Task Stdio_usa_instancia_aberta_e_assume_se_ela_fechar()
    {
        var gui = Process.Start(new ProcessStartInfo(Executable, ["--data", _data, "--no-browser"])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        _processes.Add(gui);
        await WaitUntilAsync(() => File.Exists(Path.Combine(_data, "em-execucao.json")), "a instância normal deveria iniciar");

        await using var client = await ConnectStdioAsync();
        var subject = "stdio-compartilhado-" + Guid.NewGuid();
        await SendAsync(subject);
        var list = await client.CallJsonAsync("list_messages", new() { ["subject"] = subject }, _ct);
        Assert.Equal(1, list.GetProperty("total").GetInt32());

        gui.Kill();
        await gui.WaitForExitAsync(_ct);

        var again = await client.CallJsonAsync("list_messages", new() { ["subject"] = subject }, _ct);
        Assert.Equal(1, again.GetProperty("total").GetInt32());
        var subject2 = "stdio-assumiu-" + Guid.NewGuid();
        await SendAsync(subject2);
        var after = await client.CallJsonAsync("wait_for_message", new() { ["subject"] = subject2, ["timeoutSeconds"] = 10 }, _ct);
        Assert.Equal(subject2, after.GetProperty("subject").GetString());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(ProcessTimeout, CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
            }
            process.Dispose();
        }
        for (var attempt = 0; attempt < 20 && Directory.Exists(_data); attempt++)
        {
            ServerFixture.DeleteDirectory(_data);
            if (Directory.Exists(_data))
                await Task.Delay(150, CancellationToken.None);
        }
    }
}
