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
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static string B64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Token_client_credentials_autentica_SMTP_IMAP_e_POP3()
    {
        var token = await fx.ClientCredentialsTokenAsync();
        var subject = "oauth-smtp-" + Guid.NewGuid();
        await fx.SendAsync(ServerFixture.Simple(subject), c => c.AuthenticateAsync(new SaslMechanismOAuth2("app@teste.local", token), _ct));
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

    [Fact]
    public async Task Tokens_invalidos_expirados_revogados_ou_de_outro_usuario_sao_recusados()
    {
        async Task AssertRejectedAsync(string user, string token)
        {
            using var smtp = new SmtpClient();
            await smtp.ConnectAsync("127.0.0.1", fx.Smtp, SecureSocketOptions.None, _ct);
            await Assert.ThrowsAsync<AuthenticationException>(() => smtp.AuthenticateAsync(new SaslMechanismOAuth2(user, token), _ct));
            using var imap = new ImapClient();
            await imap.ConnectAsync("127.0.0.1", fx.Imap, SecureSocketOptions.None, _ct);
            await Assert.ThrowsAsync<AuthenticationException>(() => imap.AuthenticateAsync(new SaslMechanismOAuthBearer(user, token), _ct));
        }

        await AssertRejectedAsync("a@teste.local", "token-inventado");

        var other = await fx.ManualTokenAsync("dono@teste.local");
        await AssertRejectedAsync("intruso@teste.local", other);

        var shortLived = await fx.ManualTokenAsync("curto@teste.local", 1);
        await Task.Delay(2100, _ct);
        await AssertRejectedAsync("curto@teste.local", shortLived);

        var revoked = await fx.ManualTokenAsync("revogado@teste.local");
        var jti = fx.Server.Context.Tokens.List().First(t => t.AccessToken == revoked).Jti;
        using (var http = fx.CreateHttpClient())
            (await http.DeleteAsync($"{fx.HttpBase}/api/oauth/tokens/{jti}", _ct)).EnsureSuccessStatusCode();
        await AssertRejectedAsync("revogado@teste.local", revoked);

        var valid = await fx.ManualTokenAsync("dono@teste.local");
        using var ok = new SmtpClient();
        await ok.ConnectAsync("127.0.0.1", fx.Smtp, SecureSocketOptions.None, _ct);
        await ok.AuthenticateAsync(new SaslMechanismOAuth2("dono@teste.local", valid), _ct);
        Assert.True(ok.IsAuthenticated);
    }

    [Fact]
    public async Task Fluxo_authorization_code_com_PKCE_e_refresh()
    {
        using var http = fx.CreateHttpClient(followRedirects: false);
        var verifier = B64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        const string redirect = "http://localhost:9999/callback";
        var query = $"client_id=web-app&response_type=code&redirect_uri={Uri.EscapeDataString(redirect)}&scope={Uri.EscapeDataString("openid email offline_access https://outlook.office.com/IMAP.AccessAsUser.All")}&state=xyz&code_challenge={challenge}&code_challenge_method=S256&nonce=n1&login_hint=maria@teste.local";

        var page = await http.GetStringAsync($"{fx.HttpBase}/common/oauth2/v2.0/authorize?{query}", _ct);
        Assert.Contains("maria@teste.local", page);

        var form = HttpUtility.ParseQueryString(query);
        var fields = form.AllKeys.ToDictionary(k => k!, k => form[k]!);
        fields["fake_user"] = "maria@teste.local";
        fields["fake_decision"] = "allow";
        var post = await http.PostAsync($"{fx.HttpBase}/common/oauth2/v2.0/authorize", new FormUrlEncodedContent(fields), _ct);
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        var location = post.Headers.Location!;
        Assert.StartsWith(redirect, location.ToString());
        var callback = HttpUtility.ParseQueryString(location.Query);
        Assert.Equal("xyz", callback["state"]);
        var code = callback["code"]!;

        var wrong = await http.PostAsync($"{fx.HttpBase}/common/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["client_id"] = "web-app", ["code_verifier"] = "errado",
        }), _ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var post2 = await http.PostAsync($"{fx.HttpBase}/common/oauth2/v2.0/authorize", new FormUrlEncodedContent(fields), _ct);
        code = HttpUtility.ParseQueryString(post2.Headers.Location!.Query)["code"]!;
        var tokenResponse = await http.PostAsync($"{fx.HttpBase}/common/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["client_id"] = "web-app", ["code_verifier"] = verifier,
        }), _ct);
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var json = await JsonAsync(tokenResponse);
        var access = json.GetProperty("access_token").GetString()!;
        var refresh = json.GetProperty("refresh_token").GetString()!;
        var idToken = json.GetProperty("id_token").GetString()!;
        var idPayload = JsonDocument.Parse(Convert.FromBase64String(Pad(idToken.Split('.')[1]))).RootElement;
        Assert.Equal("maria@teste.local", idPayload.GetProperty("email").GetString());
        Assert.Equal("n1", idPayload.GetProperty("nonce").GetString());
        Assert.Equal($"{fx.HttpBase}/common/v2.0", idPayload.GetProperty("iss").GetString());

        using (var imap = new ImapClient())
        {
            await imap.ConnectAsync("127.0.0.1", fx.Imap, SecureSocketOptions.None, _ct);
            await imap.AuthenticateAsync(new SaslMechanismOAuth2("maria@teste.local", access), _ct);
            Assert.True(imap.IsAuthenticated);
        }

        var refreshed = await http.PostAsync($"{fx.HttpBase}/common/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = "web-app",
        }), _ct);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.NotEqual(access, (await JsonAsync(refreshed)).GetProperty("access_token").GetString());

        var reuse = await http.PostAsync($"{fx.HttpBase}/common/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["client_id"] = "web-app", ["code_verifier"] = verifier,
        }), _ct);
        Assert.Equal("invalid_grant", (await JsonAsync(reuse)).GetProperty("error").GetString());
    }

    private static string Pad(string b64url)
    {
        var s = b64url.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }

    [Fact]
    public async Task Fluxo_device_code()
    {
        using var http = fx.CreateHttpClient();
        var device = await JsonAsync(await http.PostAsync($"{fx.HttpBase}/oauth2/devicecode", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "cli-app", ["scope"] = "https://outlook.office.com/SMTP.Send offline_access",
        }), _ct));
        var deviceCode = device.GetProperty("device_code").GetString()!;
        var userCode = device.GetProperty("user_code").GetString()!;

        var poll = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["device_code"] = deviceCode, ["client_id"] = "cli-app",
        });
        var pending = await JsonAsync(await http.PostAsync($"{fx.HttpBase}/oauth2/token", poll, _ct));
        Assert.Equal("authorization_pending", pending.GetProperty("error").GetString());

        var approve = await http.PostAsync($"{fx.HttpBase}/device", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["user_code"] = userCode.Replace("-", "").ToLowerInvariant(), ["fake_user"] = "cli@teste.local", ["fake_decision"] = "allow",
        }), _ct);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var granted = await JsonAsync(await http.PostAsync($"{fx.HttpBase}/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["device_code"] = deviceCode, ["client_id"] = "cli-app",
        }), _ct));
        var token = granted.GetProperty("access_token").GetString()!;
        await fx.SendAsync(ServerFixture.Simple("device-" + Guid.NewGuid(), from: "cli@teste.local"), c => c.AuthenticateAsync(new SaslMechanismOAuth2("cli@teste.local", token), _ct));
    }

    [Fact]
    public async Task Discovery_jwks_userinfo_e_password_grant()
    {
        using var http = fx.CreateHttpClient();
        var discovery = await JsonAsync(await http.GetAsync($"{fx.HttpsBase}/.well-known/openid-configuration", _ct));
        Assert.Equal(fx.HttpsBase, discovery.GetProperty("issuer").GetString());
        Assert.Equal($"{fx.HttpsBase}/oauth2/token", discovery.GetProperty("token_endpoint").GetString());

        var ms = await JsonAsync(await http.GetAsync($"{fx.HttpsBase}/meu-tenant/v2.0/.well-known/openid-configuration", _ct));
        Assert.Equal($"{fx.HttpsBase}/meu-tenant/v2.0", ms.GetProperty("issuer").GetString());
        Assert.Equal($"{fx.HttpsBase}/meu-tenant/oauth2/v2.0/token", ms.GetProperty("token_endpoint").GetString());

        var jwks = await JsonAsync(await http.GetAsync(ms.GetProperty("jwks_uri").GetString(), _ct));
        Assert.Equal("RSA", jwks.GetProperty("keys")[0].GetProperty("kty").GetString());

        var password = await JsonAsync(await http.PostAsync($"{fx.HttpsBase}/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password", ["username"] = "senha@teste.local", ["password"] = "x", ["client_id"] = "legado", ["scope"] = "openid",
        }), _ct));
        var access = password.GetProperty("access_token").GetString()!;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{fx.HttpsBase}/oauth2/userinfo");
        request.Headers.Authorization = new("Bearer", access);
        var userinfo = await JsonAsync(await http.SendAsync(request, _ct));
        Assert.Equal("senha@teste.local", userinfo.GetProperty("email").GetString());
    }

    private sealed class InsecureHttpFactory : IMsalHttpClientFactory
    {
        private readonly HttpClient _client = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator });

        public HttpClient GetHttpClient() => _client;
    }

    [Fact]
    public async Task Msal_obtem_token_pelo_authority_estilo_microsoft()
    {
        var app = ConfidentialClientApplicationBuilder.Create("minha-app")
            .WithClientSecret("segredo")
            .WithOidcAuthority($"https://localhost:{fx.Https}/fake-tenant/v2.0")
            .WithHttpClientFactory(new InsecureHttpFactory())
            .Build();
        var result = await app.AcquireTokenForClient(["https://outlook.office.com/.default"]).ExecuteAsync(_ct);
        Assert.False(string.IsNullOrEmpty(result.AccessToken));

        await fx.SendAsync(ServerFixture.Simple("msal-" + Guid.NewGuid()), c => c.AuthenticateAsync(new SaslMechanismOAuth2("app@teste.local", result.AccessToken), _ct));
    }
}
