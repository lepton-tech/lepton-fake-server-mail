using System.Text;
using FakeServerEmail.Core;

namespace FakeServerEmail.Protocols.Smtp;

public sealed class SmtpHandler(ServerContext context, bool allowStartTls) : IProtocolHandler
{
    public string Protocol => "SMTP";

    private sealed class Transaction
    {
        public string? From;
        public readonly List<string> Recipients = [];
        public MemoryStream? Chunks;
        public bool ChunkTooLarge;

        public void Reset()
        {
            From = null;
            Recipients.Clear();
            Chunks?.Dispose();
            Chunks = null;
            ChunkTooLarge = false;
        }
    }

    public async Task HandleAsync(MailConnection c, CancellationToken ct)
    {
        var log = c.Log!;
        var tx = new Transaction();
        string? helo = null;
        string? user = null;
        var esmtp = false;

        await c.SendLineAsync($"220 {context.HostName} ESMTP Fake Server Email pronto", ct);

        while (true)
        {
            string? line;
            try
            {
                line = await c.ReadLineAsync(ct, 8192);
            }
            catch (LineTooLongException)
            {
                await c.SendLineAsync("500 5.5.6 Linha muito longa", ct);
                return;
            }
            if (line is null)
                return;

            log.FromClient(line);
            var space = line.IndexOf(' ');
            var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
            var arg = space < 0 ? "" : line[(space + 1)..].Trim();
            var settings = context.Settings.Current;
            var maxSize = settings.MaxMessageSizeMb * 1024L * 1024L;

            switch (verb)
            {
                case "HELO":
                    helo = arg;
                    esmtp = false;
                    tx.Reset();
                    c.WriteLine($"250 {context.HostName} Olá, {(arg.Length > 0 ? arg : c.RemoteAddress)}");
                    break;

                case "EHLO":
                    helo = arg;
                    esmtp = true;
                    tx.Reset();
                    c.WriteLine($"250-{context.HostName} Olá, {(arg.Length > 0 ? arg : c.RemoteAddress)}");
                    c.WriteLine($"250-SIZE {maxSize}");
                    c.WriteLine("250-8BITMIME");
                    c.WriteLine("250-SMTPUTF8");
                    c.WriteLine("250-PIPELINING");
                    c.WriteLine("250-ENHANCEDSTATUSCODES");
                    c.WriteLine("250-CHUNKING");
                    if (allowStartTls && !c.IsSecure)
                        c.WriteLine("250-STARTTLS");
                    c.WriteLine("250-AUTH " + string.Join(' ', Sasl.Mechanisms));
                    c.WriteLine("250 HELP");
                    break;

                case "STARTTLS":
                    if (!allowStartTls || c.IsSecure)
                    {
                        c.WriteLine("502 5.5.1 STARTTLS não disponível nesta conexão");
                        break;
                    }
                    await c.SendLineAsync("220 2.0.0 Pronto para iniciar TLS", ct);
                    await c.UpgradeToTlsAsync(ct);
                    log.Secure = true;
                    helo = null;
                    user = null;
                    tx.Reset();
                    continue;

                case "AUTH":
                    if (user is not null)
                    {
                        c.WriteLine("503 5.5.1 Já autenticado");
                        break;
                    }
                    if (tx.From is not null)
                    {
                        c.WriteLine("503 5.5.1 AUTH não permitido durante uma transação");
                        break;
                    }
                    var authParts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (authParts.Length == 0 || !Sasl.IsSupported(authParts[0]))
                    {
                        c.WriteLine("504 5.5.4 Mecanismo de autenticação não suportado");
                        break;
                    }
                    var outcome = await Sasl.AuthenticateAsync(authParts[0], authParts.Length > 1 ? authParts[1] : null, async challenge =>
                    {
                        await c.SendLineAsync("334 " + challenge, ct);
                        var response = await c.ReadLineAsync(ct, 64 * 1024);
                        if (response is not null)
                            log.FromClient(response);
                        return response;
                    }, context.Authenticator, context.OpenIdConfigurationUrl);

                    if (outcome.Success)
                    {
                        user = outcome.User;
                        log.User = user;
                        log.AuthMechanism = outcome.Mechanism;
                        log.Info($"Autenticado como '{user}' via {outcome.Mechanism}");
                        c.WriteLine("235 2.7.0 Autenticação aceita");
                    }
                    else if (outcome.Cancelled)
                    {
                        c.WriteLine("501 5.7.0 Autenticação cancelada");
                    }
                    else
                    {
                        log.Info($"Falha na autenticação {outcome.Mechanism}: {outcome.Error}");
                        c.WriteLine($"535 5.7.8 Falha na autenticação: {outcome.Error}");
                    }
                    break;

                case "MAIL":
                    if (settings.SmtpRequireAuth && user is null)
                    {
                        c.WriteLine("530 5.7.0 Autenticação obrigatória");
                        break;
                    }
                    if (tx.From is not null)
                    {
                        c.WriteLine("503 5.5.1 Remetente já informado");
                        break;
                    }
                    if (!TryParsePath(arg, "FROM:", out var from, out var mailParams))
                    {
                        c.WriteLine("501 5.5.4 Sintaxe: MAIL FROM:<endereço>");
                        break;
                    }
                    if (mailParams.TryGetValue("SIZE", out var sizeText) && long.TryParse(sizeText, out var declared) && declared > maxSize)
                    {
                        c.WriteLine($"552 5.3.4 Mensagem excede o limite de {settings.MaxMessageSizeMb} MB");
                        break;
                    }
                    tx.Reset();
                    tx.From = from;
                    c.WriteLine("250 2.1.0 Remetente OK");
                    break;

                case "RCPT":
                    if (tx.From is null)
                    {
                        c.WriteLine("503 5.5.1 Informe MAIL FROM primeiro");
                        break;
                    }
                    if (!TryParsePath(arg, "TO:", out var to, out _) || string.IsNullOrEmpty(to))
                    {
                        c.WriteLine("501 5.5.4 Sintaxe: RCPT TO:<endereço>");
                        break;
                    }
                    if (tx.Recipients.Count >= 1000)
                    {
                        c.WriteLine("452 4.5.3 Destinatários demais");
                        break;
                    }
                    tx.Recipients.Add(to);
                    c.WriteLine("250 2.1.5 Destinatário OK");
                    break;

                case "DATA":
                    if (tx.From is null || tx.Recipients.Count == 0)
                    {
                        c.WriteLine(tx.From is null ? "503 5.5.1 Informe MAIL FROM primeiro" : "554 5.5.1 Nenhum destinatário válido");
                        break;
                    }
                    await c.SendLineAsync("354 Envie a mensagem; termine com <CRLF>.<CRLF>", ct);
                    var (data, tooLarge) = await c.ReadDotTerminatedAsync(maxSize, ct);
                    if (data is null && !tooLarge)
                        return;
                    log.FromClient(tooLarge ? "[mensagem descartada: tamanho excedido]" : $"[conteúdo da mensagem: {data!.Length} bytes]");
                    if (tooLarge)
                        c.WriteLine($"552 5.3.4 Mensagem excede o limite de {settings.MaxMessageSizeMb} MB");
                    else
                        c.WriteLine(Deliver(c, tx, data!, helo, user, esmtp));
                    tx.Reset();
                    break;

                case "BDAT":
                    var bdat = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (bdat.Length == 0 || !long.TryParse(bdat[0], out var chunkSize) || chunkSize < 0)
                    {
                        c.WriteLine("501 5.5.4 Sintaxe: BDAT <tamanho> [LAST]");
                        break;
                    }
                    var last = bdat.Length > 1 && bdat[1].Equals("LAST", StringComparison.OrdinalIgnoreCase);
                    var chunk = await c.ReadExactAsync(chunkSize, ct);
                    if (chunk is null)
                        return;
                    log.FromClient($"[bloco BDAT: {chunkSize} bytes{(last ? ", último" : "")}]");
                    if (tx.From is null || tx.Recipients.Count == 0)
                    {
                        c.WriteLine("503 5.5.1 Informe MAIL FROM e RCPT TO primeiro");
                        tx.Reset();
                        break;
                    }
                    tx.Chunks ??= new MemoryStream();
                    if (!tx.ChunkTooLarge)
                        tx.Chunks.Write(chunk);
                    if (tx.Chunks.Length > maxSize)
                    {
                        tx.ChunkTooLarge = true;
                        tx.Chunks.SetLength(0);
                    }
                    if (!last)
                    {
                        c.WriteLine($"250 2.0.0 {chunkSize} bytes recebidos");
                        break;
                    }
                    if (tx.ChunkTooLarge)
                        c.WriteLine($"552 5.3.4 Mensagem excede o limite de {settings.MaxMessageSizeMb} MB");
                    else
                        c.WriteLine(Deliver(c, tx, tx.Chunks.ToArray(), helo, user, esmtp));
                    tx.Reset();
                    break;

                case "RSET":
                    tx.Reset();
                    c.WriteLine("250 2.0.0 OK");
                    break;

                case "NOOP":
                    c.WriteLine("250 2.0.0 OK");
                    break;

                case "VRFY":
                case "EXPN":
                    c.WriteLine("252 2.5.2 Não é possível verificar, mas a mensagem será aceita");
                    break;

                case "HELP":
                    c.WriteLine("214 2.0.0 Comandos: HELO EHLO STARTTLS AUTH MAIL RCPT DATA BDAT RSET NOOP VRFY HELP QUIT");
                    break;

                case "QUIT":
                    await c.SendLineAsync("221 2.0.0 Até logo", ct);
                    return;

                default:
                    c.WriteLine("500 5.5.2 Comando não reconhecido");
                    break;
            }

            if (!c.HasBufferedInput)
                await c.FlushAsync(ct);
        }
    }

