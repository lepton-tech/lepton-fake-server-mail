using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;

namespace FakeServerEmail.Tests;

[Collection("servidor")]
public sealed class WebApiTests(ServerFixture fx)
{
    private static readonly string[] Views = ["inbox", "oauth", "sessions", "connect", "settings"];
    private static readonly string[] TrustScopes = ["none", "user", "computer"];

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Interface_e_status_respondem()
    {
        using var http = fx.CreateHttpClient();
        var index = await http.GetStringAsync(fx.HttpBase + "/", _ct);
        Assert.Contains("Fake Server Email", index, StringComparison.Ordinal);
        Assert.Contains("js/main.js", index, StringComparison.Ordinal);
        Assert.Contains("Alpine.start()", await http.GetStringAsync(fx.HttpBase + "/js/main.js", _ct), StringComparison.Ordinal);
        Assert.Contains("--accent", await http.GetStringAsync(fx.HttpBase + "/css/base.css", _ct), StringComparison.Ordinal);
        foreach (var view in Views)
            Assert.Contains($"x-data=\"{view}\"", await http.GetStringAsync($"{fx.HttpBase}/views/{view}.html", _ct), StringComparison.Ordinal);
        using var alpine = await http.GetAsync(fx.HttpBase + "/vendor/alpine-3.17.4.esm.min.js", _ct);
        Assert.Equal("text/javascript", alpine.Content.Headers.ContentType?.MediaType);

        var status = JsonElement.Parse(await http.GetStringAsync(fx.HttpBase + "/api/status", _ct));
        var services = status.GetProperty("services").EnumerateArray().ToList();
        Assert.Contains(services, s => s.GetProperty("name").GetString() == "SMTP" && s.GetProperty("port").GetInt32() == fx.Smtp);
        Assert.Contains(services, s => s.GetProperty("name").GetString() == "SMTP submissão" && !s.GetProperty("running").GetBoolean());
        Assert.Contains(status.GetProperty("certificate").GetProperty("trustScope").GetString(), TrustScopes);
    }

