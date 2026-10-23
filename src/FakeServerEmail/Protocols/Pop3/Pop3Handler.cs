using FakeServerEmail.Core;

namespace FakeServerEmail.Protocols.Pop3;

public sealed class Pop3Handler(ServerContext context, bool allowStartTls) : IProtocolHandler
{
    public string Protocol => "POP3";

    public async Task HandleAsync(MailConnection c, CancellationToken ct)
    {
        var log = c.Log!;
        string? pendingUser = null;
        string? user = null;
        List<StoredMessage> maildrop = [];
        var deleted = new HashSet<int>();

        await c.SendLineAsync("+OK Fake Server Email POP3 pronto", ct);

        while (true)
        {
            string? line;
            try
            {
                line = await c.ReadLineAsync(ct, 8192);
            }
            catch (LineTooLongException)
            {
                await c.SendLineAsync("-ERR Linha muito longa", ct);
                return;
            }
            if (line is null)
                return;

            var parts = line.Split(' ', 2);
            var verb = parts[0].ToUpperInvariant();
            var arg = parts.Length > 1 ? parts[1].Trim() : "";
            log.FromClient(verb == "PASS" ? "PASS ********" : line);

            if (user is null)
            {
                switch (verb)
                {
                    case "CAPA":
                        WriteCapabilities(c, false);
                        break;
                    case "STLS":
                        if (!allowStartTls || !context.Settings.Current.OfferStartTls || c.IsSecure)
                        {
                            c.WriteLine("-ERR STLS não disponível nesta conexão");
                            break;
                        }
                        await c.SendLineAsync("+OK Iniciando TLS", ct);
                        await c.UpgradeToTlsAsync(ct);
                        log.Secure = true;
                        pendingUser = null;
                        continue;
                    case "USER":
                        if (arg.Length == 0)
                        {
                            c.WriteLine("-ERR Informe o usuário");
                            break;
                        }
                        pendingUser = arg;
                        c.WriteLine("+OK Usuário aceito, envie PASS");
                        break;
                    case "PASS":
                        if (pendingUser is null)
                        {
                            c.WriteLine("-ERR Envie USER primeiro");
                            break;
                        }
                        var result = context.Authenticator.ValidatePassword(pendingUser, arg);
                        if (!result.Success)
                        {
                            log.Info("Falha na autenticação: " + result.Error);
                            c.WriteLine("-ERR [AUTH] " + result.Error);
                            pendingUser = null;
                            break;
                        }
                        user = result.User ?? pendingUser;
                        maildrop = OpenMaildrop(log, user, "USER/PASS");
                        c.WriteLine($"+OK Caixa de '{user}' aberta com {maildrop.Count} mensagens");
                        break;
                    case "AUTH":
                        if (arg.Length == 0)
                        {
                            c.WriteLine("+OK Mecanismos suportados");
                            foreach (var m in context.Settings.Current.AuthMechanisms)
                                c.WriteLine(m);
                            c.WriteLine(".");
                            break;
                        }
                        var authParts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (!Sasl.IsOffered(context.Settings.Current, authParts[0]))
                        {
                            c.WriteLine("-ERR Mecanismo de autenticação não suportado");
                            break;
                        }
                        var outcome = await Sasl.AuthenticateAsync(authParts[0], authParts.Length > 1 ? authParts[1] : null, async challenge =>
                        {
                            await c.SendLineAsync("+ " + challenge, ct);
                            var response = await c.ReadLineAsync(ct, 64 * 1024);
                            if (response is not null)
                                log.FromClient(response);
                            return response;
                        }, context.Authenticator, context.OpenIdConfigurationUrl);

                        if (outcome.Success)
                        {
                            user = outcome.User ?? "";
                            maildrop = OpenMaildrop(log, user, outcome.Mechanism);
                            c.WriteLine($"+OK Caixa de '{user}' aberta com {maildrop.Count} mensagens");
                        }
                        else if (outcome.Cancelled)
                        {
                            c.WriteLine("-ERR Autenticação cancelada");
                        }
                        else
                        {
                            log.Info($"Falha na autenticação {outcome.Mechanism}: {outcome.Error}");
                            c.WriteLine("-ERR [AUTH] Falha na autenticação: " + outcome.Error);
                        }
                        break;
                    case "QUIT":
                        await c.SendLineAsync("+OK Até logo", ct);
                        return;
                    case "NOOP":
                        c.WriteLine("+OK");
                        break;
                    default:
                        c.WriteLine("-ERR Comando inválido ou não autenticado");
                        break;
                }

                if (!c.HasBufferedInput)
                    await c.FlushAsync(ct);
                continue;
            }

            switch (verb)
            {
                case "CAPA":
                    WriteCapabilities(c, true);
                    break;

                case "STAT":
                    var live = Live(maildrop, deleted).ToList();
                    c.WriteLine($"+OK {live.Count} {live.Sum(x => x.Message.Size)}");
                    break;

                case "LIST":
                case "UIDL":
                    if (arg.Length > 0)
                    {
                        if (!TryGet(arg, maildrop, deleted, out var number, out var single))
                        {
                            c.WriteLine("-ERR Mensagem inexistente");
                            break;
                        }
                        c.WriteLine($"+OK {number} {(verb == "LIST" ? single!.Size : single!.Id)}");
                        break;
                    }
                    c.WriteLine("+OK Lista de mensagens");
                    foreach (var (number, message) in Live(maildrop, deleted))
                        c.WriteLine($"{number} {(verb == "LIST" ? message.Size : message.Id)}");
                    c.WriteLine(".");
                    break;

                case "RETR":
                case "TOP":
                    var args = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (args.Length == 0 || !TryGet(args[0], maildrop, deleted, out _, out var target))
                    {
                        c.WriteLine("-ERR Mensagem inexistente");
                        break;
                    }
                    var lines = -1;
                    if (verb == "TOP" && (args.Length < 2 || !int.TryParse(args[1], out lines) || lines < 0))
                    {
                        c.WriteLine("-ERR Sintaxe: TOP <mensagem> <linhas>");
                        break;
                    }
                    var raw = context.Store.GetRaw(target!.Id);
                    if (raw is null)
                    {
                        c.WriteLine("-ERR Mensagem não existe mais no servidor");
                        break;
                    }
                    c.WriteLine($"+OK {raw.Length} octetos");
                    WriteDotStuffed(c, raw, lines);
                    log.FromServer($"[conteúdo da mensagem: {raw.Length} bytes]");
                    c.WriteLine(".");
                    if (verb == "RETR")
                        context.Store.SetSeen([target.Id], true);
                    break;

                case "DELE":
                    if (!TryGet(arg, maildrop, deleted, out var deleteNumber, out _))
                    {
                        c.WriteLine("-ERR Mensagem inexistente");
                        break;
                    }
                    deleted.Add(deleteNumber);
                    c.WriteLine($"+OK Mensagem {deleteNumber} marcada para exclusão");
                    break;

                case "RSET":
                    deleted.Clear();
                    c.WriteLine($"+OK {maildrop.Count} mensagens");
                    break;

                case "NOOP":
                    c.WriteLine("+OK");
                    break;

                case "QUIT":
                    var toDelete = deleted.Select(n => maildrop[n - 1].Id).ToList();
                    if (toDelete.Count > 0)
                    {
                        context.Store.Delete(toDelete);
                        log.Info($"{toDelete.Count} mensagens excluídas");
                    }
                    await c.SendLineAsync($"+OK Até logo ({toDelete.Count} mensagens excluídas)", ct);
                    return;

                default:
                    c.WriteLine("-ERR Comando não reconhecido");
                    break;
            }

            if (!c.HasBufferedInput)
                await c.FlushAsync(ct);
        }
    }

