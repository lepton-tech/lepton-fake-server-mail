using MailKit.Net.Smtp;
using MailKit.Security;

namespace FakeServerEmail.Tests;

public sealed class PersistenceTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Mensagens_tokens_e_portas_alternativas_sobrevivem_ao_reinicio()
    {
        var data = Path.Combine(Path.GetTempPath(), "fse-persist-" + Guid.NewGuid().ToString("N"));
        var smtpPort = ServerFixture.FreePort();
        var httpPort = ServerFixture.FreePort();
        using var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, ServerFixture.FreePort());
        blocker.Start();
        var blockedPort = ((System.Net.IPEndPoint)blocker.LocalEndpoint).Port;

        void Configure(Core.AppSettings s)
        {
            s.SmtpPort = smtpPort;
            s.SmtpSubmissionPort = 0;
            s.SmtpsPort = 0;
            s.Pop3Port = 0;
            s.Pop3sPort = 0;
            s.ImapPort = blockedPort;
            s.ImapsPort = 0;
            s.HttpPort = httpPort;
            s.HttpsPort = 0;
            s.OpenBrowserOnStart = false;
        }

        string id;
        string token;
        await using (var first = await FakeMailServer.StartAsync(data, Configure))
        {
            var imap = first.Services.Single(s => s.Name == "IMAP");
            Assert.True(imap.Running);
            Assert.NotEqual(blockedPort, imap.ActualPort);
            Assert.NotNull(imap.Error);

            using var client = new SmtpClient();
            await client.ConnectAsync("127.0.0.1", first.Services.Single(s => s.Name == "SMTP").ActualPort!.Value, SecureSocketOptions.None, _ct);
            await client.SendAsync(ServerFixture.Simple("persistente"), _ct);
            await client.DisconnectAsync(true, _ct);
            id = first.Context.Store.List().Single().Id;
            first.Context.Store.UpdateFlags([id], f => [.. f, Core.ImapFlags.Flagged]);
            token = first.Context.Tokens.IssueAccessToken("http://x", "app", "p@teste.local", "", "manual", 3600).AccessToken;
        }

        await using (var second = await FakeMailServer.StartAsync(data, Configure))
        {
            var message = second.Context.Store.Get(id);
            Assert.NotNull(message);
            Assert.Equal("persistente", message.Subject);
            Assert.True(message.HasFlag(Core.ImapFlags.Flagged));
            Assert.True(second.Context.Tokens.Validate(token).Valid);
            Assert.True(second.Context.Store.NextUid > second.Context.Store.List().Max(m => m.Uid));
        }

        try { Directory.Delete(data, true); } catch { }
    }
}
