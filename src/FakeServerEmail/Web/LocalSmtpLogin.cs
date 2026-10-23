using FakeServerEmail.Core;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace FakeServerEmail.Web;

public static class LocalSmtpLogin
{
    public static async Task AuthenticateIfRequiredAsync(SmtpClient client, AppSettings settings, string sender, CancellationToken ct)
    {
        if (!settings.SmtpRequireAuth)
            return;
        var login = settings.AcceptAnyPassword ? new UserAccount { Username = sender, Password = "senha" } : settings.Users.FirstOrDefault()
            ?? throw new InvalidOperationException("O SMTP exige login, mas não há nenhum usuário cadastrado em Configurações → Usuário e senha.");
        try
        {
            await client.AuthenticateAsync(login.Username, login.Password, ct);
        }
        catch (Exception ex) when (ex is NotSupportedException or AuthenticationException)
        {
            throw new InvalidOperationException($"O SMTP exige login e não foi possível entrar como '{login.Username}': {ex.Message}", ex);
        }
    }
}
