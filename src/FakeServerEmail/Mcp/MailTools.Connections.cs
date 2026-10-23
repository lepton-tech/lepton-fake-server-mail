using System.ComponentModel;
using System.Globalization;
using System.Text;
using FakeServerEmail.Web;
using ModelContextProtocol.Server;

namespace FakeServerEmail.Mcp;

public sealed partial class MailTools
{
    private const int DefaultLogLines = 400;

    [McpServerTool(Name = "list_connections", Title = "Listar conexões", ReadOnly = true, Idempotent = true)]
    [Description("Lista as conexões SMTP, POP3 e IMAP recentes, com cliente, usuário, mecanismo de autenticação, TLS, mensagens recebidas, falhas e as últimas linhas do diálogo. Use para descobrir por que o sistema testado não conseguiu enviar ou autenticar.")]
    public async Task<object> ListConnections(
        [Description("Filtrar por protocolo: SMTP, POP3 ou IMAP.")] string? protocol = null,
        [Description("Somente conexões com falha (erro de autenticação, comando recusado etc.).")] bool onlyFailures = false,
        [Description("Máximo de itens (padrão 20).")] int limit = DefaultLimit,
        CancellationToken ct = default)
    {
        var sessions = await api.GetAsync<List<SessionInfo>>("/api/sessions", ct);
        return sessions
            .Where(s => protocol is null || s.Protocol.Equals(protocol, StringComparison.OrdinalIgnoreCase))
            .Where(s => !onlyFailures || s.Failures > 0 || s.Error is not null)
            .Take(ClampLimit(limit))
            .ToList();
    }

    [McpServerTool(Name = "get_connection_log", Title = "Diálogo da conexão", ReadOnly = true, Idempotent = true)]
    [Description("Mostra o diálogo completo de uma conexão (C: cliente, S: servidor, *: evento), para depurar SMTP, POP3, IMAP e autenticação.")]
    public async Task<string> GetConnectionLog(
        [Description("Id da conexão (de list_connections).")] int id,
        [Description("Máximo de linhas (padrão 400).")] int maxLines = DefaultLogLines,
        CancellationToken ct = default)
    {
        var detail = await api.GetAsync<SessionDetail>($"/api/sessions/{id}", ct, "Conexão não encontrada (use list_connections para ver os ids).");
        var s = detail.Session;
        var culture = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(culture, $"{s.Protocol} porta {s.Port} · cliente {s.Client} · {(s.Secure ? "TLS" : "sem TLS")} · usuário {s.User ?? "(nenhum)"} · início {s.StartedAt:yyyy-MM-dd HH:mm:ss}");
        var lines = detail.Lines;
        if (lines.Count > maxLines)
            sb.AppendLine(culture, $"(mostrando as últimas {maxLines} de {lines.Count} linhas)");
        foreach (var line in lines.TakeLast(Math.Max(1, maxLines)))
            sb.AppendLine(culture, $"{line.Time:HH:mm:ss.fff} {line.Direction}: {line.Text}");
        return sb.ToString();
    }
}
