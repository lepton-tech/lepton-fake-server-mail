using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace FakeServerEmail.Tests;

[Collection("servidor")]
public sealed class ImapTests(ServerFixture fx)
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private async Task<ImapClient> ConnectAsync(SecureSocketOptions options = SecureSocketOptions.None, int? port = null)
    {
        var client = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", port ?? fx.Imap, options, _ct);
        await client.AuthenticateAsync("leitor@teste.local", "qualquer", _ct);
        return client;
    }

    [Fact]
    public async Task Fetch_de_envelope_estrutura_partes_e_mensagem_completa()
    {
        var subject = "imap-rich-" + Guid.NewGuid();
        await fx.SendAsync(ServerFixture.Rich(subject));

        using var client = await ConnectAsync();
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, _ct);
        var uids = await inbox.SearchAsync(SearchQuery.SubjectContains(subject), _ct);
        var uid = Assert.Single(uids);

        var summaries = await inbox.FetchAsync([uid], MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure | MessageSummaryItems.Flags
            | MessageSummaryItems.Size | MessageSummaryItems.InternalDate | MessageSummaryItems.UniqueId | MessageSummaryItems.Headers, _ct);
        var summary = Assert.Single(summaries);
        Assert.Equal(subject, summary.Envelope!.Subject);
        Assert.Equal("José da Silva", summary.Envelope.From.Mailboxes.Single().Name);
        Assert.Equal("jose@teste.local", summary.Envelope.From.Mailboxes.Single().Address);
        Assert.Equal("copia@teste.local", summary.Envelope.Cc.Mailboxes.Single().Address);
        Assert.False(summary.Flags!.Value.HasFlag(MessageFlags.Seen));
        Assert.Equal(subject, summary.Headers![HeaderId.Subject]);

        var attachment = Assert.Single(summary.Attachments);
        Assert.Equal("relatório.txt", attachment.FileName);
        var textPart = Assert.IsType<BodyPartText>(summary.TextBody);
        var htmlPart = Assert.IsType<BodyPartText>(summary.HtmlBody);

        var text = (TextPart)await inbox.GetBodyPartAsync(uid, textPart, _ct);
        Assert.Contains("ação, maçã", text.Text);
        var html = (TextPart)await inbox.GetBodyPartAsync(uid, htmlPart, _ct);
        Assert.Contains("versão HTML", html.Text);
        var file = (MimePart)await inbox.GetBodyPartAsync(uid, attachment, _ct);
        using (var ms = new MemoryStream())
        {
            await file.Content!.DecodeToAsync(ms, _ct);
            Assert.Equal("conteúdo do anexo\r\n", System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        }

        var full = await inbox.GetMessageAsync(uid, _ct);
        Assert.Equal(subject, full.Subject);
        Assert.Equal(fx.Server.Context.Store.List().Single(m => m.Subject == subject).Size, (long)summary.Size!.Value);

        var after = await inbox.FetchAsync([uid], MessageSummaryItems.Flags, _ct);
        Assert.False(after[0].Flags!.Value.HasFlag(MessageFlags.Seen));
        await client.DisconnectAsync(true, _ct);
    }

    [Fact]
    public async Task Sintaxe_crua_literais_secoes_parciais_e_seen()
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync("127.0.0.1", fx.Imap, _ct);
        var stream = tcp.GetStream();
        var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

        async Task<List<string>> CommandAsync(string tag, string command)
        {
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(command + "\r\n"), _ct);
            var lines = new List<string>();
            while (true)
            {
                var line = await reader.ReadLineAsync(_ct) ?? throw new IOException("conexão fechada");
                lines.Add(line);
                if (line.StartsWith(tag + " ", StringComparison.Ordinal))
                    return lines;
            }
        }

        Assert.StartsWith("* OK", await reader.ReadLineAsync(_ct));
        Assert.Contains("a1 OK", (await CommandAsync("a1", "a1 LOGIN \"cru@teste.local\" {3+}\r\nabc"))[^1]);
        Assert.Contains("a2 OK", (await CommandAsync("a2", "a2 SELECT INBOX"))[^1]);

        var subject = "cru-imap-" + Guid.NewGuid();
        var message = $"Subject: {subject}\r\nFrom: x@teste.local\r\n\r\nCorpo cru 1234567890\r\n";
        var bytes = System.Text.Encoding.UTF8.GetByteCount(message);
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"a3 APPEND INBOX (\\Flagged) {{{bytes}}}\r\n"), _ct);
        Assert.StartsWith("+", await reader.ReadLineAsync(_ct));
        var append = await CommandAsync("a3", message);
        Assert.Contains("[APPENDUID ", append[^1]);
        Assert.Contains(append, l => l.EndsWith(" EXISTS", StringComparison.Ordinal));

        var search = await CommandAsync("a4", $"a4 UID SEARCH CHARSET UTF-8 SUBJECT \"{subject}\" FLAGGED");
        var uid = search.Single(l => l.StartsWith("* SEARCH ")).Split(' ')[2];

        var headerFields = await CommandAsync("a5", $"a5 UID FETCH {uid} (BODY.PEEK[HEADER.FIELDS (SUBJECT)] BODY.PEEK[TEXT]<0.9>)");
        Assert.Contains(headerFields, l => l.Contains("BODY[HEADER.FIELDS (SUBJECT)]"));
        Assert.Contains(headerFields, l => l == "Subject: " + subject);
        Assert.Contains(headerFields, l => l.Contains("BODY[TEXT]<0> {9}"));
        Assert.DoesNotContain(headerFields, l => l.Contains("\\Seen"));

        var seen = await CommandAsync("a6", $"a6 UID FETCH {uid} BODY[TEXT]");
        Assert.Contains(seen, l => l.Contains("FLAGS (") && l.Contains("\\Seen"));

        Assert.Contains("a7 BAD", (await CommandAsync("a7", "a7 FETCH 1 (XPTO)"))[^1]);
        Assert.Contains("a8 BAD", (await CommandAsync("a8", "a8 COMANDOINVALIDO"))[^1]);
        var status = await CommandAsync("a9", "a9 STATUS INBOX (MESSAGES UNSEEN UIDNEXT UIDVALIDITY)");
        Assert.Contains(status, l => l.StartsWith("* STATUS \"INBOX\" (MESSAGES "));
        Assert.Contains("b1 OK", (await CommandAsync("b1", "b1 LOGOUT"))[^1]);
    }

    [Fact]
    public async Task Flags_busca_mover_copiar_apagar_e_append()
    {
        var tag = "imap-ops-" + Guid.NewGuid();
        await fx.SendAsync(ServerFixture.Simple(tag + " A"));
        await fx.SendAsync(ServerFixture.Simple(tag + " B", text: "palavra-unica-xyz"));
        await fx.SendAsync(ServerFixture.Simple(tag + " C"));

        using var client = await ConnectAsync(SecureSocketOptions.StartTls);
        Assert.True(client.IsSecure);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, _ct);
        var uids = await inbox.SearchAsync(SearchQuery.SubjectContains(tag), _ct);
        Assert.Equal(3, uids.Count);

        await inbox.AddFlagsAsync(uids[0], MessageFlags.Flagged | MessageFlags.Seen, true, _ct);
        var flagged = await inbox.SearchAsync(SearchQuery.SubjectContains(tag).And(SearchQuery.Flagged), _ct);
        Assert.Equal([uids[0]], flagged);
        var unseen = await inbox.SearchAsync(SearchQuery.SubjectContains(tag).And(SearchQuery.NotSeen), _ct);
        Assert.Equal(2, unseen.Count);
        var body = await inbox.SearchAsync(SearchQuery.BodyContains("palavra-unica-xyz"), _ct);
        Assert.Equal([uids[1]], body);
        var or = await inbox.SearchAsync(SearchQuery.Or(SearchQuery.SubjectContains(tag + " A"), SearchQuery.SubjectContains(tag + " C")), _ct);
        Assert.Equal(2, or.Count);

        var trash = client.GetFolder(SpecialFolder.Trash);
        Assert.NotNull(trash);
        var moved = await inbox.MoveToAsync(uids[2], trash, _ct);
        Assert.NotNull(moved);
        Assert.Equal(2, (await inbox.SearchAsync(SearchQuery.SubjectContains(tag), _ct)).Count);
        await trash.OpenAsync(FolderAccess.ReadOnly, _ct);
        Assert.Single(await trash.SearchAsync(SearchQuery.SubjectContains(tag + " C"), _ct));

        await inbox.OpenAsync(FolderAccess.ReadWrite, _ct);
        var sent = client.GetFolder(SpecialFolder.Sent)!;
        var appended = await sent.AppendAsync(ServerFixture.Simple(tag + " enviado"), MessageFlags.Seen, _ct);
        Assert.NotNull(appended);
        await sent.StatusAsync(StatusItems.Count | StatusItems.Unread | StatusItems.UidNext, _ct);
        Assert.True(sent.Count >= 1);

        var copied = await inbox.CopyToAsync(uids[0], sent, _ct);
        Assert.NotNull(copied);

        await inbox.AddFlagsAsync(uids[1], MessageFlags.Deleted, true, _ct);
        await inbox.ExpungeAsync(_ct);
        var remaining = await inbox.SearchAsync(SearchQuery.SubjectContains(tag), _ct);
        Assert.Equal([uids[0]], remaining);

        var personal = client.GetFolder(client.PersonalNamespaces[0])!;
        var created = (await personal.CreateAsync("Projeto " + tag[..12], true, _ct))!;
        await created.RenameAsync(personal, "Renomeada " + tag[..12], _ct);
        var folders = await personal.GetSubfoldersAsync(false, _ct);
        Assert.Contains(folders, f => f.Name == "Renomeada " + tag[..12]);
        await created.DeleteAsync(_ct);
        folders = await personal.GetSubfoldersAsync(false, _ct);
        Assert.DoesNotContain(folders, f => f.Name.EndsWith(tag[..12]));

        await client.DisconnectAsync(true, _ct);
    }

    [Fact]
    public async Task Idle_notifica_mensagem_nova()
    {
        using var client = await ConnectAsync(SecureSocketOptions.SslOnConnect, fx.Imaps);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, _ct);
        var initial = inbox.Count;
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inbox.CountChanged += (_, _) =>
        {
            if (inbox.Count > initial)
                arrived.TrySetResult();
        };

        using var done = new CancellationTokenSource();
        var idle = client.IdleAsync(done.Token, _ct);
        await Task.Delay(300, _ct);
        await fx.SendAsync(ServerFixture.Simple("imap-idle-" + Guid.NewGuid()));
        var finished = await Task.WhenAny(arrived.Task, Task.Delay(TimeSpan.FromSeconds(10), _ct));
        done.Cancel();
        await idle;
        Assert.Same(arrived.Task, finished);
        await client.DisconnectAsync(true, _ct);
    }

    [Fact]
    public async Task Login_e_status_funcionam_e_pastas_especiais_existem()
    {
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", fx.Imap, SecureSocketOptions.None, _ct);
        client.AuthenticationMechanisms.Clear();
        await client.AuthenticateAsync("login-simples", "senha", _ct);
        Assert.True(client.IsAuthenticated);
        Assert.NotNull(client.GetFolder(SpecialFolder.Sent));
        Assert.NotNull(client.GetFolder(SpecialFolder.Drafts));
        Assert.NotNull(client.GetFolder(SpecialFolder.Junk));
        await client.Inbox.StatusAsync(StatusItems.Count | StatusItems.Unread | StatusItems.UidValidity, _ct);
        Assert.True(client.Inbox.UidValidity > 0);
        await client.DisconnectAsync(true, _ct);
    }

    [Fact]
    public async Task Sem_STARTTLS_e_so_PLAIN_quando_configurado()
    {
        var settings = fx.Server.Context.Settings;
        var original = settings.Current.Clone();
        try
        {
            var changed = original.Clone();
            changed.OfferStartTls = false;
            changed.AuthMechanisms = ["PLAIN"];
            settings.Update(changed);

            using (var strict = new ImapClient())
                await Assert.ThrowsAsync<NotSupportedException>(() => strict.ConnectAsync("127.0.0.1", fx.Imap, SecureSocketOptions.StartTls, _ct));

            var user = "imap-plain-" + Guid.NewGuid().ToString("N") + "@teste.local";
            using var client = new ImapClient();
            await client.ConnectAsync("127.0.0.1", fx.Imap, SecureSocketOptions.StartTlsWhenAvailable, _ct);
            Assert.False(client.IsSecure);
            Assert.False(client.Capabilities.HasFlag(ImapCapabilities.StartTLS));
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
