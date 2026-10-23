using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using FakeServerEmail.Protocols;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace FakeServerEmail.Tests;

[Collection("servidor")]
public sealed class ProtocolRobustnessTests(ServerFixture fx)
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private sealed class RawConnection : IAsyncDisposable
    {
        private readonly TcpClient _tcp = new();
        private readonly CancellationToken _ct;
        private NetworkStream _stream = null!;
        private StreamReader _reader = null!;

        private RawConnection(CancellationToken ct) => _ct = ct;

        public static async Task<RawConnection> OpenAsync(int port, CancellationToken ct)
        {
            var connection = new RawConnection(ct);
            await connection._tcp.ConnectAsync("127.0.0.1", port, ct);
            connection._stream = connection._tcp.GetStream();
            connection._reader = new StreamReader(connection._stream, new UTF8Encoding(false));
            return connection;
        }

        public async Task SendAsync(string text) => await _stream.WriteAsync(Encoding.UTF8.GetBytes(text), _ct);

        public async Task SendAsync(byte[] bytes) => await _stream.WriteAsync(bytes, _ct);

        public Task SendLineAsync(string line) => SendAsync(line + "\r\n");

        public async Task<string?> ReadLineAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return await _reader.ReadLineAsync(timeout.Token);
        }

        public async Task<List<string>> ReadUntilAsync(Func<string, bool> last)
        {
            var lines = new List<string>();
            while (true)
            {
                var line = await ReadLineAsync() ?? throw new IOException("conexão fechada");
                lines.Add(line);
                if (last(line))
                    return lines;
            }
        }

        public async Task<List<string>> CommandAsync(string tag, string command)
        {
            await SendLineAsync(command);
            return await ReadUntilAsync(l => l.StartsWith(tag + " ", StringComparison.Ordinal));
        }

        public async ValueTask DisposeAsync()
        {
            _reader?.Dispose();
            if (_stream is not null)
                await _stream.DisposeAsync();
            _tcp.Dispose();
        }
    }

    private async Task<RawConnection> OpenImapAsync(bool login)
    {
        var imap = await RawConnection.OpenAsync(fx.Imap, _ct);
        Assert.StartsWith("* OK", await imap.ReadLineAsync(), StringComparison.Ordinal);
        if (login)
            Assert.StartsWith("l1 OK", (await imap.CommandAsync("l1", "l1 LOGIN robustez@teste.local senha"))[^1], StringComparison.Ordinal);
        return imap;
    }

    private async Task<RawConnection> OpenSmtpAsync()
    {
        var smtp = await RawConnection.OpenAsync(fx.Smtp, _ct);
        Assert.StartsWith("220", await smtp.ReadLineAsync(), StringComparison.Ordinal);
        await smtp.SendLineAsync("EHLO robustez");
        await smtp.ReadUntilAsync(l => l.StartsWith("250 ", StringComparison.Ordinal));
        return smtp;
    }

    [Fact]
    public async Task Parenteses_aninhados_demais_recebem_BAD_e_o_servidor_segue_respondendo()
    {
        await using var imap = await OpenImapAsync(login: false);

        await imap.SendLineAsync(new string('(', 100_000));
        Assert.Equal("* BAD Comando ilegível", await imap.ReadLineAsync());
        Assert.StartsWith("a1 OK", (await imap.CommandAsync("a1", "a1 NOOP"))[^1], StringComparison.Ordinal);

        Assert.StartsWith("a2 OK", (await imap.CommandAsync("a2", "a2 LOGIN robustez@teste.local senha"))[^1], StringComparison.Ordinal);
        Assert.StartsWith("a3 OK", (await imap.CommandAsync("a3", "a3 SELECT INBOX"))[^1], StringComparison.Ordinal);

        var lists = await imap.CommandAsync("a4", "a4 SEARCH " + new string('(', 5000) + "ALL" + new string(')', 5000));
        Assert.Equal("a4 BAD Listas aninhadas demais", lists[^1]);
        var nots = await imap.CommandAsync("a5", "a5 SEARCH " + string.Concat(Enumerable.Repeat("NOT ", 20_000)) + "ALL");
        Assert.Equal("a5 BAD Critérios de busca aninhados demais", nots[^1]);
        var ors = await imap.CommandAsync("a6", "a6 SEARCH " + string.Concat(Enumerable.Repeat("OR ALL ", 20_000)) + "ALL");
        Assert.Equal("a6 BAD Critérios de busca aninhados demais", ors[^1]);

        Assert.Equal("a7 BAD Número inválido: muitos", (await imap.CommandAsync("a7", "a7 SEARCH LARGER muitos"))[^1]);
        Assert.Equal("a8 BAD Número inválido: 99999999999999999999", (await imap.CommandAsync("a8", "a8 FETCH 1 (BODY[]<99999999999999999999>)"))[^1]);
        Assert.StartsWith("a9 BAD", (await imap.CommandAsync("a9", "a9 FETCH 1 (BINARY[1])"))[^1], StringComparison.Ordinal);
        Assert.StartsWith("b1 OK", (await imap.CommandAsync("b1", "b1 NOOP"))[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Literal_grande_demais_antes_do_login_e_recusado()
    {
        await using var imap = await OpenImapAsync(login: false);

        await imap.SendLineAsync("a1 LOGIN {100000}");
        Assert.Equal("a1 NO [TOOBIG] Dados grandes demais antes do login (limite de 64 KB)", await imap.ReadLineAsync());
        Assert.StartsWith("a2 OK", (await imap.CommandAsync("a2", "a2 NOOP"))[^1], StringComparison.Ordinal);

        await imap.SendLineAsync("a3 LOGIN {100000+}");
        Assert.Equal("* BYE [TOOBIG] Dados grandes demais antes do login (limite de 64 KB)", await imap.ReadLineAsync());
        Assert.Null(await imap.ReadLineAsync());
    }

    [Fact]
    public async Task Append_grande_depois_do_login_chega_inteiro()
    {
        var subject = "robustez-append-" + Guid.NewGuid();
        var message = $"Subject: {subject}\r\nFrom: x@teste.local\r\n\r\n{new string('z', 300 * 1024)}\r\n";
        await using var imap = await OpenImapAsync(login: true);

        await imap.SendLineAsync($"a1 APPEND INBOX {{{Encoding.UTF8.GetByteCount(message)}}}");
        Assert.StartsWith("+", await imap.ReadLineAsync(), StringComparison.Ordinal);
        var append = await imap.CommandAsync("a1", message);
        Assert.Contains("[APPENDUID ", append[^1], StringComparison.Ordinal);

        var stored = fx.Server.Context.Store.List().Single(m => m.Subject == subject);
        Assert.Equal(message, Encoding.UTF8.GetString(fx.Server.Context.Store.GetRaw(stored.Id)!));
    }

    [Fact]
    public async Task Padrao_patologico_no_LIST_responde_rapido_e_curingas_continuam_certos()
    {
        var folder = new string('a', 40) + "-" + Random.Shared.Next(100_000, 999_999).ToString(CultureInfo.InvariantCulture);
        await using var imap = await OpenImapAsync(login: true);
        Assert.StartsWith("a1 OK", (await imap.CommandAsync("a1", $"a1 CREATE {folder}"))[^1], StringComparison.Ordinal);
        Assert.StartsWith("a2 OK", (await imap.CommandAsync("a2", $"a2 CREATE {folder}/filho"))[^1], StringComparison.Ordinal);
        try
        {
            var watch = Stopwatch.StartNew();
            var pathological = await imap.CommandAsync("a3", "a3 LIST \"\" \"" + string.Concat(Enumerable.Repeat("*a", 15)) + "*b\"");
            watch.Stop();
            Assert.StartsWith("a3 OK", pathological[^1], StringComparison.Ordinal);
            Assert.DoesNotContain(pathological, l => l.Contains(folder, StringComparison.Ordinal));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"LIST levou {watch.Elapsed}");

            var percent = await imap.CommandAsync("a4", "a4 LIST \"\" \"%\"");
            Assert.Contains(percent, l => l.EndsWith($"\"{folder}\"", StringComparison.Ordinal) && l.Contains("\\HasChildren", StringComparison.Ordinal));
            Assert.DoesNotContain(percent, l => l.Contains(folder + "/filho", StringComparison.Ordinal));

            var star = await imap.CommandAsync("a5", "a5 LIST \"\" \"*A%a*\"");
            Assert.Contains(star, l => l.EndsWith($"\"{folder}\"", StringComparison.Ordinal));
            Assert.Contains(star, l => l.EndsWith($"\"{folder}/filho\"", StringComparison.Ordinal));

            var child = await imap.CommandAsync("a6", $"a6 LIST \"{folder}/\" \"%\"");
            Assert.Contains(child, l => l.EndsWith($"\"{folder}/filho\"", StringComparison.Ordinal));

            Assert.Equal("a7 NO Nome de pasta inválido", (await imap.CommandAsync("a7", $"a7 RENAME {folder} \"\""))[^1]);
            Assert.Equal("a8 NO Nome de pasta inválido", (await imap.CommandAsync("a8", $"a8 RENAME {folder} \"/\""))[^1]);
        }
        finally
        {
            await imap.CommandAsync("a9", $"a9 DELETE {folder}");
        }
        Assert.DoesNotContain(fx.Server.Context.Store.Folders, f => f.StartsWith(folder, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Linha_longa_demais_no_IDLE_encerra_com_BYE()
    {
        await using var imap = await OpenImapAsync(login: true);
        Assert.StartsWith("a1 OK", (await imap.CommandAsync("a1", "a1 SELECT INBOX"))[^1], StringComparison.Ordinal);
        await imap.SendLineAsync("a2 IDLE");
        await imap.ReadUntilAsync(l => l.StartsWith("+ ", StringComparison.Ordinal));
        await imap.SendLineAsync(new string('x', 9000));
        var lines = await imap.ReadUntilAsync(l => l.StartsWith("* BYE", StringComparison.Ordinal));
        Assert.Equal("* BYE Linha muito longa", lines[^1]);
        Assert.Null(await imap.ReadLineAsync());
    }

    [Fact]
    public async Task BDAT_enorme_sem_MAIL_nao_aloca_o_tamanho_declarado()
    {
        await using var stuck = await OpenSmtpAsync();
        var before = GC.GetTotalAllocatedBytes(true);
        await stuck.SendAsync("BDAT 2000000000 LAST\r\nabc");
        await Task.Delay(500, _ct);

        var subject = "robustez-bdat-" + Guid.NewGuid();
        await fx.SendAsync(ServerFixture.Simple(subject), _ct);
        Assert.Contains(fx.Server.Context.Store.List(), m => m.Subject == subject);

        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        Assert.True(allocated < 500L * 1024 * 1024, $"Foram alocados {allocated} bytes");
    }

    [Fact]
    public async Task BDAT_sem_MAIL_descarta_o_bloco_e_responde_503()
    {
        await using var smtp = await OpenSmtpAsync();
        var chunk = new byte[64 * 1024];
        Array.Fill(chunk, (byte)'x');
        const int count = 48;
        await smtp.SendAsync($"BDAT {chunk.Length * count} LAST\r\n");
        for (var i = 0; i < count; i++)
            await smtp.SendAsync(chunk);
        Assert.Equal("503 5.5.1 Informe MAIL FROM e RCPT TO primeiro", await smtp.ReadLineAsync());
        await smtp.SendLineAsync("NOOP");
        Assert.Equal("250 2.0.0 OK", await smtp.ReadLineAsync());
    }

    [Fact]
    public async Task BDAT_acima_do_limite_responde_250_por_bloco_e_552_no_ultimo()
    {
        var settings = fx.Server.Context.Settings;
        var original = settings.Current.Clone();
        try
        {
            var changed = original.Clone();
            changed.MaxMessageSizeMb = 1;
            settings.Update(changed);

            await using var smtp = await OpenSmtpAsync();
            await smtp.SendLineAsync("MAIL FROM:<a@teste.local>");
            Assert.StartsWith("250", await smtp.ReadLineAsync(), StringComparison.Ordinal);
            await smtp.SendLineAsync("RCPT TO:<b@teste.local>");
            Assert.StartsWith("250", await smtp.ReadLineAsync(), StringComparison.Ordinal);

            var chunk = new byte[700 * 1024];
            Array.Fill(chunk, (byte)'y');
            await smtp.SendAsync($"BDAT {chunk.Length}\r\n");
            await smtp.SendAsync(chunk);
            Assert.Equal($"250 2.0.0 {chunk.Length} bytes recebidos", await smtp.ReadLineAsync());
            await smtp.SendAsync($"BDAT {chunk.Length} LAST\r\n");
            await smtp.SendAsync(chunk);
            Assert.Equal("552 5.3.4 Mensagem excede o limite de 1 MB", await smtp.ReadLineAsync());
            await smtp.SendLineAsync("NOOP");
            Assert.Equal("250 2.0.0 OK", await smtp.ReadLineAsync());
        }
        finally
        {
            settings.Update(original);
        }
    }

    [Fact]
    public async Task Linha_longa_no_DATA_dentro_do_limite_e_aceita()
    {
        var subject = "robustez-data-" + Guid.NewGuid();
        var longLine = new string('x', 2 * 1024 * 1024);
        await using var smtp = await OpenSmtpAsync();
        await smtp.SendLineAsync("MAIL FROM:<a@teste.local>");
        Assert.StartsWith("250", await smtp.ReadLineAsync(), StringComparison.Ordinal);
        await smtp.SendLineAsync("RCPT TO:<b@teste.local>");
        Assert.StartsWith("250", await smtp.ReadLineAsync(), StringComparison.Ordinal);
        await smtp.SendLineAsync("DATA");
        Assert.StartsWith("354", await smtp.ReadLineAsync(), StringComparison.Ordinal);

        await smtp.SendAsync($"Subject: {subject}\r\nFrom: a@teste.local\r\n\r\n{longLine}\r\nlinha com LF\n..ponto\r\n.\r\n");
        Assert.StartsWith("250 2.0.0 Mensagem aceita", await smtp.ReadLineAsync(), StringComparison.Ordinal);

        var stored = fx.Server.Context.Store.List().Single(m => m.Subject == subject);
        var raw = Encoding.UTF8.GetString(fx.Server.Context.Store.GetRaw(stored.Id)!);
        Assert.EndsWith("\r\n\r\n" + longLine + "\r\nlinha com LF\r\n.ponto\r\n", raw, StringComparison.Ordinal);

        var session = fx.Server.Context.Log.List().Single(s => s.Lines.Any(l => l.Text.Contains(subject, StringComparison.Ordinal)));
        Assert.Contains(session.Lines, l => l.Direction == '*' && l.Text.Contains("só com LF", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resposta_SASL_longa_demais_falha_sem_derrubar_a_conexao()
    {
        await using var smtp = await OpenSmtpAsync();
        await smtp.SendLineAsync("AUTH PLAIN");
        Assert.Equal("334 ", await smtp.ReadLineAsync());
        await smtp.SendLineAsync(new string('A', 100_000));
        Assert.Equal("535 5.7.8 Falha na autenticação: Resposta longa demais", await smtp.ReadLineAsync());
        await smtp.SendLineAsync("NOOP");
        Assert.Equal("250 2.0.0 OK", await smtp.ReadLineAsync());
    }

    [Fact]
    public async Task Credenciais_SASL_nao_aparecem_no_log_das_conexoes()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var password = "senha-secreta-" + id;
        var smtpPlain = $"plain-{id}@teste.local";
        var smtpLogin = $"login-{id}@teste.local";
        var smtpOAuth = $"oauth-{id}@teste.local";
        var imapPlain = $"imap-{id}@teste.local";
        var imapLogin = $"imaplogin-{id}@teste.local";
        var popPlain = $"pop-{id}@teste.local";
        var token = await fx.ManualTokenAsync(smtpOAuth, _ct);

        foreach (var mechanism in new SaslMechanism[] { new SaslMechanismPlain(smtpPlain, password), new SaslMechanismLogin(smtpLogin, password), new SaslMechanismOAuth2(smtpOAuth, token) })
        {
            using var smtp = new SmtpClient();
            await smtp.ConnectAsync("127.0.0.1", fx.Smtp, SecureSocketOptions.None, _ct);
            await smtp.AuthenticateAsync(mechanism, _ct);
            await smtp.DisconnectAsync(true, _ct);
        }
        foreach (var mechanism in new SaslMechanism[] { new SaslMechanismPlain(imapPlain, password), new SaslMechanismLogin(imapLogin, password) })
        {
            using var imap = new ImapClient();
            await imap.ConnectAsync("127.0.0.1", fx.Imap, SecureSocketOptions.None, _ct);
            await imap.AuthenticateAsync(mechanism, _ct);
            await imap.DisconnectAsync(true, _ct);
        }
        using (var pop = new Pop3Client())
        {
            await pop.ConnectAsync("127.0.0.1", fx.Pop3, SecureSocketOptions.None, _ct);
            await pop.AuthenticateAsync(new SaslMechanismPlain(popPlain, password), _ct);
            await pop.DisconnectAsync(true, _ct);
        }

        var sessions = fx.Server.Context.Log.List().Where(s => s.User?.Contains(id, StringComparison.Ordinal) == true).ToList();
        Assert.Equal(6, sessions.Count);
        var lines = sessions.SelectMany(s => s.Lines).Select(l => l.Text).ToList();
        var secrets = new[]
        {
            password,
            token,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(password)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{smtpPlain}\0{password}")),
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"user={smtpOAuth}\u0001auth=Bearer {token}\u0001\u0001")),
        };
        foreach (var secret in secrets)
            Assert.DoesNotContain(lines, l => l.Contains(secret, StringComparison.Ordinal));

        Assert.Contains(lines, l => l.Contains($"[credenciais de '{smtpPlain}' ocultas]", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains($"[usuário '{smtpLogin}']", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains($"[credenciais de '{smtpOAuth}' ocultas]", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains($"[credenciais de '{imapPlain}' ocultas]", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains($"[usuário '{imapLogin}']", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains($"[credenciais de '{popPlain}' ocultas]", StringComparison.Ordinal));
        Assert.Contains(lines, l => l == "[senha oculta]");
    }

    [Fact]
    public void Porta_fora_do_intervalo_nao_gera_candidatas()
    {
        Assert.Empty(TcpService.CandidatePorts(70_000));
        Assert.Empty(TcpService.CandidatePorts(-5));
        int[] expected = [25, 2025, 2026];
        Assert.Equal(expected, TcpService.CandidatePorts(25).Take(3).ToArray());
        Assert.All(TcpService.CandidatePorts(65_530), p => Assert.InRange(p, 1, 65_535));
    }
}
