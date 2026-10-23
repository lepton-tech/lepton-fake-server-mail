using MailKit.Net.Pop3;
using MailKit.Security;

namespace FakeServerEmail.Tests;

[Collection("servidor")]
public sealed class Pop3Tests(ServerFixture fx)
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lista_baixa_e_apaga_mensagens()
    {
        var subject = "pop3-" + Guid.NewGuid();
        await fx.SendAsync(ServerFixture.Simple(subject));

        using var client = new Pop3Client { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", fx.Pop3, SecureSocketOptions.StartTls, _ct);
        Assert.True(client.IsSecure);
        await client.AuthenticateAsync("pop@teste.local", "x", _ct);
        var count = client.Count;
        Assert.True(count >= 1);

        var uids = await client.GetMessageUidsAsync(_ct);
        var headers = await client.GetMessageHeadersAsync(0, count, _ct);
        var index = headers.ToList().FindIndex(h => h[MimeKit.HeaderId.Subject] == subject);
        Assert.True(index >= 0);
        var message = await client.GetMessageAsync(index, _ct);
        Assert.Equal(subject, message.Subject);
        var stored = fx.Server.Context.Store.List().Single(m => m.Subject == subject);
        Assert.Equal(stored.Id, uids[index]);
        Assert.True(fx.Server.Context.Store.Get(stored.Id)!.HasFlag(Core.ImapFlags.Seen));

        await client.DeleteMessageAsync(index, _ct);
        await client.DisconnectAsync(true, _ct);
        Assert.Null(fx.Server.Context.Store.Get(stored.Id));
    }

    [Fact]
    public async Task Pop3s_com_USER_PASS()
    {
        using var client = new Pop3Client { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", fx.Pop3s, SecureSocketOptions.SslOnConnect, _ct);
        client.AuthenticationMechanisms.Clear();
        await client.AuthenticateAsync("usuario", "senha", _ct);
        Assert.True(client.IsAuthenticated);
        await client.NoOpAsync(_ct);
        await client.DisconnectAsync(true, _ct);
    }

    [Fact]
    public async Task Sem_STLS_e_so_PLAIN_quando_configurado()
    {
        var settings = fx.Server.Context.Settings;
        var original = settings.Current.Clone();
        try
        {
            var changed = original.Clone();
            changed.OfferStartTls = false;
            changed.AuthMechanisms = ["PLAIN"];
            settings.Update(changed);

            using (var strict = new Pop3Client())
                await Assert.ThrowsAsync<NotSupportedException>(() => strict.ConnectAsync("127.0.0.1", fx.Pop3, SecureSocketOptions.StartTls, _ct));

            var user = "pop-plain-" + Guid.NewGuid().ToString("N") + "@teste.local";
            using var client = new Pop3Client();
            await client.ConnectAsync("127.0.0.1", fx.Pop3, SecureSocketOptions.StartTlsWhenAvailable, _ct);
            Assert.False(client.IsSecure);
            Assert.Equal("PLAIN", Assert.Single(client.AuthenticationMechanisms));
            await client.AuthenticateAsync(user, "x", _ct);
            await client.DisconnectAsync(true, _ct);
            Assert.Equal("PLAIN", fx.Server.Context.Log.List().Single(s => s.User == user).AuthMechanism);
        }
        finally
        {
            settings.Update(original);
        }
    }
}
