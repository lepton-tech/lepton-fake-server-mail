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
        await fx.SendAsync(ServerFixture.Simple(subject, to: "alguem@teste.local"), _ct);

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
        using var client = new SmtpClient { ServerCertificateValidationCallback = fx.IsServerCertificate };
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
        using var client = new SmtpClient { ServerCertificateValidationCallback = fx.IsServerCertificate };
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
        await using var smtp = await RawLineClient.ConnectAsync(fx.Smtp, _ct);
        Assert.StartsWith("220", await smtp.ReadSmtpReplyAsync(), StringComparison.Ordinal);
        await smtp.WriteAsync("EHLO cliente\r\nMAIL FROM:<a@teste.local>\r\nRCPT TO:<b@teste.local>\r\nRCPT TO:<c@teste.local>\r\nDATA\r\n");
        Assert.Contains("250 HELP", await smtp.ReadSmtpReplyAsync(), StringComparison.Ordinal);
        Assert.StartsWith("250", await smtp.ReadSmtpReplyAsync(), StringComparison.Ordinal);
        Assert.StartsWith("250", await smtp.ReadSmtpReplyAsync(), StringComparison.Ordinal);
        Assert.StartsWith("250", await smtp.ReadSmtpReplyAsync(), StringComparison.Ordinal);
        Assert.StartsWith("354", await smtp.ReadSmtpReplyAsync(), StringComparison.Ordinal);
        await smtp.WriteAsync($"Subject: {subject}\r\n\r\nlinha normal\r\n..linha com ponto\r\n.\r\nQUIT\r\n");
        Assert.StartsWith("250", await smtp.ReadSmtpReplyAsync(), StringComparison.Ordinal);
        Assert.StartsWith("221", await smtp.ReadSmtpReplyAsync(), StringComparison.Ordinal);

        var stored = fx.Server.Context.Store.List().Single(m => m.Subject == subject);
        Assert.Equal(["b@teste.local", "c@teste.local"], stored.EnvelopeTo);
        var raw = Encoding.ASCII.GetString(fx.Server.Context.Store.GetRaw(stored.Id)!);
        Assert.Contains("\r\n.linha com ponto\r\n", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("..linha", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comando_invalido_e_sequencia_errada_retornam_erros_corretos()
    {
        await using var smtp = await RawLineClient.ConnectAsync(fx.Smtp, _ct);
        await smtp.ReadSmtpReplyAsync();
        Assert.StartsWith("500", await smtp.SmtpAsync("XPTO"), StringComparison.Ordinal);
        Assert.StartsWith("503", await smtp.SmtpAsync("RCPT TO:<x@y>"), StringComparison.Ordinal);
        Assert.StartsWith("504", await smtp.SmtpAsync("AUTH CRAM-MD5"), StringComparison.Ordinal);
        Assert.StartsWith("221", await smtp.SmtpAsync("QUIT"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sem_STARTTLS_e_so_PLAIN_quando_configurado()
    {
        using var settings = fx.OverrideSettings(s =>
        {
            s.OfferStartTls = false;
            s.AuthMechanisms = ["plain"];
        });

        using (var strict = new SmtpClient())
            await Assert.ThrowsAsync<NotSupportedException>(() => strict.ConnectAsync("127.0.0.1", fx.Smtp, SecureSocketOptions.StartTls, _ct));

        var subject = "smtp-so-plain-" + Guid.NewGuid();
        using (var client = new SmtpClient())
        {
            await client.ConnectAsync("127.0.0.1", fx.Smtp, SecureSocketOptions.StartTlsWhenAvailable, _ct);
            Assert.False(client.IsSecure);
            Assert.Equal("PLAIN", Assert.Single(client.AuthenticationMechanisms));
            await client.AuthenticateAsync("plain@teste.local", "x", _ct);
            await client.SendAsync(ServerFixture.Simple(subject), _ct);
            await client.DisconnectAsync(true, _ct);
        }
        Assert.Equal("plain@teste.local", fx.Server.Context.Store.List().Single(m => m.Subject == subject).AuthenticatedUser);

        await using var smtp = await RawLineClient.ConnectAsync(fx.Smtp, _ct);
        await smtp.ReadSmtpReplyAsync();
        Assert.StartsWith("502", await smtp.SmtpAsync("STARTTLS"), StringComparison.Ordinal);
        Assert.StartsWith("504", await smtp.SmtpAsync("AUTH LOGIN"), StringComparison.Ordinal);

        settings.Apply(s => s.AuthMechanisms = []);
        var ehlo = await smtp.SmtpAsync("EHLO cliente");
        Assert.DoesNotContain("AUTH", ehlo, StringComparison.Ordinal);
        Assert.DoesNotContain("STARTTLS", ehlo, StringComparison.Ordinal);
        Assert.StartsWith("504", await smtp.SmtpAsync("AUTH PLAIN"), StringComparison.Ordinal);
    }
}
