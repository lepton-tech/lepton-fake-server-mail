using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using FakeServerEmail.Core;

namespace FakeServerEmail.Protocols.Pop3;

public sealed class Pop3Handler(ServerContext context, bool allowStartTls) : IProtocolHandler
{
    public string Protocol => "POP3";

    public Task HandleAsync(MailConnection connection, CancellationToken ct) => new Pop3Session(context, connection, allowStartTls).RunAsync(ct);
}

internal sealed class Pop3Session(ServerContext context, MailConnection connection, bool allowStartTls)
{
    private const int CommandLineLimit = 8192;

    private readonly ProtocolSession _log = connection.Log!;
    private readonly HashSet<int> _deleted = [];
    private List<StoredMessage> _maildrop = [];
    private string? _pendingUser;
    private string? _user;

    public async Task RunAsync(CancellationToken ct)
    {
        await connection.SendLineAsync("+OK Fake Server Email POP3 pronto", ct);

        while (true)
        {
            string? line;
            try
            {
                line = await connection.ReadLineAsync(ct, CommandLineLimit);
            }
            catch (LineTooLongException)
            {
                await connection.SendLineAsync("-ERR Linha muito longa", ct);
                return;
            }
            if (line is null)
                return;

            var parts = line.Split(' ', 2);
            var verb = parts[0].ToUpperInvariant();
            var arg = parts.Length > 1 ? parts[1].Trim() : "";
            _log.FromClient(verb switch
            {
                "PASS" => "PASS ********",
                "AUTH" when arg.Length > 0 => $"{parts[0]} {Sasl.MaskArguments(arg)}",
                _ => line,
            });

            var keepGoing = _user is null ? await AuthorizationAsync(verb, arg, ct) : await TransactionAsync(verb, arg, ct);
            if (!keepGoing)
                return;
            await connection.FlushIfIdleAsync(ct);
        }
    }

    private async Task<bool> AuthorizationAsync(string verb, string arg, CancellationToken ct)
    {
        switch (verb)
        {
            case "CAPA":
                WriteCapabilities();
                break;
            case "STLS":
                await StlsAsync(ct);
                break;
            case "USER":
                User(arg);
                break;
            case "PASS":
                Pass(arg);
                break;
            case "AUTH":
                await AuthAsync(arg, ct);
                break;
            case "QUIT":
                await connection.SendLineAsync("+OK Até logo", ct);
                return false;
            case "NOOP":
                connection.WriteLine("+OK");
                break;
            default:
                connection.WriteLine("-ERR Comando inválido ou não autenticado");
                break;
        }
        return true;
    }

    private async Task<bool> TransactionAsync(string verb, string arg, CancellationToken ct)
    {
        switch (verb)
        {
            case "CAPA":
                WriteCapabilities();
                break;
            case "STAT":
                Stat();
                break;
            case "LIST":
            case "UIDL":
                List(verb, arg);
                break;
            case "RETR":
            case "TOP":
                await RetrieveAsync(verb, arg, ct);
                break;
            case "DELE":
                Dele(arg);
                break;
            case "RSET":
                _deleted.Clear();
                connection.WriteLine($"+OK {_maildrop.Count} mensagens");
                break;
            case "NOOP":
                connection.WriteLine("+OK");
                break;
            case "QUIT":
                await QuitAsync(ct);
                return false;
            default:
                connection.WriteLine("-ERR Comando não reconhecido");
                break;
        }
        return true;
    }

    private async Task StlsAsync(CancellationToken ct)
    {
        if (!connection.CanStartTls(allowStartTls, context.Settings.Current))
        {
            connection.WriteLine("-ERR STLS não disponível nesta conexão");
            return;
        }
        await connection.SendLineAsync("+OK Iniciando TLS", ct);
        await connection.UpgradeToTlsAsync(ct);
        _pendingUser = null;
    }

    private void User(string arg)
    {
        if (arg.Length == 0)
        {
            connection.WriteLine("-ERR Informe o usuário");
            return;
        }
        _pendingUser = arg;
        connection.WriteLine("+OK Usuário aceito, envie PASS");
    }

    private void Pass(string arg)
    {
        if (_pendingUser is null)
        {
            connection.WriteLine("-ERR Envie USER primeiro");
            return;
        }
        var result = context.Authenticator.ValidatePassword(_pendingUser, arg);
        if (!result.Success)
        {
            _log.Info("Falha na autenticação: " + result.Error);
            connection.WriteLine("-ERR [AUTH] " + result.Error);
            _pendingUser = null;
            return;
        }
        OpenMaildrop(result.User ?? _pendingUser, "USER/PASS");
    }

    private async Task AuthAsync(string arg, CancellationToken ct)
    {
        var settings = context.Settings.Current;
        if (arg.Length == 0)
        {
            connection.WriteLine("+OK Mecanismos suportados");
            foreach (var mechanism in settings.AuthMechanisms)
                connection.WriteLine(mechanism);
            connection.WriteLine(".");
            return;
        }

        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (!Sasl.IsOffered(settings, parts[0]))
        {
            connection.WriteLine("-ERR Mecanismo de autenticação não suportado");
            return;
        }

        var outcome = await Sasl.ExchangeAsync(connection, context, parts[0], parts.Length > 1 ? parts[1] : null, "+ ", ct);
        if (outcome.Success)
            OpenMaildrop(outcome.User ?? "", outcome.Mechanism);
        else if (outcome.Cancelled)
            connection.WriteLine("-ERR Autenticação cancelada");
        else
            connection.WriteLine("-ERR [AUTH] Falha na autenticação: " + outcome.Error);
    }

