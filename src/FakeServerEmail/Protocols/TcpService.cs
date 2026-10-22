using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using FakeServerEmail.Core;

namespace FakeServerEmail.Protocols;

public interface IProtocolHandler
{
    string Protocol { get; }

    Task HandleAsync(MailConnection connection, CancellationToken ct);
}

public sealed class TcpService
{
    private readonly List<Socket> _listeners = [];
    private readonly IProtocolHandler _handler;
    private readonly X509Certificate2 _certificate;
    private readonly SessionLog _log;
    private CancellationTokenSource? _cts;

    public TcpService(string name, int configuredPort, bool implicitTls, IProtocolHandler handler, X509Certificate2 certificate, SessionLog log)
    {
        Name = name;
        ConfiguredPort = configuredPort;
        ImplicitTls = implicitTls;
        _handler = handler;
        _certificate = certificate;
        _log = log;
    }

    public string Name { get; }
    public string Protocol => _handler.Protocol;
    public int ConfiguredPort { get; }
    public bool ImplicitTls { get; }
    public int? ActualPort { get; private set; }
    public string? Error { get; private set; }
    public bool Running => ActualPort is not null;
    public int ActiveConnections => _active;

    private int _active;

    public static IEnumerable<int> CandidatePorts(int configured)
    {
        if (configured <= 0)
            yield break;
        yield return configured;
        var alternative = configured < 1024 ? 2000 + configured : configured + 1;
        for (var i = 0; i < 30; i++)
        {
            var port = alternative + i;
            if (port is > 0 and < 65536 && port != configured)
                yield return port;
        }
    }

    public void Start(bool allowNetwork)
    {
        if (ConfiguredPort <= 0)
        {
            Error = "Desativado (porta 0)";
            return;
        }

        var failures = new List<string>();
        foreach (var port in CandidatePorts(ConfiguredPort))
        {
            try
            {
                Bind(port, allowNetwork);
                ActualPort = port;
                Error = port == ConfiguredPort ? null : $"Porta {ConfiguredPort} ocupada; usando {port}";
                break;
            }
            catch (SocketException ex)
            {
                CloseListeners();
                failures.Add($"{port}: {ex.SocketErrorCode}");
            }
        }

        if (ActualPort is null)
        {
            Error = "Não foi possível abrir nenhuma porta (" + string.Join(", ", failures.Take(3)) + ")";
            return;
        }

        _cts = new CancellationTokenSource();
        foreach (var listener in _listeners)
            _ = AcceptLoopAsync(listener, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        CloseListeners();
        ActualPort = null;
    }

    private void Bind(int port, bool allowNetwork)
    {
        if (allowNetwork)
        {
            Socket socket;
            try
            {
                socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            }
            catch (SocketException) when (Socket.OSSupportsIPv4)
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            socket.Listen(128);
            _listeners.Add(socket);
            return;
        }

        var v4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        try
        {
            v4.Bind(new IPEndPoint(IPAddress.Loopback, port));
            v4.Listen(128);
        }
        catch
        {
            v4.Dispose();
            throw;
        }
        _listeners.Add(v4);

        if (Socket.OSSupportsIPv6)
        {
            var v6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            try
            {
                v6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
                v6.Listen(128);
                _listeners.Add(v6);
            }
            catch (SocketException)
            {
                v6.Dispose();
            }
        }
    }

    private void CloseListeners()
    {
        foreach (var l in _listeners)
        {
            try { l.Dispose(); } catch { }
        }
        _listeners.Clear();
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            client.NoDelay = true;
            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken ct)
    {
        Interlocked.Increment(ref _active);
        var connection = new MailConnection(client, _certificate, ActualPort ?? 0);
        var session = _log.Start(Protocol, ActualPort ?? 0, connection.RemoteAddress, ImplicitTls);
        connection.Log = session;
        session.Info($"Conexão recebida de {connection.RemoteAddress} na porta {ActualPort} ({Name})");
        try
        {
            if (ImplicitTls)
                await connection.UpgradeToTlsAsync(ct);
            await _handler.HandleAsync(connection, ct);
        }
        catch (OperationCanceledException)
        {
            session.Info("Servidor encerrado");
        }
        catch (TimeoutException ex)
        {
            session.Info(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException)
        {
            session.Error = ex.Message;
            session.Info("Conexão interrompida: " + ex.Message);
        }
        catch (Exception ex)
        {
            session.Error = ex.Message;
            session.Info("Erro interno: " + ex);
        }
        finally
        {
            await connection.DisposeAsync();
            session.Info("Conexão encerrada");
            session.End();
            Interlocked.Decrement(ref _active);
        }
    }
}
