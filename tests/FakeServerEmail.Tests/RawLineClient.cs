using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FakeServerEmail.Tests;

public sealed class RawLineClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;
    private readonly CancellationToken _ct;

    private RawLineClient(TcpClient tcp, CancellationToken ct)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _reader = new StreamReader(_stream, Encoding.UTF8);
        _ct = ct;
    }

    public static async Task<RawLineClient> ConnectAsync(int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        return new RawLineClient(tcp, ct);
    }

    public async Task<string> ReadLineAsync() => await _reader.ReadLineAsync(_ct) ?? throw new IOException("conexão fechada pelo servidor");

    public async Task WriteAsync(string text) => await _stream.WriteAsync(Encoding.UTF8.GetBytes(text), _ct);

    public Task SendLineAsync(string line) => WriteAsync(line + "\r\n");

    public async Task<string> ReadSmtpReplyAsync()
    {
        var reply = new StringBuilder();
        while (true)
        {
            var line = await ReadLineAsync();
            reply.AppendLine(line);
            if (line.Length < 4 || line[3] != '-')
                return reply.ToString();
        }
    }

    public async Task<string> SmtpAsync(string command)
    {
        await SendLineAsync(command);
        return await ReadSmtpReplyAsync();
    }

    public async Task<List<string>> ImapAsync(string tag, string command)
    {
        await SendLineAsync($"{tag} {command}");
        return await ReadImapUntilTaggedAsync(tag);
    }

    public async Task<List<string>> ReadImapUntilTaggedAsync(string tag)
    {
        var lines = new List<string>();
        while (true)
        {
            var line = await ReadLineAsync();
            lines.Add(line);
            if (line.StartsWith(tag + " ", StringComparison.Ordinal))
                return lines;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }
}
