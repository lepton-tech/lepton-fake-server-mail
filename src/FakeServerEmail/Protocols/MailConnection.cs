using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FakeServerEmail.Core;

namespace FakeServerEmail.Protocols;

public sealed class LineTooLongException() : Exception("Linha muito longa");

public readonly record struct DotTerminatedResult(bool Completed, bool TooLarge, bool BareLineFeeds);

public sealed class MailConnection : IAsyncDisposable
{
    private const int BufferSize = 16 * 1024;
    private const int DefaultLineLimit = 64 * 1024;
    private const int PendingOutputLimit = 1024 * 1024;
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TlsHandshakeTimeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding Utf8 = new(false, false);

    private readonly Socket _socket;
    private readonly NetworkStream _network;
    private readonly X509Certificate2 _certificate;
    private readonly MemoryStream _output = new();
    private Stream _stream;
    private byte[] _buffer = new byte[BufferSize];
    private int _start;
    private int _end;
    private int _scanned;
    private bool _skipLine;
    private CancellationTokenSource? _readTimeout;
    private CancellationToken _readTimeoutParent;

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

    public TimeSpan IdleTimeout { get; set; } = DefaultIdleTimeout;

    public bool HasBufferedInput => _end > _start;

    public bool CanStartTls(bool portAllowsStartTls, AppSettings settings) => portAllowsStartTls && settings.OfferStartTls && !IsSecure;

