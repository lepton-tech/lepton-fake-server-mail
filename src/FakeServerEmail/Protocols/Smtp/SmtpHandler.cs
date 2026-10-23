using System.Globalization;
using System.Text;
using FakeServerEmail.Core;

namespace FakeServerEmail.Protocols.Smtp;

public sealed class SmtpHandler(ServerContext context, bool allowStartTls) : IProtocolHandler
{
    public string Protocol => "SMTP";

    public Task HandleAsync(MailConnection connection, CancellationToken ct) => new SmtpSession(context, connection, allowStartTls).RunAsync(ct);
}

internal sealed class SmtpSession(ServerContext context, MailConnection connection, bool allowStartTls)
{
    private const int CommandLineLimit = 8192;
    private const int MaxRecipients = 1000;

    private sealed class Transaction
    {
        public string? From { get; set; }
        public List<string> Recipients { get; } = [];
        public MemoryStream? Chunks { get; private set; }
        public bool ChunksTooLarge { get; private set; }

        public MemoryStream? ChunkDestination(long size, long maxSize)
        {
            if (ChunksTooLarge)
                return null;
            Chunks ??= new MemoryStream();
            if (Chunks.Length + size <= maxSize)
                return Chunks;
            ChunksTooLarge = true;
            Chunks.Dispose();
            Chunks = null;
            return null;
        }

        public void Reset()
        {
            From = null;
            Recipients.Clear();
            Chunks?.Dispose();
            Chunks = null;
            ChunksTooLarge = false;
        }
    }

    private readonly ProtocolSession _log = connection.Log!;
    private readonly Transaction _tx = new();
    private string? _helo;
    private string? _user;
    private bool _esmtp;

    public async Task RunAsync(CancellationToken ct)
    {
        await connection.SendLineAsync($"220 {context.HostName} ESMTP Fake Server Email pronto", ct);

        while (true)
        {
            string? line;
            try
            {
                line = await connection.ReadLineAsync(ct, CommandLineLimit);
            }
            catch (LineTooLongException)
            {
                await connection.SendLineAsync("500 5.5.6 Linha muito longa", ct);
                return;
            }
            if (line is null)
                return;

            var space = line.IndexOf(' ');
            var word = space < 0 ? line : line[..space];
            var verb = word.ToUpperInvariant();
            var arg = space < 0 ? "" : line[(space + 1)..].Trim();
            _log.FromClient(verb == "AUTH" && arg.Length > 0 ? $"{word} {Sasl.MaskArguments(arg)}" : line);
            var settings = context.Settings.Current;

            switch (verb)
            {
                case "HELO":
                    Helo(arg);
                    break;
                case "EHLO":
                    Ehlo(arg, settings);
                    break;
                case "STARTTLS":
                    await StartTlsAsync(settings, ct);
                    break;
                case "AUTH":
                    await AuthAsync(arg, settings, ct);
                    break;
                case "MAIL":
                    Mail(arg, settings);
                    break;
                case "RCPT":
                    Rcpt(arg);
                    break;
                case "DATA":
                    if (!await DataAsync(settings, ct))
                        return;
                    break;
                case "BDAT":
                    if (!await BdatAsync(arg, settings, ct))
                        return;
                    break;
                case "RSET":
                    _tx.Reset();
                    connection.WriteLine("250 2.0.0 OK");
                    break;
                case "NOOP":
                    connection.WriteLine("250 2.0.0 OK");
                    break;
                case "VRFY":
                case "EXPN":
                    connection.WriteLine("252 2.5.2 Não é possível verificar, mas a mensagem será aceita");
                    break;
                case "HELP":
                    connection.WriteLine("214 2.0.0 Comandos: HELO EHLO STARTTLS AUTH MAIL RCPT DATA BDAT RSET NOOP VRFY HELP QUIT");
                    break;
                case "QUIT":
                    await connection.SendLineAsync("221 2.0.0 Até logo", ct);
                    return;
                default:
                    connection.WriteLine("500 5.5.2 Comando não reconhecido");
                    break;
            }

            await connection.FlushIfIdleAsync(ct);
        }
    }

