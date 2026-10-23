using System.Net;
using System.Net.Sockets;
using FakeServerEmail.Core;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace FakeServerEmail.Web;

public sealed class LocalSendException(string message, Exception? inner = null) : Exception(message, inner);

public static class LocalSmtpSender
{
    private const string AnyPassword = "senha";
    private static readonly char[] AddressSeparators = [',', ';'];

    public static void AddAddresses(InternetAddressList list, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        foreach (var address in value.Split(AddressSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            list.Add(MailboxAddress.Parse(address));
    }

    public static async Task SendAsync(int port, AppSettings settings, MimeMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback.ToString(), port, SecureSocketOptions.None, ct);
            await AuthenticateIfRequiredAsync(client, settings, message.From.Mailboxes.First().Address, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex) when (ex is SmtpCommandException or SmtpProtocolException or ServiceNotAuthenticatedException or IOException or SocketException)
        {
            throw new LocalSendException("O SMTP local recusou o envio: " + ex.Message, ex);
        }
    }

    private static async Task AuthenticateIfRequiredAsync(SmtpClient client, AppSettings settings, string sender, CancellationToken ct)
    {
        if (!settings.SmtpRequireAuth)
            return;
        var login = settings.AcceptAnyPassword ? new UserAccount { Username = sender, Password = AnyPassword } : settings.Users.FirstOrDefault()
            ?? throw new LocalSendException("O SMTP exige login, mas não há nenhum usuário cadastrado em Configurações → Usuário e senha.");
        try
        {
            await client.AuthenticateAsync(login.Username, login.Password, ct);
        }
        catch (Exception ex) when (ex is NotSupportedException or AuthenticationException)
        {
            throw new LocalSendException($"O SMTP exige login e não foi possível entrar como '{login.Username}': {ex.Message}", ex);
        }
    }
}
