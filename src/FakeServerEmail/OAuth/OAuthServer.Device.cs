using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace FakeServerEmail.OAuth;

public sealed partial class OAuthServer
{
    private const string UserCodeAlphabet = "BCDFGHJKLMNPQRSTVWXZ";
    private const int UserCodeLength = 8;
    private const int DevicePollIntervalSeconds = 2;
    private static readonly TimeSpan DeviceCodeLifetime = TimeSpan.FromMinutes(15);

    private enum DeviceStatus { Pending, Approved, Denied }

    private sealed record DeviceAuthorization(string DeviceCode, string UserCode, string ClientId, string Scope, string Issuer, DateTimeOffset ExpiresAt, DeviceStatus Status = DeviceStatus.Pending, string? User = null);

    private async Task DeviceCodeAsync(HttpContext http)
    {
        var form = await ReadFormAsync(http);
        var chars = RandomNumberGenerator.GetItems<char>(UserCodeAlphabet, UserCodeLength);
        var userCode = new string(chars, 0, UserCodeLength / 2) + "-" + new string(chars, UserCodeLength / 2, UserCodeLength / 2);
        var device = new DeviceAuthorization(
            "fdc_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24)),
            userCode,
            form.Optional("client_id") ?? UnknownClientId,
            form["scope"],
            Issuer(http),
            DateTimeOffset.UtcNow.Add(DeviceCodeLifetime));
        _devices[device.DeviceCode] = device;

        var verification = $"{BaseUrl(http)}/device";
        log.Report($"OAuth  código de dispositivo {userCode} gerado para o cliente {device.ClientId}");
        await WriteJsonAsync(http, StatusCodes.Status200OK, new JsonObject
        {
            ["device_code"] = device.DeviceCode,
            ["user_code"] = userCode,
            ["verification_uri"] = verification,
            ["verification_url"] = verification,
            ["verification_uri_complete"] = $"{verification}?user_code={userCode}",
            ["expires_in"] = (int)DeviceCodeLifetime.TotalSeconds,
            ["interval"] = DevicePollIntervalSeconds,
            ["message"] = $"Para entrar, abra {verification} e digite o código {userCode}.",
        });
    }

    private Task DeviceCodeGrantAsync(HttpContext http, FormValues form)
    {
        if (!_devices.TryGetValue(form["device_code"], out var device))
            return WriteErrorAsync(http, StatusCodes.Status400BadRequest, "invalid_grant", "device_code inválido");
        if (device.ExpiresAt < DateTimeOffset.UtcNow)
        {
            _devices.TryRemove(device.DeviceCode, out _);
            return WriteErrorAsync(http, StatusCodes.Status400BadRequest, "expired_token", "O código de dispositivo expirou");
        }
        if (device.Status == DeviceStatus.Pending)
            return WriteErrorAsync(http, StatusCodes.Status400BadRequest, "authorization_pending", "Aguardando o usuário autorizar no navegador");
        if (!_devices.TryRemove(KeyValuePair.Create(device.DeviceCode, device)))
            return WriteErrorAsync(http, StatusCodes.Status400BadRequest, "invalid_grant", "Este código de dispositivo já foi usado");
        if (device.Status == DeviceStatus.Denied)
            return WriteErrorAsync(http, StatusCodes.Status400BadRequest, "access_denied", "O usuário negou o acesso");
        return IssueAsync(http, new TokenGrant(device.Issuer, device.ClientId, device.User, device.Scope, "device_code", true));
    }

    private async Task DevicePageAsync(HttpContext http, bool isPost)
    {
        if (!isPost)
        {
            var body = $"""
                <p class="lead">Digite o código exibido pelo aplicativo.</p>
                <form method="post">
                  <label>Código</label>
                  <input name="user_code" value="{OAuthPages.E(http.Request.Query["user_code"].ToString())}" placeholder="ABCD-EFGH" required autofocus style="text-transform:uppercase;letter-spacing:.15em">
                  <label>E-mail do usuário simulado</label>
                  <input type="email" name="fake_user" value="{OAuthPages.E(_lastUser)}" required>
                  <div class="actions">
                    <button type="submit" name="fake_decision" value="deny" class="secondary">Negar</button>
                    <button type="submit" name="fake_decision" value="allow">Autorizar</button>
                  </div>
                </form>
                """;
            await WriteHtmlAsync(http, StatusCodes.Status200OK, OAuthPages.Page("Entrar em um dispositivo", body));
            return;
        }

        var form = await ReadFormAsync(http);
        var userCode = form["user_code"].Trim().ToUpperInvariant();
        if (userCode.Length == UserCodeLength)
            userCode = userCode[..(UserCodeLength / 2)] + "-" + userCode[(UserCodeLength / 2)..];
        var device = _devices.Values.FirstOrDefault(d => d.UserCode == userCode && d.Status == DeviceStatus.Pending && d.ExpiresAt > DateTimeOffset.UtcNow);
        if (device is null)
        {
            await WriteHtmlAsync(http, StatusCodes.Status400BadRequest, OAuthPages.Page("Código inválido", "<p>Código não encontrado ou expirado.</p><p><a href=\"/device\">Tentar novamente</a></p>"));
            return;
        }

        if (form["fake_decision"] == "allow")
        {
            var user = form.Optional("fake_user")?.Trim() is { Length: > 0 } typed ? typed : _lastUser;
            _lastUser = user;
            _devices.TryUpdate(device.DeviceCode, device with { Status = DeviceStatus.Approved, User = user }, device);
            await WriteHtmlAsync(http, StatusCodes.Status200OK, OAuthPages.Page("Pronto", $"<p class=\"lead\">Acesso autorizado para <b>{OAuthPages.E(user)}</b>.</p><p>Pode voltar ao aplicativo.</p>"));
        }
        else
        {
            _devices.TryUpdate(device.DeviceCode, device with { Status = DeviceStatus.Denied }, device);
            await WriteHtmlAsync(http, StatusCodes.Status200OK, OAuthPages.Page("Acesso negado", "<p>O aplicativo receberá <code>access_denied</code>.</p>"));
        }
    }
}