    private void Helo(string arg)
    {
        _helo = arg;
        _esmtp = false;
        _tx.Reset();
        connection.WriteLine($"250 {context.HostName} Olá, {Greeting(arg)}");
    }

    private void Ehlo(string arg, AppSettings settings)
    {
        _helo = arg;
        _esmtp = true;
        _tx.Reset();
        connection.WriteLine($"250-{context.HostName} Olá, {Greeting(arg)}");
        connection.WriteLine($"250-SIZE {settings.MaxMessageBytes()}");
        connection.WriteLine("250-8BITMIME");
        connection.WriteLine("250-SMTPUTF8");
        connection.WriteLine("250-PIPELINING");
        connection.WriteLine("250-ENHANCEDSTATUSCODES");
        connection.WriteLine("250-CHUNKING");
        if (connection.CanStartTls(allowStartTls, settings))
            connection.WriteLine("250-STARTTLS");
        if (settings.AuthMechanisms.Count > 0)
            connection.WriteLine("250-AUTH " + string.Join(' ', settings.AuthMechanisms));
        connection.WriteLine("250 HELP");
    }

    private string Greeting(string arg) => arg.Length > 0 ? arg : connection.RemoteAddress;

    private async Task StartTlsAsync(AppSettings settings, CancellationToken ct)
    {
        if (!connection.CanStartTls(allowStartTls, settings))
        {
            connection.WriteLine("502 5.5.1 STARTTLS não disponível nesta conexão");
            return;
        }
        await connection.SendLineAsync("220 2.0.0 Pronto para iniciar TLS", ct);
        await connection.UpgradeToTlsAsync(ct);
        _helo = null;
        _user = null;
        _tx.Reset();
    }

    private async Task AuthAsync(string arg, AppSettings settings, CancellationToken ct)
    {
        if (_user is not null)
        {
            connection.WriteLine("503 5.5.1 Já autenticado");
            return;
        }
        if (_tx.From is not null)
        {
            connection.WriteLine("503 5.5.1 AUTH não permitido durante uma transação");
            return;
        }
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !Sasl.IsOffered(settings, parts[0]))
        {
            connection.WriteLine("504 5.5.4 Mecanismo de autenticação não suportado");
            return;
        }