    private void OpenMaildrop(string user, string mechanism)
    {
        _user = user;
        var separate = context.Settings.Current.SeparateMailboxesByRecipient;
        _maildrop = context.Store.List(MessageStore.Inbox).Where(m => MessageStore.IsVisibleTo(m, user, separate)).ToList();
        _log.MarkAuthenticated(user, mechanism, $"{_maildrop.Count} mensagens na caixa");
        connection.WriteLine($"+OK Caixa de '{user}' aberta com {_maildrop.Count} mensagens");
    }

    private void WriteCapabilities()
    {
        connection.WriteLine("+OK Lista de capacidades");
        connection.WriteLine("TOP");
        connection.WriteLine("UIDL");
        connection.WriteLine("RESP-CODES");
        connection.WriteLine("AUTH-RESP-CODE");
        connection.WriteLine("PIPELINING");
        if (_user is null)
        {
            var settings = context.Settings.Current;
            connection.WriteLine("USER");
            if (settings.AuthMechanisms.Count > 0)
                connection.WriteLine("SASL " + string.Join(' ', settings.AuthMechanisms));
            if (connection.CanStartTls(allowStartTls, settings))
                connection.WriteLine("STLS");
        }
        connection.WriteLine("IMPLEMENTATION Fake-Server-Email");
        connection.WriteLine(".");
    }

    private void Stat()
    {
        var live = Live().ToList();
        connection.WriteLine($"+OK {live.Count} {live.Sum(x => x.Message.Size)}");
    }

    private void List(string verb, string arg)
    {
        if (arg.Length > 0)
        {
            if (!TryGet(arg, out var number, out var single))
            {
                connection.WriteLine("-ERR Mensagem inexistente");
                return;
            }
            connection.WriteLine($"+OK {number} {(verb == "LIST" ? single.Size : single.Id)}");
            return;
        }
        connection.WriteLine("+OK Lista de mensagens");
        foreach (var (number, message) in Live())
            connection.WriteLine($"{number} {(verb == "LIST" ? message.Size : message.Id)}");
        connection.WriteLine(".");
    }

    private async Task RetrieveAsync(string verb, string arg, CancellationToken ct)
    {
        var args = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length == 0 || !TryGet(args[0], out _, out var target))
        {
            connection.WriteLine("-ERR Mensagem inexistente");
            return;
        }
        var lines = -1;
        if (verb == "TOP" && (args.Length < 2 || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out lines)))
        {
            connection.WriteLine("-ERR Sintaxe: TOP <mensagem> <linhas>");
            return;
        }
        var raw = context.Store.GetRaw(target.Id);
        if (raw is null)
        {
            connection.WriteLine("-ERR Mensagem não existe mais no servidor");
            return;
        }
        connection.WriteLine($"+OK {raw.Length} octetos");
        await WriteDotStuffedAsync(raw, lines, ct);
        _log.FromServer($"[conteúdo da mensagem: {raw.Length} bytes]");
        connection.WriteLine(".");
        if (verb == "RETR")
            context.Store.SetSeen([target.Id], true);
    }

    private void Dele(string arg)
    {
        if (!TryGet(arg, out var number, out _))
        {
            connection.WriteLine("-ERR Mensagem inexistente");
            return;
        }
        _deleted.Add(number);
        connection.WriteLine($"+OK Mensagem {number} marcada para exclusão");
    }

    private async Task QuitAsync(CancellationToken ct)
    {
        var toDelete = _deleted.Select(n => _maildrop[n - 1].Id).ToList();
        if (toDelete.Count > 0)
        {
            context.Store.Delete(toDelete);
            _log.Info($"{toDelete.Count} mensagens excluídas");
        }
        await connection.SendLineAsync($"+OK Até logo ({toDelete.Count} mensagens excluídas)", ct);
    }

    private IEnumerable<(int Number, StoredMessage Message)> Live() =>
        _maildrop.Select((m, i) => (i + 1, m)).Where(x => !_deleted.Contains(x.Item1));

    private bool TryGet(string arg, out int number, [NotNullWhen(true)] out StoredMessage? message)
    {
        message = null;
        if (!int.TryParse(arg.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 1 || number > _maildrop.Count || _deleted.Contains(number))
            return false;
        message = _maildrop[number - 1];
        return true;
    }

    private async Task WriteDotStuffedAsync(byte[] raw, int bodyLines, CancellationToken ct)
    {
        var position = 0;
        var inBody = false;
        var bodyCount = 0;
        while (position < raw.Length)
        {
            if (inBody && bodyLines >= 0 && bodyCount++ >= bodyLines)
                break;

            var newline = raw.AsSpan(position).IndexOf((byte)'\n');
            var end = newline < 0 ? raw.Length : position + newline;
            var lineEnd = end > position && raw[end - 1] == '\r' ? end - 1 : end;
            WriteStuffedLine(raw.AsSpan(position, lineEnd - position));
            if (!inBody && lineEnd == position)
                inBody = true;
            position = newline < 0 ? raw.Length : end + 1;
            await connection.FlushIfLargeAsync(ct);
        }
    }

    private void WriteStuffedLine(ReadOnlySpan<byte> line)
    {
        if (line.Length > 0 && line[0] == '.')
            connection.Write("."u8);
        connection.Write(line);
        connection.Write("\r\n"u8);
    }
}