    [Fact]
    public async Task Detalhe_html_com_cid_anexo_raw_e_exclusao()
    {
        var subject = "web-" + Guid.NewGuid();
        await fx.SendAsync(ServerFixture.Rich(subject), _ct);
        using var http = fx.CreateHttpClient();

        var list = await http.GetFromJsonAsync<JsonElement>(fx.HttpBase + "/api/messages?folder=INBOX", _ct);
        var item = list.EnumerateArray().Single(m => m.GetProperty("subject").GetString() == subject);
        var id = item.GetProperty("id").GetString();

        var detail = await http.GetFromJsonAsync<JsonElement>($"{fx.HttpBase}/api/messages/{id}", _ct);
        Assert.True(detail.GetProperty("hasHtml").GetBoolean());
        Assert.Contains("ação, maçã", detail.GetProperty("text").GetString(), StringComparison.Ordinal);
        var attachments = detail.GetProperty("attachments").EnumerateArray().ToList();
        Assert.Equal(2, attachments.Count);

        using var html = await http.GetAsync($"{fx.HttpBase}/api/messages/{id}/html", _ct);
        Assert.Contains("default-src 'none'", html.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        var htmlText = await html.Content.ReadAsStringAsync(_ct);
        Assert.Contains($"/api/messages/{id}/cid/logo%40teste", htmlText, StringComparison.Ordinal);
        using var image = await http.GetAsync($"{fx.HttpBase}/api/messages/{id}/cid/logo%40teste", _ct);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);

        var file = attachments.Single(a => a.GetProperty("fileName").GetString() == "relatório.txt");
        var content = await http.GetStringAsync($"{fx.HttpBase}/api/messages/{id}/parts/{file.GetProperty("index").GetInt32()}", _ct);
        Assert.Equal("conteúdo do anexo\r\n", content);

        var raw = await http.GetStringAsync($"{fx.HttpBase}/api/messages/{id}/raw", _ct);
        Assert.Contains("Subject: " + subject, raw, StringComparison.Ordinal);

        using (var seen = await http.PostAsJsonAsync($"{fx.HttpBase}/api/messages/{id}/seen", new { seen = true }, _ct))
            seen.EnsureSuccessStatusCode();
        Assert.True(fx.Server.Context.Store.Get(id!)!.HasFlag(Core.ImapFlags.Seen));

        using (var deleted = await http.DeleteAsync($"{fx.HttpBase}/api/messages/{id}", _ct))
            deleted.EnsureSuccessStatusCode();
        using var gone = await http.GetAsync($"{fx.HttpBase}/api/messages/{id}", _ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Anexos_html_e_svg_vem_isolados_e_sem_adivinhar_o_tipo()
    {
        var subject = "anexo-ativo-" + Guid.NewGuid();
        var builder = new BodyBuilder { TextBody = "veja os anexos" };
        builder.Attachments.Add("pagina.html", "<script>fetch('/api/data/remove',{method:'POST'})</script>"u8.ToArray(), new ContentType("text", "html"));
        builder.Attachments.Add("desenho.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray(), new ContentType("image", "svg+xml"));
        builder.Attachments.Add("nota.txt", "texto"u8.ToArray(), new ContentType("text", "plain"));
        var message = ServerFixture.Simple(subject);
        message.Body = builder.ToMessageBody();
        await fx.SendAsync(message, _ct);
        var id = fx.Server.Context.Store.List().Single(m => m.Subject == subject).Id;

        using var http = fx.CreateHttpClient();
        var detail = await http.GetFromJsonAsync<JsonElement>($"{fx.HttpBase}/api/messages/{id}", _ct);
        foreach (var attachment in detail.GetProperty("attachments").EnumerateArray())
        {
            using var part = await http.GetAsync($"{fx.HttpBase}/api/messages/{id}/parts/{attachment.GetProperty("index").GetInt32()}", _ct);
            Assert.Equal("nosniff", part.Headers.GetValues("X-Content-Type-Options").Single());
            var isText = attachment.GetProperty("fileName").GetString() == "nota.txt";
            Assert.Equal(!isText, part.Headers.TryGetValues("Content-Security-Policy", out var policy) && policy.Single().StartsWith("sandbox", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Pedidos_de_outro_site_sao_recusados()
    {
        using var http = fx.CreateHttpClient();

        using var crossSite = new HttpRequestMessage(HttpMethod.Post, fx.HttpBase + "/api/messages/seen-all");
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        using (var response = await http.SendAsync(crossSite, _ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("outra página", await response.Content.ReadAsStringAsync(_ct), StringComparison.Ordinal);
        }

        using var otherOrigin = new HttpRequestMessage(HttpMethod.Post, fx.HttpBase + "/api/messages/seen-all");
        otherOrigin.Headers.Add("Origin", "http://site-malicioso.example");
        using (var response = await http.SendAsync(otherOrigin, _ct))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var sameOrigin = new HttpRequestMessage(HttpMethod.Post, fx.HttpBase + "/api/messages/seen-all");
        sameOrigin.Headers.Add("Origin", fx.HttpBase);
        sameOrigin.Headers.Add("Sec-Fetch-Site", "same-origin");
        using (var response = await http.SendAsync(sameOrigin, _ct))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var crossSiteRead = new HttpRequestMessage(HttpMethod.Get, fx.HttpBase + "/api/folders");
        crossSiteRead.Headers.Add("Sec-Fetch-Site", "cross-site");
        using (var response = await http.SendAsync(crossSiteRead, _ct))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Endereco_que_nao_e_deste_computador_e_recusado()
    {
        using var http = fx.CreateHttpClient();
        using var rebinding = new HttpRequestMessage(HttpMethod.Get, fx.HttpBase + "/api/status");
        rebinding.Headers.Host = "site-malicioso.example";
        using var response = await http.SendAsync(rebinding, _ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Porta_invalida_e_recusada_e_nao_e_salva()
    {
        using var http = fx.CreateHttpClient();
        var settings = JsonNode.Parse(await http.GetStringAsync(fx.HttpBase + "/api/settings", _ct))!;
        settings["smtpPort"] = 70000;
        using var response = await http.PutAsJsonAsync(fx.HttpBase + "/api/settings", settings, _ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("smtpPort", await response.Content.ReadAsStringAsync(_ct), StringComparison.Ordinal);
        Assert.Equal(fx.Smtp, fx.Server.Context.Settings.Current.SmtpPort);
    }

    [Fact]
    public async Task Botao_enviar_teste_faz_login_quando_o_SMTP_exige()
    {
        using var settings = fx.OverrideSettings(s =>
        {
            s.SmtpRequireAuth = true;
            s.AcceptAnyPassword = false;
            s.Users = [new Core.UserAccount { Username = "painel@teste.local", Password = "123" }];
        });

        var subject = "botao-com-login-" + Guid.NewGuid();
        using var http = fx.CreateHttpClient();
        using (var response = await http.PostAsJsonAsync(fx.HttpBase + "/api/send-test", new { subject }, _ct))
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(_ct));
        Assert.Equal("painel@teste.local", fx.Server.Context.Store.List().Single(m => m.Subject == subject).AuthenticatedUser);

        settings.Apply(s => s.Users = []);
        using var refused = await http.PostAsJsonAsync(fx.HttpBase + "/api/send-test", new { subject = subject + "-sem-usuario" }, _ct);
        Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);
        Assert.Contains("nenhum usuário cadastrado", await refused.Content.ReadAsStringAsync(_ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Botao_enviar_teste_usa_o_SMTP_local()
    {
        var subject = "botao-" + Guid.NewGuid();
        using var http = fx.CreateHttpClient();
        using (var response = await http.PostAsJsonAsync(fx.HttpBase + "/api/send-test", new { from = "a@teste.local", to = "b@teste.local; c@teste.local", subject, text = "oi", html = "<b>oi</b>", attachSample = true }, _ct))
            response.EnsureSuccessStatusCode();
        var stored = fx.Server.Context.Store.List().Single(m => m.Subject == subject);
        Assert.Equal(["b@teste.local", "c@teste.local"], stored.EnvelopeTo);
        Assert.True(stored.HasAttachments);
        Assert.Equal("SMTP", stored.Source);

        using var noRecipient = await http.PostAsJsonAsync(fx.HttpBase + "/api/send-test", new { to = " ; ", subject }, _ct);
        Assert.Equal(HttpStatusCode.BadRequest, noRecipient.StatusCode);
        Assert.Contains("destinatário", await noRecipient.Content.ReadAsStringAsync(_ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Separacao_por_destinatario_e_senha_restrita()
    {
        using var settings = fx.OverrideSettings(s =>
        {
            s.SeparateMailboxesByRecipient = true;
            s.AcceptAnyPassword = false;
            s.Users = [new Core.UserAccount { Username = "ana@teste.local", Password = "123" }];
        });

        var subject = "separado-" + Guid.NewGuid();
        await fx.SendAsync(ServerFixture.Simple(subject, to: "ana@teste.local"), _ct);
        await fx.SendAsync(ServerFixture.Simple(subject + "-outro", to: "bruno@teste.local"), _ct);

        using var imap = new ImapClient();
        await imap.ConnectAsync("127.0.0.1", fx.Imap, SecureSocketOptions.None, _ct);
        await Assert.ThrowsAsync<AuthenticationException>(() => imap.AuthenticateAsync("ana@teste.local", "errada", _ct));
        await imap.AuthenticateAsync("ana@teste.local", "123", _ct);
        await imap.Inbox.OpenAsync(MailKit.FolderAccess.ReadOnly, _ct);
        var found = await imap.Inbox.SearchAsync(MailKit.Search.SearchQuery.SubjectContains(subject), _ct);
        Assert.Single(found);
        await imap.DisconnectAsync(true, _ct);
    }
}