    public async Task UpgradeToTlsAsync(CancellationToken ct)
    {
        _start = _end = _scanned = 0;
        _skipLine = false;
        var ssl = new SslStream(_network, false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TlsHandshakeTimeout);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, timeout.Token);
        }
        catch (Exception ex)
        {
            await ssl.DisposeAsync();
            if (ex is OperationCanceledException && !ct.IsCancellationRequested)
                throw new TimeoutException("Tempo esgotado na negociação TLS", ex);
            throw;
        }

        _stream = ssl;
        IsSecure = true;
        TlsProtocol = ssl.SslProtocol.ToString();
        if (Log is not null)
        {
            Log.Secure = true;
            Log.Info($"TLS estabelecido ({TlsProtocol}, {ssl.NegotiatedCipherSuite})");
        }
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct, int maxLength = DefaultLineLimit)
    {
        while (true)
        {
            var from = Math.Max(_scanned, _start);
            var found = _buffer.AsSpan(from, _end - from).IndexOf((byte)'\n');
            if (found >= 0)
            {
                var newline = from + found;
                var start = _start;
                var length = newline - start;
                if (length > 0 && _buffer[newline - 1] == '\r')
                    length--;
                _start = _scanned = newline + 1;
                if (_skipLine)
                {
                    _skipLine = false;
                    continue;
                }
                if (length > maxLength)
                    throw new LineTooLongException();
                return Utf8.GetString(_buffer, start, length);
            }

            _scanned = _end;
            if (_skipLine)
                _start = _end;
            else if (_end - _start > maxLength + 1)
            {
                _start = _end;
                _skipLine = true;
                throw new LineTooLongException();
            }

            if (!await FillAsync(ct))
            {
                if (_end == _start)
                    return null;
                var rest = Utf8.GetString(_buffer, _start, _end - _start);
                _start = _scanned = _end;
                return rest;
            }
        }
    }

    public async Task<bool> CopyExactAsync(long count, Stream? destination, CancellationToken ct)
    {
        while (count > 0)
        {
            if (_end == _start && !await FillAsync(ct))
                return false;
            var take = (int)Math.Min(_end - _start, count);
            destination?.Write(_buffer, _start, take);
            _start += take;
            count -= take;
        }
        return true;
    }

    public async Task<DotTerminatedResult> ReadDotTerminatedAsync(MemoryStream destination, long maxSize, CancellationToken ct)
    {
        var size = 0L;
        var tooLarge = false;
        var bareLineFeeds = false;
        var lineStart = true;
        var afterCr = false;

        void Append(ReadOnlySpan<byte> bytes)
        {
            size += bytes.Length;
            if (tooLarge)
                return;
            if (size > maxSize)
            {
                tooLarge = true;
                destination.SetLength(0);
                destination.Capacity = 0;
                return;
            }
            destination.Write(bytes);
        }

        while (true)
        {
            if (_end == _start && !await FillAsync(ct))
                return new DotTerminatedResult(false, tooLarge, bareLineFeeds);

            if (lineStart && _buffer[_start] == '.')
            {
                var available = _end - _start;
                if (available < 2 || (available < 3 && _buffer[_start + 1] == '\r'))
                {
                    if (!await FillAsync(ct))
                        return new DotTerminatedResult(false, tooLarge, bareLineFeeds);
                    continue;
                }
                var next = _buffer[_start + 1];
                if (next == '\n' || (next == '\r' && _buffer[_start + 2] == '\n'))
                {
                    _start += next == '\n' ? 2 : 3;
                    return new DotTerminatedResult(true, tooLarge, bareLineFeeds || next == '\n');
                }
                _start++;
            }
            lineStart = false;

            var pending = _buffer.AsSpan(_start, _end - _start);
            var newline = pending.IndexOf((byte)'\n');
            var content = newline < 0 ? pending : pending[..newline];
            if (content.Length > 0)
            {
                Append(content);
                afterCr = content[^1] == '\r';
            }
            if (newline < 0)
            {
                _start = _end;
                continue;
            }

            if (!afterCr)
            {
                Append("\r"u8);
                bareLineFeeds = true;
            }
            Append("\n"u8);
            afterCr = false;
            lineStart = true;
            _start += newline + 1;
        }
    }

    public void Write(string text) => _output.Write(Utf8.GetBytes(text));

    public void Write(ReadOnlySpan<byte> bytes) => _output.Write(bytes);

    public void WriteLine(string line)
    {
        Write(line);
        _output.Write("\r\n"u8);
        Log?.FromServer(line);
    }

    public Task FlushAsync(CancellationToken ct) => FlushAsync(true, ct);

    public Task FlushIfIdleAsync(CancellationToken ct) => HasBufferedInput ? Task.CompletedTask : FlushAsync(true, ct);

    public Task FlushIfLargeAsync(CancellationToken ct) => _output.Length >= PendingOutputLimit ? FlushAsync(false, ct) : Task.CompletedTask;

    private async Task FlushAsync(bool shrink, CancellationToken ct)
    {
        if (_output.Length > 0)
        {
            await _stream.WriteAsync(_output.GetBuffer().AsMemory(0, (int)_output.Length), ct);
            await _stream.FlushAsync(ct);
            _output.SetLength(0);
        }
        if (shrink && _output.Capacity > PendingOutputLimit)
            _output.Capacity = BufferSize;
    }

    public async Task SendLineAsync(string line, CancellationToken ct)
    {
        WriteLine(line);
        await FlushAsync(ct);
    }

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _scanned = Math.Max(0, _scanned - _start);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buffer.Length)
            Array.Resize(ref _buffer, _buffer.Length * 2);

        var timeout = ArmReadTimeout(ct);
        int read;
        try
        {
            read = await _stream.ReadAsync(_buffer.AsMemory(_end), timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Tempo de inatividade esgotado");
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
        }
        if (read <= 0)
            return false;
        _end += read;
        return true;
    }

    private CancellationTokenSource ArmReadTimeout(CancellationToken ct)
    {
        if (_readTimeout is null || _readTimeout.IsCancellationRequested || _readTimeoutParent != ct)
        {
            _readTimeout?.Dispose();
            _readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _readTimeoutParent = ct;
        }
        _readTimeout.CancelAfter(IdleTimeout);
        return _readTimeout;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stream.DisposeAsync();
        }
        catch (IOException)
        {
        }
        _socket.Dispose();
        _readTimeout?.Dispose();
        _output.Dispose();
    }
}
