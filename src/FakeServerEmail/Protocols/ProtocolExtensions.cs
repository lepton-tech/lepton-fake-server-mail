using FakeServerEmail.Core;

namespace FakeServerEmail.Protocols;

internal static class ProtocolExtensions
{
    public static void MarkAuthenticated(this ProtocolSession log, string user, string mechanism, string? detail = null)
    {
        log.User = user;
        log.AuthMechanism = mechanism;
        var text = $"Autenticado como '{user}' via {mechanism}";
        log.Info(detail is null ? text : $"{text}; {detail}");
    }

    public static long MaxMessageBytes(this AppSettings settings) => settings.MaxMessageSizeMb * 1024L * 1024L;
}