    private string Deliver(MailConnection c, Transaction tx, byte[] data, string? helo, string? user, bool esmtp)
    {
        var with = (esmtp ? "ESMTP" : "SMTP") + (c.IsSecure ? "S" : "") + (user is not null ? "A" : "");
        var received = $"Received: from {(string.IsNullOrEmpty(helo) ? "desconhecido" : helo)} ({c.RemoteAddress})\r\n" +
                       $"\tby {context.HostName} (Fake Server Email) with {with};\r\n" +
                       $"\t{MimeKit.Utils.DateUtils.FormatDate(DateTimeOffset.Now)}\r\n";
        var raw = Encoding.ASCII.GetBytes(received).Concat(data).ToArray();

        var message = context.Store.Add(raw, new MessageOrigin("SMTP", tx.From, [.. tx.Recipients], c.RemoteAddress, user));
        c.Log!.MessagesReceived++;
        c.Log.Info($"Mensagem armazenada: \"{message.Subject}\" ({message.Size} bytes)");
        context.Log.Report($"SMTP  de {tx.From} para {string.Join(", ", tx.Recipients)}: {message.Subject}");
        return $"250 2.0.0 Mensagem aceita, id {message.Id}";
    }

    private static bool TryParsePath(string arg, string prefix, out string address, out Dictionary<string, string> parameters)
    {
        address = "";
        parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = arg[prefix.Length..].TrimStart();
        string tail;
        if (rest.StartsWith('<'))
        {
            var close = rest.IndexOf('>');
            if (close < 0)
                return false;
            address = rest[1..close];
            tail = rest[(close + 1)..];
        }
        else
        {
            var sp = rest.IndexOf(' ');
            address = sp < 0 ? rest : rest[..sp];
            tail = sp < 0 ? "" : rest[sp..];
        }

        var colon = address.IndexOf(':');
        if (address.StartsWith('@') && colon > 0)
            address = address[(colon + 1)..];

        foreach (var p in tail.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = p.IndexOf('=');
            parameters[eq < 0 ? p : p[..eq]] = eq < 0 ? "" : p[(eq + 1)..];
        }
        return true;
    }
}