    private List<StoredMessage> OpenMaildrop(ProtocolSession log, string user, string mechanism)
    {
        log.User = user;
        log.AuthMechanism = mechanism;
        var separate = context.Settings.Current.SeparateMailboxesByRecipient;
        var maildrop = context.Store.List(MessageStore.Inbox).Where(m => MessageStore.IsVisibleTo(m, user, separate)).ToList();
        log.Info($"Autenticado como '{user}' via {mechanism}; {maildrop.Count} mensagens na caixa");
        return maildrop;
    }

    private void WriteCapabilities(MailConnection c, bool authenticated)
    {
        c.WriteLine("+OK Lista de capacidades");
        c.WriteLine("TOP");
        c.WriteLine("UIDL");
        c.WriteLine("RESP-CODES");
        c.WriteLine("AUTH-RESP-CODE");
        c.WriteLine("PIPELINING");
        if (!authenticated)
        {
            var settings = context.Settings.Current;
            c.WriteLine("USER");
            if (settings.AuthMechanisms.Count > 0)
                c.WriteLine("SASL " + string.Join(' ', settings.AuthMechanisms));
            if (allowStartTls && settings.OfferStartTls && !c.IsSecure)
                c.WriteLine("STLS");
        }
        c.WriteLine("IMPLEMENTATION Fake-Server-Email");
        c.WriteLine(".");
    }

    private static IEnumerable<(int Number, StoredMessage Message)> Live(List<StoredMessage> maildrop, HashSet<int> deleted) =>
        maildrop.Select((m, i) => (i + 1, m)).Where(x => !deleted.Contains(x.Item1));

    private static bool TryGet(string arg, List<StoredMessage> maildrop, HashSet<int> deleted, out int number, out StoredMessage? message)
    {
        message = null;
        if (!int.TryParse(arg.Trim(), out number) || number < 1 || number > maildrop.Count || deleted.Contains(number))
            return false;
        message = maildrop[number - 1];
        return true;
    }

    private static void WriteDotStuffed(MailConnection c, byte[] raw, int bodyLines)
    {
        var position = 0;
        var inBody = false;
        var bodyCount = 0;
        while (position < raw.Length)
        {
            var newline = Array.IndexOf(raw, (byte)'\n', position);
            var end = newline < 0 ? raw.Length : newline;
            var lineEnd = end > position && raw[end - 1] == '\r' ? end - 1 : end;
            var line = raw.AsSpan(position, lineEnd - position);

            if (inBody && bodyLines >= 0 && bodyCount++ >= bodyLines)
                break;

            if (line.Length > 0 && line[0] == '.')
                c.Write("."u8);
            c.Write(line);
            c.Write("\r\n"u8);

            if (!inBody && line.Length == 0)
                inBody = true;
            position = newline < 0 ? raw.Length : newline + 1;
        }
    }
}
