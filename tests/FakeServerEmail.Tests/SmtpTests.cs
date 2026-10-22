using System.Net.Sockets;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace FakeServerEmail.Tests;

[Collection("servidor")]
public sealed class SmtpTests(ServerFixture fx)
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Envio_sem_autenticacao_e_armazenado_com_envelope()
    {
        var subject = "smtp-simples-" + Guid.NewGuid();
        await fx.SendAsync(ServerFixture.Simple(subject, to: "alguem@teste.local"));

        var stored = fx.Server.Context.Store.List().Single(m => m.Subject == subject);
        Assert.Equal("origem@teste.local", stored.EnvelopeFrom);
        Assert.Equal(["alguem@teste.local"], stored.EnvelopeTo);
        Assert.Equal("SMTP", stored.Source);
        Assert.Null(stored.AuthenticatedUser);
        var raw = Encoding.UTF8.GetString(fx.Server.Context.Store.GetRaw(stored.Id)!);
        Assert.StartsWith("Received: from ", raw);
    }

    [Fact]
    public async Task StartTls_com_AUTH_PLAIN_registra_usuario()
    {
        var subject = "smtp-starttls-" + Guid.NewGuid();
        using var client = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", fx.Smtp, SecureSocketOptions.StartTls, _ct);
        Assert.True(client.IsSecure);
        Assert.Contains("PLAIN", client.AuthenticationMechanisms);
        Assert.Contains("XOAUTH2", client.AuthenticationMechanisms);
        await client.AuthenticateAsync("usuario@teste.local", "qualquer-senha", _ct);
        await client.SendAsync(ServerFixture.Simple(subject), _ct);
        await client.DisconnectAsync(true, _ct);

        var stored = fx.Server.Context.Store.List().Single(m => m.Subject == subject);
        Assert.Equal("usuario@teste.local", stored.AuthenticatedUser);
    }

    [Fact]
    public async Task Smtps_com_TLS_implicito_e_AUTH_LOGIN()
    {
        var subject = "smtps-" + Guid.NewGuid();
        using var client = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", fx.Smtps, SecureSocketOptions.SslOnConnect, _ct);
        client.AuthenticationMechanisms.Remove("PLAIN");
        await client.AuthenticateAsync("login@teste.local", "x", _ct);
        await client.SendAsync(ServerFixture.Rich(subject), _ct);
        await client.DisconnectAsync(true, _ct);

        var stored = fx.Server.Context.Store.List().Single(m => m.Subject == subject);
        Assert.True(stored.HasAttachments);
        Assert.Equal("login@teste.local", stored.AuthenticatedUser);
    }

    [Fact]
    public async Task Dot_stuffing_e_pipelining_em_socket_cru()
    {
        var subject = "smtp-cru-" + Guid.NewGuid();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", fx.Smtp, _ct);
        var stream = tcp.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);

        async Task<string> ReadReplyAsync()
        {
            var sb = new StringBuilder();
            while (true)
            {
                var line = await reader.ReadLineAsync(_ct) ?? throw new IOException("conexão fechada");
                sb.AppendLine(line);
                if (line.Length < 4 || line[3] != '-')
                    return sb.ToString();
            }
        }

        Assert.StartsWith("220", await ReadReplyAsync());
        var pipelined = "EHLO cliente\r\nMAIL FROM:<a@teste.local>\r\nRCPT TO:<b@teste.local>\r\nRCPT TO:<c@teste.local>\r\nDATA\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(pipelined), _ct);
        Assert.Contains("250 HELP", await ReadReplyAsync());
        Assert.StartsWith("250", await ReadReplyAsync());
        Assert.StartsWith("250", await ReadReplyAsync());
        Assert.StartsWith("250", await ReadReplyAsync());
        Assert.StartsWith("354", await ReadReplyAsync());
        var body = $"Subject: {subject}\r\n\r\nlinha normal\r\n..linha com ponto\r\n.\r\nQUIT\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(body), _ct);
        Assert.StartsWith("250", await ReadReplyAsync());
        Assert.StartsWith("221", await ReadReplyAsync());

        var stored = fx.Server.Context.Store.List().Single(m => m.Subject == subject);
        Assert.Equal(["b@teste.local", "c@teste.local"], stored.EnvelopeTo);
        var raw = Encoding.ASCII.GetString(fx.Server.Context.Store.GetRaw(stored.Id)!);
        Assert.Contains("\r\n.linha com ponto\r\n", raw);
        Assert.DoesNotContain("..linha", raw);
    }

    [Fact]
    public async Task Comando_invalido_e_sequencia_errada_retornam_erros_corretos()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", fx.Smtp, _ct);
        var stream = tcp.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        await reader.ReadLineAsync(_ct);

        await writer.WriteLineAsync("XPTO");
        Assert.StartsWith("500", await reader.ReadLineAsync(_ct));
        await writer.WriteLineAsync("RCPT TO:<x@y>");
        Assert.StartsWith("503", await reader.ReadLineAsync(_ct));
        await writer.WriteLineAsync("AUTH CRAM-MD5");
        Assert.StartsWith("504", await reader.ReadLineAsync(_ct));
        await writer.WriteLineAsync("QUIT");
        Assert.StartsWith("221", await reader.ReadLineAsync(_ct));
    }
}
