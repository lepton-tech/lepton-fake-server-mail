using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace FakeServerEmail.Tests;

[Collection("servidor")]
public sealed class McpHttpTests(ServerFixture fx)
{
    private static readonly string[] ExpectedTools = ["server_info", "list_messages", "get_message", "wait_for_message", "get_message_source", "get_attachment", "send_email",
        "delete_messages", "mark_messages", "create_oauth_token", "list_oauth_tokens", "revoke_oauth_token", "list_connections", "get_connection_log", "update_settings"];
    private static readonly string[] RestrictedUsers = ["ana@teste.local:segredo"];
    private static readonly string[] LoginAndPlain = ["login", "PLAIN"];
    private static readonly string[] UnknownMechanism = ["PLAIN", "CRAM-MD5"];

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private Task<McpClient> ConnectAsync() =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{fx.HttpBase}/mcp") }), cancellationToken: _ct);

    [Fact]
    public async Task Expoe_as_ferramentas_com_descricao_e_instrucoes()
    {
        await using var client = await ConnectAsync();
        Assert.Equal("fake-server-email", client.ServerInfo.Name);
        Assert.Contains("wait_for_message", client.ServerInstructions, StringComparison.Ordinal);
        var tools = await client.ListToolsAsync(cancellationToken: _ct);
        Assert.Equal(ExpectedTools.Order(), tools.Select(t => t.Name).Order());
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        Assert.NotEqual(true, tools.Single(t => t.Name == "get_attachment").ProtocolTool.Annotations?.ReadOnlyHint);

        var info = await client.CallJsonAsync("server_info", null, _ct);
        var smtp = info.GetProperty("services").EnumerateArray().First(s => s.GetProperty("name").GetString() == "SMTP");
        Assert.Equal(fx.Smtp, smtp.GetProperty("port").GetInt32());
        Assert.True(info.GetProperty("authentication").GetProperty("acceptAnyPassword").GetBoolean());
        Assert.StartsWith("https://", info.GetProperty("oauth").GetProperty("discovery").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Espera_le_extrai_links_baixa_anexos_e_apaga()
    {
        await using var client = await ConnectAsync();
        var subject = "mcp-fluxo-" + Guid.NewGuid();
        var wait = client.CallJsonAsync("wait_for_message", new() { ["subject"] = subject, ["to"] = "novo@teste.local", ["timeoutSeconds"] = 20 }, _ct);

        var message = ServerFixture.Rich(subject, to: "novo@teste.local");
        var builder = new MimeKit.BodyBuilder
        {
            TextBody = "Confirme em https://app.teste.local/confirmar?token=abc123 por favor.",
            HtmlBody = "<p>Clique <a href=\"https://app.teste.local/confirmar?token=abc123&amp;x=1\">aqui</a> <img src=\"cid:logo@teste\"></p>",
        };
        var image = builder.LinkedResources.Add("logo.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="), new MimeKit.ContentType("image", "png"));
        image.ContentId = "logo@teste";
        builder.Attachments.Add("dados.csv", "id;nome\r\n1;Ana\r\n"u8.ToArray(), new MimeKit.ContentType("text", "csv"));
        message.Body = builder.ToMessageBody();
        await fx.SendAsync(message, _ct);

        var found = await wait;
        var id = found.GetProperty("id").GetString()!;
        Assert.Equal(subject, found.GetProperty("subject").GetString());
        Assert.Contains("abc123", found.GetProperty("text").GetString(), StringComparison.Ordinal);
        var links = found.GetProperty("links").EnumerateArray().Select(l => l.GetString()).ToList();
        Assert.Contains("https://app.teste.local/confirmar?token=abc123&x=1", links);
        Assert.Contains("https://app.teste.local/confirmar?token=abc123", links);
        Assert.Equal(["novo@teste.local", "copia@teste.local"], found.GetProperty("envelope").GetProperty("rcptTo").EnumerateArray().Select(e => e.GetString()));

        var list = await client.CallJsonAsync("list_messages", new() { ["subject"] = subject, ["unreadOnly"] = true }, _ct);
        Assert.Equal(1, list.GetProperty("total").GetInt32());

        var attachments = found.GetProperty("attachments").EnumerateArray().ToList();
        var csv = attachments.Single(a => a.GetProperty("fileName").GetString() == "dados.csv");
        Assert.Contains("1;Ana", await client.CallTextAsync("get_attachment", new() { ["id"] = id, ["index"] = csv.GetProperty("index").GetInt32() }, _ct), StringComparison.Ordinal);
        var png = await client.CallBlocksAsync("get_attachment", new() { ["id"] = id, ["fileName"] = "logo.png" }, _ct);
        Assert.Equal("image/png", Assert.IsType<ImageContentBlock>(png[1]).MimeType);

        var target = Path.Combine(fx.DataDirectory, "saida", "dados.csv");
        Assert.Contains(target, await client.CallTextAsync("get_attachment", new() { ["id"] = id, ["fileName"] = "dados.csv", ["saveTo"] = target }, _ct), StringComparison.Ordinal);
        Assert.Contains("1;Ana", await File.ReadAllTextAsync(target, _ct), StringComparison.Ordinal);

        Assert.Contains("Subject: " + subject, await client.CallTextAsync("get_message_source", new() { ["id"] = id }, _ct), StringComparison.Ordinal);

        var full = await client.CallJsonAsync("get_message", new() { ["id"] = id, ["includeHtml"] = true, ["includeHeaders"] = true, ["markAsRead"] = true }, _ct);
        Assert.Contains("aqui", full.GetProperty("html").GetString(), StringComparison.Ordinal);
        Assert.True(full.GetProperty("headers").GetArrayLength() > 3);
        Assert.True(fx.Server.Context.Store.Get(id)!.HasFlag(Core.ImapFlags.Seen));

        await client.CallJsonAsync("mark_messages", new() { ["ids"] = new[] { id }, ["seen"] = false }, _ct);
        Assert.False(fx.Server.Context.Store.Get(id)!.HasFlag(Core.ImapFlags.Seen));

        var deleted = await client.CallJsonAsync("delete_messages", new() { ["ids"] = new[] { id, "inexistente" } }, _ct);
        Assert.Equal(1, deleted.GetProperty("deleted").GetInt32());
        Assert.Equal("inexistente", Assert.Single(deleted.GetProperty("notFound").EnumerateArray()).GetString());
        Assert.Null(fx.Server.Context.Store.Get(id));
    }

    [Fact]
    public async Task Espera_sem_resultado_e_ids_inexistentes_retornam_erro_explicativo()
    {
        await using var client = await ConnectAsync();
        var error = await client.CallErrorAsync("wait_for_message", new() { ["subject"] = "nunca-" + Guid.NewGuid(), ["timeoutSeconds"] = 1 }, _ct);
        Assert.Contains("Nenhum e-mail combinou", error, StringComparison.Ordinal);
        var notFound = await client.CallErrorAsync("get_message", new() { ["id"] = "inexistente" }, _ct);
        Assert.Contains("não encontrado", notFound, StringComparison.Ordinal);
        var noSession = await client.CallErrorAsync("get_connection_log", new() { ["id"] = int.MaxValue }, _ct);
        Assert.Contains("Conexão não encontrada", noSession, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Envia_email_pelo_MCP_e_ele_fica_capturado()
    {
        await using var client = await ConnectAsync();
        var subject = "mcp-envio-" + Guid.NewGuid();
        var file = Path.Combine(fx.DataDirectory, "anexo-mcp.txt");
        await File.WriteAllTextAsync(file, "conteúdo", _ct);
        var sent = await client.CallJsonAsync("send_email", new()
        {
            ["to"] = "suporte@teste.local; outro@teste.local",
            ["cc"] = "copia@teste.local",
            ["subject"] = subject,
            ["text"] = "Chamado aberto",
            ["html"] = "<b>Chamado aberto</b>",
            ["from"] = "Cliente <cliente@teste.local>",
            ["attachmentPaths"] = new[] { file },
        }, _ct);
        var stored = fx.Server.Context.Store.Get(sent.GetProperty("storedId").GetString()!)!;
        Assert.Equal(subject, stored.Subject);
        Assert.Equal(3, stored.EnvelopeTo.Count);
        Assert.True(stored.HasAttachments);
    }

    [Fact]
    public async Task Send_email_faz_login_quando_o_SMTP_exige()
    {
        using var settings = fx.OverrideSettings(s =>
        {
            s.SmtpRequireAuth = true;
            s.AcceptAnyPassword = true;
        });

        await using var client = await ConnectAsync();
        var subject = "mcp-com-login-" + Guid.NewGuid();
        var sent = await client.CallJsonAsync("send_email", new() { ["to"] = "x@teste.local", ["subject"] = subject, ["from"] = "robo@teste.local" }, _ct);
        Assert.Equal("robo@teste.local", fx.Server.Context.Store.Get(sent.GetProperty("storedId").GetString()!)!.AuthenticatedUser);
    }

    [Fact]
    public async Task Tokens_OAuth_criados_e_revogados_pelo_MCP()
    {
        await using var client = await ConnectAsync();
        var token = await client.CallJsonAsync("create_oauth_token", new() { ["user"] = "mcp@teste.local", ["lifetimeSeconds"] = 300 }, _ct);
        var access = token.GetProperty("accessToken").GetString()!;
        var xoauth2 = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token.GetProperty("xoauth2").GetString()!));
        Assert.Equal($"user=mcp@teste.local\u0001auth=Bearer {access}\u0001\u0001", xoauth2);

        using (var smtp = new SmtpClient())
        {
            await smtp.ConnectAsync("127.0.0.1", fx.Smtp, SecureSocketOptions.None, _ct);
            await smtp.AuthenticateAsync(new SaslMechanismOAuth2("mcp@teste.local", access), _ct);
        }

        await client.CallJsonAsync("revoke_oauth_token", new() { ["accessToken"] = access }, _ct);
        using (var smtp = new SmtpClient())
        {
            await smtp.ConnectAsync("127.0.0.1", fx.Smtp, SecureSocketOptions.None, _ct);
            await Assert.ThrowsAsync<AuthenticationException>(() => smtp.AuthenticateAsync(new SaslMechanismOAuth2("mcp@teste.local", access), _ct));
        }

        var tokens = await client.CallJsonAsync("list_oauth_tokens", new() { ["limit"] = 50 }, _ct);
        var listed = tokens.EnumerateArray().First(t => t.GetProperty("jti").GetString() == token.GetProperty("jti").GetString());
        Assert.Equal("revogado", listed.GetProperty("status").GetString());
        Assert.Equal(1, listed.GetProperty("timesUsed").GetInt32());

        var unknown = await client.CallErrorAsync("revoke_oauth_token", new() { ["jti"] = "nao-existe" }, _ct);
        Assert.Contains("Token não encontrado", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configuracoes_e_log_de_conexoes_ajudam_a_depurar_falhas()
    {
        using var restore = fx.OverrideSettings();
        await using var client = await ConnectAsync();
        var settings = await client.CallJsonAsync("update_settings", new() { ["acceptAnyPassword"] = false, ["users"] = RestrictedUsers }, _ct);
        Assert.False(settings.GetProperty("acceptAnyPassword").GetBoolean());

        using (var smtp = new SmtpClient())
        {
            await smtp.ConnectAsync("127.0.0.1", fx.Smtp, SecureSocketOptions.None, _ct);
            await Assert.ThrowsAsync<AuthenticationException>(() => smtp.AuthenticateAsync("ana@teste.local", "errada", _ct));
        }

        var failures = await client.CallJsonAsync("list_connections", new() { ["protocol"] = "SMTP", ["onlyFailures"] = true, ["limit"] = 5 }, _ct);
        var failed = failures.EnumerateArray().First();
        Assert.True(failed.GetProperty("failures").GetInt32() > 0);
        var transcript = await client.CallTextAsync("get_connection_log", new() { ["id"] = failed.GetProperty("id").GetInt32() }, _ct);
        Assert.Contains("535 5.7.8", transcript, StringComparison.Ordinal);
        Assert.Contains("Senha incorreta", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_settings_desliga_STARTTLS_e_escolhe_mecanismos()
    {
        using var restore = fx.OverrideSettings();
        await using var client = await ConnectAsync();
        var saved = await client.CallJsonAsync("update_settings", new() { ["offerStartTls"] = false, ["authMechanisms"] = LoginAndPlain }, _ct);
        Assert.False(saved.GetProperty("offerStartTls").GetBoolean());
        Assert.Equal(["PLAIN", "LOGIN"], saved.GetProperty("authMechanisms").EnumerateArray().Select(m => m.GetString()!));

        var info = await client.CallJsonAsync("server_info", null, _ct);
        Assert.Equal(["PLAIN", "LOGIN"], info.GetProperty("authentication").GetProperty("mechanisms").EnumerateArray().Select(m => m.GetString()!));
        var smtp = info.GetProperty("services").EnumerateArray().First(s => s.GetProperty("name").GetString() == "SMTP");
        Assert.Contains("sem STARTTLS", smtp.GetProperty("security").GetString(), StringComparison.Ordinal);

        var error = await client.CallErrorAsync("update_settings", new() { ["authMechanisms"] = UnknownMechanism }, _ct);
        Assert.Contains("CRAM-MD5", error, StringComparison.Ordinal);
        Assert.Equal(["PLAIN", "LOGIN"], fx.Server.Context.Settings.Current.AuthMechanisms);
    }

    [Fact]
    public async Task Update_settings_desliga_servicos_e_troca_portas()
    {
        using var restore = fx.OverrideSettings();
        await using var client = await ConnectAsync();
        var imaps = ServerFixture.FreePort();
        var saved = await client.CallJsonAsync("update_settings", new() { ["pop3Port"] = 0, ["imapsPort"] = imaps }, _ct);
        var services = saved.GetProperty("services").EnumerateArray().ToDictionary(s => s.GetProperty("name").GetString()!);
        Assert.False(services["POP3"].GetProperty("running").GetBoolean());
        Assert.Equal(imaps, services["IMAPS"].GetProperty("port").GetInt32());
        Assert.False(fx.Server.Services.Single(s => s.Name == "POP3").Running);

        using (var imap = new ImapClient { ServerCertificateValidationCallback = fx.IsServerCertificate })
        {
            await imap.ConnectAsync("127.0.0.1", imaps, SecureSocketOptions.SslOnConnect, _ct);
            Assert.True(imap.IsSecure);
            await imap.DisconnectAsync(true, _ct);
        }

        var error = await client.CallErrorAsync("update_settings", new() { ["smtpPort"] = 70000 }, _ct);
        Assert.Contains("smtpPort", error, StringComparison.Ordinal);
        Assert.Equal(fx.Smtp, fx.Server.Context.Settings.Current.SmtpPort);
    }
}
