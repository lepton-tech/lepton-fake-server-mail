using System.Net;
using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace FakeServerEmail.Tests;

public sealed class PersistenceTests
{
    private static readonly string[] AllMechanisms = ["PLAIN", "LOGIN", "XOAUTH2", "OAUTHBEARER"];
    private static readonly string[] NormalizedMechanisms = ["PLAIN", "XOAUTH2"];

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static string NewDataDirectory(string prefix) => Path.Combine(Path.GetTempPath(), $"fse-{prefix}-{Guid.NewGuid():N}");

    private static int SmtpOf(FakeMailServer server) => server.Services.Single(s => s.Name == "SMTP").ActualPort!.Value;

    [Fact]
    public async Task Mensagens_tokens_e_portas_alternativas_sobrevivem_ao_reinicio()
    {
        var data = NewDataDirectory("persist");
        var smtpPort = ServerFixture.FreePort();
        var httpPort = ServerFixture.FreePort();
        using var blocker = new TcpListener(IPAddress.Loopback, ServerFixture.FreePort());
        blocker.Start();
        var blockedPort = ((IPEndPoint)blocker.LocalEndpoint).Port;

        void Configure(Core.AppSettings s)
        {
            ServerFixture.OnlySmtpAndWeb(s, smtpPort, httpPort);
            s.ImapPort = blockedPort;
        }

        try
        {
            string id;
            string token;
            await using (var first = await FakeMailServer.StartAsync(data, Configure))
            {
                var imap = first.Services.Single(s => s.Name == "IMAP");
                Assert.True(imap.Running);
                Assert.NotEqual(blockedPort, imap.ActualPort);
                Assert.NotNull(imap.Error);

                using var client = new SmtpClient();
                await client.ConnectAsync("127.0.0.1", SmtpOf(first), SecureSocketOptions.None, _ct);
                await client.AuthenticateAsync("teste@teste.local", "teste", _ct);
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

            var unexpected = Directory.EnumerateFileSystemEntries(data).Select(Path.GetFileName)
                .Where(name => !Core.DataFiles.OwnedFiles.Contains(name) && !Core.DataFiles.OwnedDirectories.Contains(name))
                .ToList();
            Assert.Empty(unexpected);
        }
        finally
        {
            ServerFixture.DeleteDirectory(data);
        }
    }

    [Fact]
    public void Settings_antigo_ou_com_valores_invalidos_e_normalizado()
    {
        var data = NewDataDirectory("settings");
        Directory.CreateDirectory(data);
        var path = Path.Combine(data, "settings.json");
        try
        {
            File.WriteAllText(path, """{ "smtpPort": 2525 }""");
            var old = new Core.SettingsStore(data).Current;
            Assert.Equal(2525, old.SmtpPort);
            Assert.True(old.OfferStartTls);
            Assert.Equal(AllMechanisms, old.AuthMechanisms);

            File.WriteAllText(path, """{ "authMechanisms": ["xoauth2", "CRAM-MD5", "plain", "PLAIN"], "smtpPort": 70000, "httpPort": 0, "pop3Port": -1 }""");
            var normalized = new Core.SettingsStore(data).Current;
            Assert.Equal(NormalizedMechanisms, normalized.AuthMechanisms);
            Assert.Equal(25, normalized.SmtpPort);
            Assert.Equal(5080, normalized.HttpPort);
            Assert.Equal(0, normalized.Pop3Port);

            File.WriteAllText(path, "{ isto não é json");
            var recovered = new Core.SettingsStore(data).Current;
            Assert.Equal(25, recovered.SmtpPort);
            Assert.True(File.Exists(path + ".invalido"));
        }
        finally
        {
            ServerFixture.DeleteDirectory(data);
        }
    }

    [Fact]
    public async Task Padrao_liga_so_o_SMTP_e_exige_login_com_o_usuario_teste()
    {
        var data = NewDataDirectory("padrao");
        var smtpPort = ServerFixture.FreePort();
        var httpPort = ServerFixture.FreePort();
        try
        {
            Directory.CreateDirectory(data);
            var fresh = new Core.SettingsStore(data).Current;
            Assert.Equal((25, 587), (fresh.SmtpPort, fresh.SmtpSubmissionPort));
            Assert.All(new[] { fresh.SmtpsPort, fresh.Pop3Port, fresh.Pop3sPort, fresh.ImapPort, fresh.ImapsPort }, port => Assert.Equal(0, port));

            await using (var server = await FakeMailServer.StartAsync(data, s => ServerFixture.OnlySmtpAndWeb(s, smtpPort, httpPort)))
            {
                var settings = server.Context.Settings.Current;
                Assert.False(settings.AcceptAnyPassword);
                Assert.True(settings.SmtpRequireAuth);
                var user = Assert.Single(settings.Users);
                Assert.Equal(("teste@teste.local", "teste"), (user.Username, user.Password));

                using var client = new SmtpClient();
                await client.ConnectAsync("127.0.0.1", SmtpOf(server), SecureSocketOptions.None, _ct);
                var refused = await Assert.ThrowsAsync<MailKit.ServiceNotAuthenticatedException>(() => client.SendAsync(ServerFixture.Simple("sem login"), _ct));
                Assert.Contains("Autenticação obrigatória", refused.Message, StringComparison.Ordinal);
                await Assert.ThrowsAsync<AuthenticationException>(() => client.AuthenticateAsync("teste@teste.local", "errada", _ct));
                await client.AuthenticateAsync("teste@teste.local", "teste", _ct);
                await client.SendAsync(ServerFixture.Simple("com login"), _ct);
                await client.DisconnectAsync(true, _ct);
                Assert.Equal("teste@teste.local", server.Context.Store.List().Single().AuthenticatedUser);
            }

            File.WriteAllText(Path.Combine(data, "settings.json"), """{ "acceptAnyPassword": true, "users": [] }""");
            var chosen = new Core.SettingsStore(data).Current;
            Assert.True(chosen.AcceptAnyPassword);
            Assert.Empty(chosen.Users);
        }
        finally
        {
            ServerFixture.DeleteDirectory(data);
        }
    }
}
