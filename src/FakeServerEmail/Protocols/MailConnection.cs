using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FakeServerEmail.Core;

namespace FakeServerEmail.Protocols;

public sealed class LineTooLongException() : Exception("Linha muito longa");

public sealed class MailConnection : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, false);

    private readonly Socket _socket;
    private readonly NetworkStream _network;
    private readonly X509Certificate2 _certificate;
    private readonly MemoryStream _output = new();
    private Stream _stream;
    private byte[] _buffer = new byte[16 * 1024];
    private int _start;
    private int _end;

    public MailConnection(Socket socket, X509Certificate2 certificate, int localPort)
    {
        _socket = socket;
        _network = new NetworkStream(socket, true);
        _stream = _network;
        _certificate = certificate;
        LocalPort = localPort;
        RemoteAddress = (socket.RemoteEndPoint as IPEndPoint)?.ToString() ?? "?";
    }

    public string RemoteAddress { get; }

    public int LocalPort { get; }

    public bool IsSecure { get; private set; }

    public string? TlsProtocol { get; private set; }

    public ProtocolSession? Log { get; set; }

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public async Task UpgradeToTlsAsync(CancellationToken ct)
    {
        _start = _end = 0;
        var ssl = new SslStream(_network, false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _certificate,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.None,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }, cts.Token);
        _stream = ssl;
        IsSecure = true;
        TlsProtocol = ssl.SslProtocol.ToString();
        Log?.Info($"TLS estabelecido ({TlsProtocol}, {ssl.NegotiatedCipherSuite})");
    }

    public async Task<byte[]?> ReadLineBytesAsync(CancellationToken ct, int maxLength = 64 * 1024)
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                var length = newline - _start;
                if (length > 0 && _buffer[newline - 1] == '\r')
                    length--;
                var line = _buffer.AsSpan(_start, length).ToArray();
                _start = newline + 1;
                return line;
            }

            if (_end - _start > maxLength)
                throw new LineTooLongException();

            if (!await FillAsync(ct))
            {
                if (_end > _start)
                {
                    var rest = _buffer.AsSpan(_start, _end - _start).ToArray();
                    _start = _end;
                    return rest;
                }
                return null;
            }
        }
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct, int maxLength = 64 * 1024)
    {
        var bytes = await ReadLineBytesAsync(ct, maxLength);
        return bytes is null ? null : Utf8.GetString(bytes);
    }

    public async Task<byte[]?> ReadExactAsync(long count, CancellationToken ct)
    {
        var result = new byte[count];
        long copied = 0;
        while (copied < count)
        {
            if (_end == _start && !await FillAsync(ct))
                return null;
            var take = (int)Math.Min(_end - _start, count - copied);
            Buffer.BlockCopy(_buffer, _start, result, (int)copied, take);
            _start += take;
            copied += take;
        }
        return result;
    }

    public async Task<(byte[]? Data, bool TooLarge)> ReadDotTerminatedAsync(long maxSize, CancellationToken ct)
    {
        using var data = new MemoryStream();
        var tooLarge = false;
        while (true)
        {
            var line = await ReadLineBytesAsync(ct, 1024 * 1024);
            if (line is null)
                return (null, false);
            if (line.Length == 1 && line[0] == '.')
                return (tooLarge ? null : data.ToArray(), tooLarge);
            if (tooLarge)
                continue;
            var offset = line.Length > 0 && line[0] == '.' ? 1 : 0;
            data.Write(line, offset, line.Length - offset);
            data.Write("\r\n"u8);
            if (data.Length > maxSize)
                tooLarge = true;
        }
    }

    public void Write(string text)
    {
        var bytes = Utf8.GetBytes(text);
        _output.Write(bytes);
    }

    public void Write(ReadOnlySpan<byte> bytes) => _output.Write(bytes);

    public void WriteLine(string line)
    {
        Write(line);
        _output.Write("\r\n"u8);
        Log?.FromServer(line);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        if (_output.Length == 0)
            return;
        await _stream.WriteAsync(_output.GetBuffer().AsMemory(0, (int)_output.Length), ct);
        await _stream.FlushAsync(ct);
        _output.SetLength(0);
    }

    public async Task SendLineAsync(string line, CancellationToken ct)
    {
        WriteLine(line);
        await FlushAsync(ct);
    }

    public bool HasBufferedInput => _end > _start;

    public long PendingOutput => _output.Length;

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buffer.Length)
            Array.Resize(ref _buffer, _buffer.Length * 2);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(IdleTimeout);
        int read;
        try
        {
            read = await _stream.ReadAsync(_buffer.AsMemory(_end), cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Tempo de inatividade esgotado");
        }
        catch (IOException)
        {
            return false;
        }
        if (read <= 0)
            return false;
        _end += read;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stream.DisposeAsync();
        }
        catch
        {
        }
        try
        {
            _socket.Dispose();
        }
        catch
        {
        }
    }
}