        var outcome = await Sasl.ExchangeAsync(connection, context, parts[0], parts.Length > 1 ? parts[1] : null, "334 ", ct);
        if (outcome.Success)
        {
            _user = outcome.User ?? "";
            _log.MarkAuthenticated(_user, outcome.Mechanism);
            connection.WriteLine("235 2.7.0 Autenticação aceita");
        }
        else if (outcome.Cancelled)
        {
            connection.WriteLine("501 5.7.0 Autenticação cancelada");
        }
        else
        {
            connection.WriteLine($"535 5.7.8 Falha na autenticação: {outcome.Error}");
        }
    }

    private void Mail(string arg, AppSettings settings)
    {
        if (settings.SmtpRequireAuth && _user is null)
        {
            connection.WriteLine("530 5.7.0 Autenticação obrigatória");
            return;
        }
        if (_tx.From is not null)
        {
            connection.WriteLine("503 5.5.1 Remetente já informado");
            return;
        }
        if (!TryParsePath(arg, "FROM:", out var from, out var parameters))
        {
            connection.WriteLine("501 5.5.4 Sintaxe: MAIL FROM:<endereço>");
            return;
        }
        if (parameters.TryGetValue("SIZE", out var sizeText)
            && long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var declared)
            && declared > settings.MaxMessageBytes())
        {
            connection.WriteLine(TooLargeReply(settings));
            return;
        }
        _tx.Reset();
        _tx.From = from;
        connection.WriteLine("250 2.1.0 Remetente OK");
    }

    private void Rcpt(string arg)
    {
        if (_tx.From is null)
        {
            connection.WriteLine("503 5.5.1 Informe MAIL FROM primeiro");
            return;
        }
        if (!TryParsePath(arg, "TO:", out var to, out _) || string.IsNullOrEmpty(to))
        {
            connection.WriteLine("501 5.5.4 Sintaxe: RCPT TO:<endereço>");
            return;
        }
        if (_tx.Recipients.Count >= MaxRecipients)
        {
            connection.WriteLine("452 4.5.3 Destinatários demais");
            return;
        }
        _tx.Recipients.Add(to);
        connection.WriteLine("250 2.1.5 Destinatário OK");
    }

    private async Task<bool> DataAsync(AppSettings settings, CancellationToken ct)
    {
        if (_tx.From is null || _tx.Recipients.Count == 0)
        {
            connection.WriteLine(_tx.From is null ? "503 5.5.1 Informe MAIL FROM primeiro" : "554 5.5.1 Nenhum destinatário válido");
            return true;
        }

        await connection.SendLineAsync("354 Envie a mensagem; termine com <CRLF>.<CRLF>", ct);
        using var data = new MemoryStream();
        var result = await connection.ReadDotTerminatedAsync(data, settings.MaxMessageBytes(), ct);
        if (!result.Completed)
            return false;

        if (result.TooLarge)
        {
            _log.FromClient("[mensagem descartada: tamanho excedido]");
            connection.WriteLine(TooLargeReply(settings));
        }
        else
        {
            _log.FromClient($"[conteúdo da mensagem: {data.Length} bytes]");
            if (result.BareLineFeeds)
                _log.Info("A mensagem usou quebras de linha só com LF, sem o CR que o SMTP exige; ela foi aceita assim mesmo");
            connection.WriteLine(Deliver(data.GetBuffer().AsSpan(0, (int)data.Length)));
        }
        _tx.Reset();
        return true;
    }

    private async Task<bool> BdatAsync(string arg, AppSettings settings, CancellationToken ct)
    {
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
        {
            connection.WriteLine("501 5.5.4 Sintaxe: BDAT <tamanho> [LAST]");
            return true;
        }

        var last = parts.Length > 1 && parts[1].Equals("LAST", StringComparison.OrdinalIgnoreCase);
        var ready = _tx.From is not null && _tx.Recipients.Count > 0;
        var destination = ready ? _tx.ChunkDestination(size, settings.MaxMessageBytes()) : null;
        if (!await connection.CopyExactAsync(size, destination, ct))
            return false;
        _log.FromClient($"[bloco BDAT: {size} bytes{(last ? ", último" : "")}]");

        if (!ready)
        {
            connection.WriteLine("503 5.5.1 Informe MAIL FROM e RCPT TO primeiro");
            _tx.Reset();
            return true;
        }
        if (!last)
        {
            connection.WriteLine($"250 2.0.0 {size} bytes recebidos");
            return true;
        }

        if (_tx.ChunksTooLarge || _tx.Chunks is null)
            connection.WriteLine(TooLargeReply(settings));
        else
            connection.WriteLine(Deliver(_tx.Chunks.GetBuffer().AsSpan(0, (int)_tx.Chunks.Length)));
        _tx.Reset();
        return true;
    }

    private static string TooLargeReply(AppSettings settings) => $"552 5.3.4 Mensagem excede o limite de {settings.MaxMessageSizeMb} MB";

    private string Deliver(ReadOnlySpan<byte> data)
    {
        var with = (_esmtp ? "ESMTP" : "SMTP") + (connection.IsSecure ? "S" : "") + (_user is not null ? "A" : "");
        var received = $"Received: from {(string.IsNullOrEmpty(_helo) ? "desconhecido" : _helo)} ({connection.RemoteAddress})\r\n" +
                       $"\tby {context.HostName} (Fake Server Email) with {with};\r\n" +
                       $"\t{MimeKit.Utils.DateUtils.FormatDate(DateTimeOffset.Now)}\r\n";
        var raw = new byte[Encoding.ASCII.GetByteCount(received) + data.Length];
        var headerLength = Encoding.ASCII.GetBytes(received, raw);
        data.CopyTo(raw.AsSpan(headerLength));

        var message = context.Store.Add(raw, new MessageOrigin("SMTP", _tx.From, [.. _tx.Recipients], connection.RemoteAddress, _user));
        _log.MessagesReceived++;
        _log.Info($"Mensagem armazenada: \"{message.Subject}\" ({message.Size} bytes)");
        context.Log.Report($"SMTP  de {_tx.From} para {string.Join(", ", _tx.Recipients)}: {message.Subject}");
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
