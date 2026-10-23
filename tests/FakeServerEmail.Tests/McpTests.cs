using System.Text.Json;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace FakeServerEmail.Tests;

internal static class McpTestExtensions
{
    public static async Task<JsonElement> CallJsonAsync(this McpClient client, string tool, Dictionary<string, object?>? args, CancellationToken ct)
    {
        var result = await client.CallToolAsync(tool, args ?? [], cancellationToken: ct);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.False(result.IsError == true, $"{tool} retornou erro: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<string> CallErrorAsync(this McpClient client, string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
        Assert.True(result.IsError == true, $"{tool} deveria falhar");
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
    }
}

[Collection("servidor")]
public sealed class McpHttpTests(ServerFixture fx)
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private Task<McpClient> ConnectAsync() =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{fx.HttpBase}/mcp") }), cancellationToken: _ct);

    [Fact]
    public async Task Expoe_as_ferramentas_com_descricao_e_instrucoes()
    {
        await using var client = await ConnectAsync();
        Assert.Equal("fake-server-email", client.ServerInfo.Name);
        Assert.Contains("wait_for_message", client.ServerInstructions);
        var tools = await client.ListToolsAsync(cancellationToken: _ct);
        string[] expected = ["server_info", "list_messages", "get_message", "wait_for_message", "get_message_source", "get_attachment", "send_email",
            "delete_messages", "mark_messages", "create_oauth_token", "list_oauth_tokens", "revoke_oauth_token", "list_connections", "get_connection_log", "update_settings"];
        Assert.Equal(expected.Order(), tools.Select(t => t.Name).Order());
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));

        var info = await client.CallJsonAsync("server_info", null, _ct);
        var smtp = info.GetProperty("services").EnumerateArray().First(s => s.GetProperty("name").GetString() == "SMTP");
        Assert.Equal(fx.Smtp, smtp.GetProperty("port").GetInt32());
        Assert.True(info.GetProperty("authentication").GetProperty("acceptAnyPassword").GetBoolean());
    }

    [Fact]
    public async Task Espera_le_extrai_links_baixa_anexos_e_apaga()
    {
        await using var client = await ConnectAsync();
        var subject = "mcp-fluxo-" + Guid.NewGuid();
        var wait = client.CallJsonAsync("wait_for_message", new() { ["subject"] = subject, ["to"] = "novo@teste.local", ["timeoutSeconds"] = 20 }, _ct);
        await Task.Delay(400, _ct);

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
        await fx.SendAsync(message);

        var found = await wait;
        var id = found.GetProperty("id").GetString()!;
        Assert.Equal(subject, found.GetProperty("subject").GetString());
        Assert.Contains("abc123", found.GetProperty("text").GetString());
        var links = found.GetProperty("links").EnumerateArray().Select(l => l.GetString()).ToList();
        Assert.Contains("https://app.teste.local/confirmar?token=abc123&x=1", links);
        Assert.Contains("https://app.teste.local/confirmar?token=abc123", links);
        Assert.Equal(["novo@teste.local", "copia@teste.local"], found.GetProperty("envelope").GetProperty("rcptTo").EnumerateArray().Select(e => e.GetString()));

        var list = await client.CallJsonAsync("list_messages", new() { ["subject"] = subject, ["unreadOnly"] = true }, _ct);
        Assert.Equal(1, list.GetProperty("total").GetInt32());

        var attachments = found.GetProperty("attachments").EnumerateArray().ToList();
        var csv = attachments.Single(a => a.GetProperty("fileName").GetString() == "dados.csv");
        var csvResult = await client.CallToolAsync("get_attachment", new Dictionary<string, object?> { ["id"] = id, ["index"] = csv.GetProperty("index").GetInt32() }, cancellationToken: _ct);
        Assert.Contains("1;Ana", ((TextContentBlock)csvResult.Content[0]).Text);
        var png = await client.CallToolAsync("get_attachment", new Dictionary<string, object?> { ["id"] = id, ["fileName"] = "logo.png" }, cancellationToken: _ct);
        var imageBlock = Assert.IsType<ImageContentBlock>(png.Content[1]);
        Assert.Equal("image/png", imageBlock.MimeType);

        var target = Path.Combine(fx.DataDirectory, "saida", "dados.csv");
        await client.CallToolAsync("get_attachment", new Dictionary<string, object?> { ["id"] = id, ["fileName"] = "dados.csv", ["saveTo"] = target }, cancellationToken: _ct);
        Assert.Contains("1;Ana", await File.ReadAllTextAsync(target, _ct));

        var source = await client.CallToolAsync("get_message_source", new Dictionary<string, object?> { ["id"] = id }, cancellationToken: _ct);
        Assert.Contains("Subject: " + subject, ((TextContentBlock)source.Content[0]).Text);

        var full = await client.CallJsonAsync("get_message", new() { ["id"] = id, ["includeHtml"] = true, ["includeHeaders"] = true, ["markAsRead"] = true }, _ct);
        Assert.Contains("aqui", full.GetProperty("html").GetString());
        Assert.True(full.GetProperty("headers").GetArrayLength() > 3);
        Assert.True(fx.Server.Context.Store.Get(id)!.HasFlag(Core.ImapFlags.Seen));

        await client.CallJsonAsync("mark_messages", new() { ["ids"] = new[] { id }, ["seen"] = false }, _ct);
        Assert.False(fx.Server.Context.Store.Get(id)!.HasFlag(Core.ImapFlags.Seen));

        await client.CallJsonAsync("delete_messages", new() { ["ids"] = new[] { id } }, _ct);
        Assert.Null(fx.Server.Context.Store.Get(id));
    }

    [Fact]
    public async Task Espera_sem_resultado_retorna_erro_explicativo()
    {
        await using var client = await ConnectAsync();
        var error = await client.CallErrorAsync("wait_for_message", new() { ["subject"] = "nunca-" + Guid.NewGuid(), ["timeoutSeconds"] = 1 }, _ct);
        Assert.Contains("Nenhum e-mail combinou", error);
        var notFound = await client.CallErrorAsync("get_message", new() { ["id"] = "inexistente" }, _ct);
        Assert.Contains("não encontrado", notFound);
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
        var storedId = sent.GetProperty("storedId").GetString()!;
        var stored = fx.Server.Context.Store.Get(storedId)!;
        Assert.Equal(subject, stored.Subject);
        Assert.Equal(3, stored.EnvelopeTo.Count);
        Assert.True(stored.HasAttachments);
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
    }

    [Fact]
    public async Task Configuracoes_e_log_de_conexoes_ajudam_a_depurar_falhas()
    {
        await using var client = await ConnectAsync();
        try
        {
            var settings = await client.CallJsonAsync("update_settings", new() { ["acceptAnyPassword"] = false, ["users"] = new[] { "ana@teste.local:segredo" } }, _ct);
            Assert.False(settings.GetProperty("acceptAnyPassword").GetBoolean());

            using (var smtp = new SmtpClient())
            {
                await smtp.ConnectAsync("127.0.0.1", fx.Smtp, SecureSocketOptions.None, _ct);
                await Assert.ThrowsAsync<AuthenticationException>(() => smtp.AuthenticateAsync("ana@teste.local", "errada", _ct));
            }

            var failures = await client.CallJsonAsync("list_connections", new() { ["protocol"] = "SMTP", ["onlyFailures"] = true, ["limit"] = 5 }, _ct);
            var failed = failures.EnumerateArray().First();
            Assert.True(failed.GetProperty("failures").GetInt32() > 0);
            var log = await client.CallToolAsync("get_connection_log", new Dictionary<string, object?> { ["id"] = failed.GetProperty("id").GetInt32() }, cancellationToken: _ct);
            var transcript = ((TextContentBlock)log.Content[0]).Text;
            Assert.Contains("535 5.7.8", transcript);
            Assert.Contains("Senha incorreta", transcript);
        }
        finally
        {
            await client.CallJsonAsync("update_settings", new() { ["acceptAnyPassword"] = true, ["users"] = Array.Empty<string>() }, _ct);
        }
    }

    [Fact]
    public async Task Update_settings_desliga_STARTTLS_e_escolhe_mecanismos()
    {
        await using var client = await ConnectAsync();
        try
        {
            var saved = await client.CallJsonAsync("update_settings", new() { ["offerStartTls"] = false, ["authMechanisms"] = new[] { "login", "PLAIN" } }, _ct);
            Assert.False(saved.GetProperty("offerStartTls").GetBoolean());
            Assert.Equal(["PLAIN", "LOGIN"], saved.GetProperty("authMechanisms").EnumerateArray().Select(m => m.GetString()!));

            var info = await client.CallJsonAsync("server_info", null, _ct);
            Assert.Equal(["PLAIN", "LOGIN"], info.GetProperty("authentication").GetProperty("mechanisms").EnumerateArray().Select(m => m.GetString()!));
            var smtp = info.GetProperty("services").EnumerateArray().First(s => s.GetProperty("name").GetString() == "SMTP");
            Assert.Contains("sem STARTTLS", smtp.GetProperty("security").GetString());

            var error = await client.CallErrorAsync("update_settings", new() { ["authMechanisms"] = new[] { "PLAIN", "CRAM-MD5" } }, _ct);
            Assert.Contains("CRAM-MD5", error);
            Assert.Equal(["PLAIN", "LOGIN"], fx.Server.Context.Settings.Current.AuthMechanisms);
        }
        finally
        {
            await client.CallJsonAsync("update_settings", new() { ["offerStartTls"] = true, ["authMechanisms"] = new[] { "PLAIN", "LOGIN", "XOAUTH2", "OAUTHBEARER" } }, _ct);
        }
    }

    [Fact]
    public async Task Update_settings_desliga_servicos_e_troca_portas()
    {
        await using var client = await ConnectAsync();
        var imaps = ServerFixture.FreePort();
        try
        {
            var saved = await client.CallJsonAsync("update_settings", new() { ["pop3Port"] = 0, ["imapsPort"] = imaps }, _ct);
            var services = saved.GetProperty("services").EnumerateArray().ToDictionary(s => s.GetProperty("name").GetString()!);
            Assert.False(services["POP3"].GetProperty("running").GetBoolean());
            Assert.Equal(imaps, services["IMAPS"].GetProperty("port").GetInt32());
            Assert.False(fx.Server.Services.Single(s => s.Name == "POP3").Running);

            using (var imap = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true })
            {
                await imap.ConnectAsync("127.0.0.1", imaps, SecureSocketOptions.SslOnConnect, _ct);
                Assert.True(imap.IsSecure);
                await imap.DisconnectAsync(true, _ct);
            }

            var error = await client.CallErrorAsync("update_settings", new() { ["smtpPort"] = 70000 }, _ct);
            Assert.Contains("smtpPort", error);
            Assert.Equal(fx.Smtp, fx.Server.Context.Settings.Current.SmtpPort);
        }
        finally
        {
            await client.CallJsonAsync("update_settings", new() { ["pop3Port"] = fx.Pop3, ["imapsPort"] = fx.Imaps }, _ct);
        }
    }
}
