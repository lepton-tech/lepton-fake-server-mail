using FakeServerEmail.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace FakeServerEmail.Web;

public static partial class ApiEndpoints
{
    private const int SummaryLineCount = 3;

    private static void MapSessions(RouteGroupBuilder api)
    {
        var sessions = api.MapGroup("/sessions");
        sessions.MapGet("", (SessionLog log) => TypedResults.Ok(log.List().Select(s => Summarize(s, s.Lines)).ToList()));

        sessions.MapGet("/{id:int}", Results<Ok<SessionDetail>, NotFound> (int id, SessionLog log) =>
        {
            if (log.Get(id) is not { } session)
                return TypedResults.NotFound();
            var lines = session.Lines;
            return TypedResults.Ok(new SessionDetail(Summarize(session, lines), lines));
        });

        sessions.MapDelete("", (SessionLog log) =>
        {
            log.Clear();
            return TypedResults.NoContent();
        });
    }

    private static SessionInfo Summarize(ProtocolSession s, IReadOnlyList<LogLine> lines) => new(
        s.Id, s.Protocol, s.Port, s.Client, s.Secure, s.StartedAt, s.EndedAt, s.User, s.AuthMechanism, s.Error, s.MessagesReceived,
        lines.Count,
        lines.Count(IsFailure),
        lines.TakeLast(SummaryLineCount).Select(l => $"{l.Direction}: {l.Text}").ToList());

    private static bool IsFailure(LogLine line)
    {
        if (line.Direction == '*')
            return line.Text.StartsWith("Falha", StringComparison.Ordinal) || line.Text.StartsWith("Erro", StringComparison.Ordinal);
        if (line.Direction != 'S' || line.Text.Length < 3)
            return false;
        if (line.Text[0] is '4' or '5' && char.IsAsciiDigit(line.Text[1]) && char.IsAsciiDigit(line.Text[2]))
            return true;
        if (line.Text.StartsWith("-ERR", StringComparison.Ordinal))
            return true;
        var parts = line.Text.Split(' ', 3);
        return parts.Length >= 2 && parts[0] != "*" && parts[1] is "NO" or "BAD";
    }
}
