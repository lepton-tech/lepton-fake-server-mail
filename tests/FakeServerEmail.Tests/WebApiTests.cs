using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MailKit.Net.Imap;
using MailKit.Security;

namespace FakeServerEmail.Tests;

[Collection("servidor")]
public sealed class WebApiTests(ServerFixture fx)
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Interface_e_status_respondem()
    {
        using var http = fx.CreateHttpClient();
        var index = await http.GetStringAsync(fx.HttpBase + "/", _ct);
        Assert.Contains("Fake Server Email", index);
        Assert.Contains("js/main.js", index);
        Assert.Contains("Alpine.start()", await http.GetStringAsync(fx.HttpBase + "/js/main.js", _ct));
        Assert.Contains("--accent", await http.GetStringAsync(fx.HttpBase + "/css/base.css", _ct));
        foreach (var view in new[] { "inbox", "oauth", "sessions", "connect", "settings" })
            Assert.Contains($"x-data=\"{view}\"", await http.GetStringAsync($"{fx.HttpBase}/views/{view}.html", _ct));
        var alpine = await http.GetAsync(fx.HttpBase + "/vendor/alpine-3.17.4.esm.min.js", _ct);
        Assert.Equal("text/javascript", alpine.Content.Headers.ContentType?.MediaType);

        using var status = JsonDocument.Parse(await http.GetStringAsync(fx.HttpBase + "/api/status", _ct));
        var services = status.RootElement.GetProperty("services").EnumerateArray().ToList();
        Assert.Contains(services, s => s.GetProperty("name").GetString() == "SMTP" && s.GetProperty("port").GetInt32() == fx.Smtp);
        Assert.Contains(services, s => s.GetProperty("name").GetString() == "SMTP submissão" && !s.GetProperty("running").GetBoolean());
    }

    [Fact]
    public async Task Detalhe_html_com_cid_anexo_raw_e_exclusao()
    {
        var subject = "web-" + Guid.NewGuid();
        await fx.SendAsync(ServerFixture.Rich(subject));
        using var http = fx.CreateHttpClient();

        var list = await http.GetFromJsonAsync<JsonElement>(fx.HttpBase + "/api/messages?folder=INBOX", _ct);
        var item = list.EnumerateArray().Single(m => m.GetProperty("subject").GetString() == subject);
        var id = item.GetProperty("id").GetString();

        var detail = await http.GetFromJsonAsync<JsonElement>($"{fx.HttpBase}/api/messages/{id}", _ct);
        Assert.True(detail.GetProperty("hasHtml").GetBoolean());
        Assert.Contains("ação, maçã", detail.GetProperty("text").GetString());
        var attachments = detail.GetProperty("attachments").EnumerateArray().ToList();
        Assert.Equal(2, attachments.Count);

        var html = await http.GetAsync($"{fx.HttpBase}/api/messages/{id}/html", _ct);
        Assert.Contains("default-src 'none'", html.Headers.GetValues("Content-Security-Policy").Single());
        var htmlText = await html.Content.ReadAsStringAsync(_ct);
        Assert.Contains($"/api/messages/{id}/cid/logo%40teste", htmlText);
        var image = await http.GetAsync($"{fx.HttpBase}/api/messages/{id}/cid/logo%40teste", _ct);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);

        var file = attachments.Single(a => a.GetProperty("fileName").GetString() == "relatório.txt");
        var content = await http.GetStringAsync($"{fx.HttpBase}/api/messages/{id}/parts/{file.GetProperty("index").GetInt32()}", _ct);
        Assert.Equal("conteúdo do anexo\r\n", content);

        var raw = await http.GetStringAsync($"{fx.HttpBase}/api/messages/{id}/raw", _ct);
        Assert.Contains("Subject: " + subject, raw);

        (await http.PostAsJsonAsync($"{fx.HttpBase}/api/messages/{id}/seen", new { seen = true }, _ct)).EnsureSuccessStatusCode();
        Assert.True(fx.Server.Context.Store.Get(id!)!.HasFlag(Core.ImapFlags.Seen));

        (await http.DeleteAsync($"{fx.HttpBase}/api/messages/{id}", _ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"{fx.HttpBase}/api/messages/{id}", _ct)).StatusCode);
    }

    [Fact]
    public async Task Botao_enviar_teste_faz_login_quando_o_SMTP_exige()
    {
        var settings = fx.Server.Context.Settings;
        var original = settings.Current.Clone();
        try
        {
            var changed = original.Clone();
            changed.SmtpRequireAuth = true;
            changed.AcceptAnyPassword = false;
            changed.Users = [new Core.UserAccount { Username = "painel@teste.local", Password = "123" }];
            settings.Update(changed);

            var subject = "botao-com-login-" + Guid.NewGuid();
            using var http = fx.CreateHttpClient();
            var response = await http.PostAsJsonAsync(fx.HttpBase + "/api/send-test", new { subject }, _ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(_ct));
            Assert.Equal("painel@teste.local", fx.Server.Context.Store.List().Single(m => m.Subject == subject).AuthenticatedUser);

            changed.Users = [];
            settings.Update(changed);
            var refused = await http.PostAsJsonAsync(fx.HttpBase + "/api/send-test", new { subject = subject + "-sem-usuario" }, _ct);
            Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);
            Assert.Contains("nenhum usuário cadastrado", await refused.Content.ReadAsStringAsync(_ct));
        }
        finally
        {
            settings.Update(original);
        }
    }

    [Fact]
    public async Task Botao_enviar_teste_usa_o_SMTP_local()
    {
        var subject = "botao-" + Guid.NewGuid();
        using var http = fx.CreateHttpClient();
        var response = await http.PostAsJsonAsync(fx.HttpBase + "/api/send-test", new { from = "a@teste.local", to = "b@teste.local; c@teste.local", subject, text = "oi", html = "<b>oi</b>", attachSample = true }, _ct);
        response.EnsureSuccessStatusCode();
        var stored = fx.Server.Context.Store.List().Single(m => m.Subject == subject);
        Assert.Equal(["b@teste.local", "c@teste.local"], stored.EnvelopeTo);
        Assert.True(stored.HasAttachments);
        Assert.Contains(fx.Server.Context.Log.List(), s => s.Protocol == "SMTP" && s.MessagesReceived > 0);
    }

    [Fact]
    public async Task Separacao_por_destinatario_e_senha_restrita()
    {
        var settings = fx.Server.Context.Settings;
        var original = settings.Current.Clone();
        try
        {
            var changed = original.Clone();
            changed.SeparateMailboxesByRecipient = true;
            changed.AcceptAnyPassword = false;
            changed.Users = [new Core.UserAccount { Username = "ana@teste.local", Password = "123" }];
            settings.Update(changed);

            var subject = "separado-" + Guid.NewGuid();
            await fx.SendAsync(ServerFixture.Simple(subject, to: "ana@teste.local"));
            await fx.SendAsync(ServerFixture.Simple(subject + "-outro", to: "bruno@teste.local"));

            using var imap = new ImapClient();
            await imap.ConnectAsync("127.0.0.1", fx.Imap, SecureSocketOptions.None, _ct);
            await Assert.ThrowsAsync<AuthenticationException>(() => imap.AuthenticateAsync("ana@teste.local", "errada", _ct));
            await imap.AuthenticateAsync("ana@teste.local", "123", _ct);
            await imap.Inbox.OpenAsync(MailKit.FolderAccess.ReadOnly, _ct);
            var found = await imap.Inbox.SearchAsync(MailKit.Search.SearchQuery.SubjectContains(subject), _ct);
            Assert.Single(found);
            await imap.DisconnectAsync(true, _ct);
        }
        finally
        {
            settings.Update(original);
        }
    }
}
