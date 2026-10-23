using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Identity.Client;

namespace FakeServerEmail.Tests;

[Collection("servidor")]
public sealed class OAuthTests(ServerFixture fx)
{
    private const string Redirect = "http://localhost:9999/callback";

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private async Task<JsonElement> JsonAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return JsonElement.Parse(await response.Content.ReadAsStringAsync(_ct));
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => KeyValuePair.Create(f.Key, f.Value)));

    private static JsonElement Payload(string jwt) => JsonElement.Parse(Base64Url.DecodeFromChars(jwt.Split('.')[1]));

    [Fact]
    public async Task Token_client_credentials_autentica_SMTP_IMAP_e_POP3()
    {
        var token = await fx.ClientCredentialsTokenAsync(_ct);
        var subject = "oauth-smtp-" + Guid.NewGuid();
        await fx.SendAsync(ServerFixture.Simple(subject), _ct, c => c.AuthenticateAsync(new SaslMechanismOAuth2("app@teste.local", token), _ct));
        Assert.Equal("app@teste.local", fx.Server.Context.Store.List().Single(m => m.Subject == subject).AuthenticatedUser);

        using (var imap = new ImapClient())
        {
            await imap.ConnectAsync("127.0.0.1", fx.Imap, SecureSocketOptions.None, _ct);
            await imap.AuthenticateAsync(new SaslMechanismOAuthBearer("app@teste.local", token), _ct);
            Assert.True(imap.IsAuthenticated);
            await imap.DisconnectAsync(true, _ct);
        }

        using (var imap = new ImapClient())
        {
            await imap.ConnectAsync("127.0.0.1", fx.Imap, SecureSocketOptions.None, _ct);
            await imap.AuthenticateAsync(new SaslMechanismOAuth2("app@teste.local", token), _ct);
            Assert.True(imap.IsAuthenticated);
            await imap.DisconnectAsync(true, _ct);
        }

        using var pop = new Pop3Client();
        await pop.ConnectAsync("127.0.0.1", fx.Pop3, SecureSocketOptions.None, _ct);
        await pop.AuthenticateAsync(new SaslMechanismOAuth2("app@teste.local", token), _ct);
        Assert.True(pop.IsAuthenticated);
        await pop.DisconnectAsync(true, _ct);
    }

    private async Task AssertRejectedAsync(string user, string token)
    {
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync("127.0.0.1", fx.Smtp, SecureSocketOptions.None, _ct);
        await Assert.ThrowsAsync<AuthenticationException>(() => smtp.AuthenticateAsync(new SaslMechanismOAuth2(user, token), _ct));
        using var imap = new ImapClient();
        await imap.ConnectAsync("127.0.0.1", fx.Imap, SecureSocketOptions.None, _ct);
        await Assert.ThrowsAsync<AuthenticationException>(() => imap.AuthenticateAsync(new SaslMechanismOAuthBearer(user, token), _ct));
    }

    [Fact]
    public async Task Tokens_invalidos_expirados_revogados_ou_de_outro_usuario_sao_recusados()
    {
        await AssertRejectedAsync("a@teste.local", "token-inventado");

        var other = await fx.ManualTokenAsync("dono@teste.local", _ct);
        await AssertRejectedAsync("intruso@teste.local", other);

        var shortLived = await fx.ManualTokenAsync("curto@teste.local", _ct, 1);
        await Task.Delay(2100, _ct);
        await AssertRejectedAsync("curto@teste.local", shortLived);

        var revoked = await fx.ManualTokenAsync("revogado@teste.local", _ct);
        var jti = fx.Server.Context.Tokens.List().First(t => t.AccessToken == revoked).Jti;
        using (var http = fx.CreateHttpClient())
        using (var response = await http.DeleteAsync($"{fx.HttpBase}/api/oauth/tokens/{jti}", _ct))
            response.EnsureSuccessStatusCode();
        await AssertRejectedAsync("revogado@teste.local", revoked);

        var valid = await fx.ManualTokenAsync("dono@teste.local", _ct);
        using var ok = new SmtpClient();
        await ok.ConnectAsync("127.0.0.1", fx.Smtp, SecureSocketOptions.None, _ct);
        await ok.AuthenticateAsync(new SaslMechanismOAuth2("dono@teste.local", valid), _ct);
        Assert.True(ok.IsAuthenticated);
    }

    [Fact]
    public async Task Token_apagado_da_lista_e_id_token_nao_servem_para_entrar_no_email()
    {
        using var http = fx.CreateHttpClient();
        var password = await JsonAsync(http.PostAsync($"{fx.HttpBase}/oauth2/token",
            Form(("grant_type", "password"), ("username", "id@teste.local"), ("password", "x"), ("client_id", "app"), ("scope", "openid email")), _ct));
        var idToken = password.GetProperty("id_token").GetString()!;
        await AssertRejectedAsync("id@teste.local", idToken);
        Assert.Contains("id_token", fx.Server.Context.Tokens.Validate(idToken).Error, StringComparison.Ordinal);

        using var userinfo = new HttpRequestMessage(HttpMethod.Get, $"{fx.HttpBase}/oauth2/userinfo");
        userinfo.Headers.Authorization = new("Bearer", idToken);
        using (var response = await http.SendAsync(userinfo, _ct))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var cleared = await fx.ManualTokenAsync("apagado@teste.local", _ct);
        using (var response = await http.DeleteAsync($"{fx.HttpBase}/api/oauth/tokens", _ct))
            response.EnsureSuccessStatusCode();
        await AssertRejectedAsync("apagado@teste.local", cleared);
        Assert.Contains("Token desconhecido", fx.Server.Context.Tokens.Validate(cleared).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fluxo_authorization_code_com_PKCE_e_refresh()
    {
        using var http = fx.CreateHttpClient(followRedirects: false);
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = $"client_id=web-app&response_type=code&redirect_uri={Uri.EscapeDataString(Redirect)}&scope={Uri.EscapeDataString("openid email offline_access https://outlook.office.com/IMAP.AccessAsUser.All")}&state=xyz&code_challenge={challenge}&code_challenge_method=S256&nonce=n1&login_hint=maria@teste.local";

        var page = await http.GetStringAsync($"{fx.HttpBase}/common/oauth2/v2.0/authorize?{query}", _ct);
        Assert.Contains("maria@teste.local", page, StringComparison.Ordinal);

        var code = await AuthorizeAsync(http, query);
        var wrong = await JsonAsync(http.PostAsync($"{fx.HttpBase}/common/oauth2/v2.0/token",
            Form(("grant_type", "authorization_code"), ("code", code), ("redirect_uri", Redirect), ("client_id", "web-app"), ("code_verifier", "errado")), _ct));
        Assert.Equal("invalid_grant", wrong.GetProperty("error").GetString());

        code = await AuthorizeAsync(http, query);
        var json = await JsonAsync(http.PostAsync($"{fx.HttpBase}/common/oauth2/v2.0/token",
            Form(("grant_type", "authorization_code"), ("code", code), ("redirect_uri", Redirect), ("client_id", "web-app"), ("code_verifier", verifier)), _ct));
        var access = json.GetProperty("access_token").GetString()!;
        var refresh = json.GetProperty("refresh_token").GetString()!;
        var idPayload = Payload(json.GetProperty("id_token").GetString()!);
        Assert.Equal("maria@teste.local", idPayload.GetProperty("email").GetString());
        Assert.Equal("n1", idPayload.GetProperty("nonce").GetString());
        Assert.Equal($"{fx.HttpBase}/common/v2.0", idPayload.GetProperty("iss").GetString());

        using (var imap = new ImapClient())
        {
            await imap.ConnectAsync("127.0.0.1", fx.Imap, SecureSocketOptions.None, _ct);
            await imap.AuthenticateAsync(new SaslMechanismOAuth2("maria@teste.local", access), _ct);
            Assert.True(imap.IsAuthenticated);
        }

        var refreshed = await JsonAsync(http.PostAsync($"{fx.HttpBase}/common/oauth2/v2.0/token",
            Form(("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", "web-app")), _ct));
        Assert.NotEqual(access, refreshed.GetProperty("access_token").GetString());

        var reuse = await JsonAsync(http.PostAsync($"{fx.HttpBase}/common/oauth2/v2.0/token",
            Form(("grant_type", "authorization_code"), ("code", code), ("redirect_uri", Redirect), ("client_id", "web-app"), ("code_verifier", verifier)), _ct));
        Assert.Equal("invalid_grant", reuse.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PKCE_sem_metodo_usa_plain_e_metodo_desconhecido_e_recusado()
    {
        using var http = fx.CreateHttpClient(followRedirects: false);
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var plainQuery = $"client_id=web-app&response_type=code&redirect_uri={Uri.EscapeDataString(Redirect)}&scope=openid&code_challenge={verifier}";
        var code = await AuthorizeAsync(http, plainQuery);
        var token = await JsonAsync(http.PostAsync($"{fx.HttpBase}/oauth2/token",
            Form(("grant_type", "authorization_code"), ("code", code), ("redirect_uri", Redirect), ("client_id", "web-app"), ("code_verifier", verifier)), _ct));
        Assert.True(token.TryGetProperty("access_token", out _), token.ToString());

        using var unknown = await http.GetAsync($"{fx.HttpBase}/oauth2/authorize?{plainQuery}&code_challenge_method=S512&state=s", _ct);
        Assert.Equal(HttpStatusCode.Redirect, unknown.StatusCode);
        var error = HttpUtility.ParseQueryString(unknown.Headers.Location!.Query);
        Assert.Equal("invalid_request", error["error"]);
        Assert.Equal("s", error["state"]);
    }

    [Fact]
    public async Task Redirect_uri_perigoso_e_recusado()
    {
        using var http = fx.CreateHttpClient(followRedirects: false);
        foreach (var redirect in new[] { "javascript:alert(1)", "data:text/html,oi", "vbscript:msgbox(1)" })
        {
            using var response = await http.GetAsync($"{fx.HttpBase}/oauth2/authorize?client_id=x&response_type=token&response_mode=form_post&redirect_uri={Uri.EscapeDataString(redirect)}", _ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain(redirect, await response.Content.ReadAsStringAsync(_ct), StringComparison.Ordinal);
        }

        using (var customFormPost = await http.GetAsync($"{fx.HttpBase}/oauth2/authorize?client_id=x&response_type=code&response_mode=form_post&redirect_uri={Uri.EscapeDataString("meuapp://retorno")}", _ct))
            Assert.Equal(HttpStatusCode.BadRequest, customFormPost.StatusCode);

        using var customQuery = await http.GetAsync($"{fx.HttpBase}/oauth2/authorize?client_id=x&response_type=code&redirect_uri={Uri.EscapeDataString("meuapp://retorno")}", _ct);
        Assert.Equal(HttpStatusCode.OK, customQuery.StatusCode);
        Assert.Equal("frame-ancestors 'none'", customQuery.Headers.GetValues("Content-Security-Policy").Single());
    }

    private async Task<string> AuthorizeAsync(HttpClient http, string query)
    {
        var form = HttpUtility.ParseQueryString(query);
        var fields = form.AllKeys.ToDictionary(k => k!, k => form[k]!);
        fields["fake_user"] = "maria@teste.local";
        fields["fake_decision"] = "allow";
        using var content = new FormUrlEncodedContent(fields);
        using var post = await http.PostAsync($"{fx.HttpBase}/common/oauth2/v2.0/authorize", content, _ct);
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        var location = post.Headers.Location!;
        Assert.StartsWith(Redirect, location.ToString(), StringComparison.Ordinal);
        var callback = HttpUtility.ParseQueryString(location.Query);
        Assert.Equal(form["state"], callback["state"]);
        return callback["code"]!;
    }

    [Fact]
    public async Task Fluxo_device_code()
    {
        using var http = fx.CreateHttpClient();
        var device = await JsonAsync(http.PostAsync($"{fx.HttpBase}/oauth2/devicecode",
            Form(("client_id", "cli-app"), ("scope", "https://outlook.office.com/SMTP.Send offline_access")), _ct));
        var deviceCode = device.GetProperty("device_code").GetString()!;
        var userCode = device.GetProperty("user_code").GetString()!;

        FormUrlEncodedContent Poll() => Form(("grant_type", "urn:ietf:params:oauth:grant-type:device_code"), ("device_code", deviceCode), ("client_id", "cli-app"));
        var pending = await JsonAsync(http.PostAsync($"{fx.HttpBase}/oauth2/token", Poll(), _ct));
        Assert.Equal("authorization_pending", pending.GetProperty("error").GetString());

        using (var approve = await http.PostAsync($"{fx.HttpBase}/device",
            Form(("user_code", userCode.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant()), ("fake_user", "cli@teste.local"), ("fake_decision", "allow")), _ct))
            Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var granted = await JsonAsync(http.PostAsync($"{fx.HttpBase}/oauth2/token", Poll(), _ct));
        var token = granted.GetProperty("access_token").GetString()!;
        await fx.SendAsync(ServerFixture.Simple("device-" + Guid.NewGuid(), from: "cli@teste.local"), _ct, c => c.AuthenticateAsync(new SaslMechanismOAuth2("cli@teste.local", token), _ct));

        var again = await JsonAsync(http.PostAsync($"{fx.HttpBase}/oauth2/token", Poll(), _ct));
        Assert.Equal("invalid_grant", again.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Discovery_jwks_userinfo_e_password_grant()
    {
        using var http = fx.CreateHttpClient();
        var discovery = await JsonAsync(http.GetAsync($"{fx.HttpsBase}/.well-known/openid-configuration", _ct));
        Assert.Equal(fx.HttpsBase, discovery.GetProperty("issuer").GetString());
        Assert.Equal($"{fx.HttpsBase}/oauth2/token", discovery.GetProperty("token_endpoint").GetString());

        var ms = await JsonAsync(http.GetAsync($"{fx.HttpsBase}/meu-tenant/v2.0/.well-known/openid-configuration", _ct));
        Assert.Equal($"{fx.HttpsBase}/meu-tenant/v2.0", ms.GetProperty("issuer").GetString());
        Assert.Equal($"{fx.HttpsBase}/meu-tenant/oauth2/v2.0/token", ms.GetProperty("token_endpoint").GetString());

        var jwks = await JsonAsync(http.GetAsync(ms.GetProperty("jwks_uri").GetString(), _ct));
        Assert.Equal("RSA", jwks.GetProperty("keys")[0].GetProperty("kty").GetString());

        var password = await JsonAsync(http.PostAsync($"{fx.HttpsBase}/oauth2/token",
            Form(("grant_type", "password"), ("username", "senha@teste.local"), ("password", "x"), ("client_id", "legado"), ("scope", "openid")), _ct));
        var access = password.GetProperty("access_token").GetString()!;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{fx.HttpsBase}/oauth2/userinfo");
        request.Headers.Authorization = new("Bearer", access);
        var userinfo = await JsonAsync(http.SendAsync(request, _ct));
        Assert.Equal("senha@teste.local", userinfo.GetProperty("email").GetString());
    }

    private sealed class ServerTrustingHttpFactory(ServerFixture fx) : IMsalHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = fx.CreateHttpClient();

        public HttpClient GetHttpClient() => _client;

        public void Dispose() => _client.Dispose();
    }

    [Fact]
    public async Task Msal_obtem_token_pelo_authority_estilo_microsoft()
    {
        using var factory = new ServerTrustingHttpFactory(fx);
        var app = ConfidentialClientApplicationBuilder.Create("minha-app")
            .WithClientSecret("segredo")
            .WithOidcAuthority($"https://localhost:{fx.Https}/fake-tenant/v2.0")
            .WithHttpClientFactory(factory)
            .Build();
        var result = await app.AcquireTokenForClient(["https://outlook.office.com/.default"]).ExecuteAsync(_ct);
        Assert.False(string.IsNullOrEmpty(result.AccessToken));

        await fx.SendAsync(ServerFixture.Simple("msal-" + Guid.NewGuid()), _ct, c => c.AuthenticateAsync(new SaslMechanismOAuth2("app@teste.local", result.AccessToken), _ct));
    }
}
