using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using FakeServerEmail.Core;

namespace FakeServerEmail.Protocols;

public interface IProtocolHandler
{
    string Protocol { get; }

    Task HandleAsync(MailConnection connection, CancellationToken ct);
}

public sealed class TcpService(string name, int configuredPort, bool implicitTls, IProtocolHandler handler, X509Certificate2 certificate, SessionLog log) : IDisposable
{
    private const int Backlog = 128;
    private const int PrivilegedPortLimit = 1024;
    private const int PrivilegedPortOffset = 2000;
    private const int AlternativePortCount = 30;
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly List<Socket> _listeners = [];
    private CancellationTokenSource? _cts;
    private int _active;

    public string Name { get; } = name;
    public string Protocol => handler.Protocol;
    public int ConfiguredPort { get; } = configuredPort;
    public bool ImplicitTls { get; } = implicitTls;
    public int? ActualPort { get; private set; }
    public string? Error { get; private set; }
    public bool Running => ActualPort is not null;
    public int ActiveConnections => _active;

    public static IEnumerable<int> CandidatePorts(int configured)
    {
        if (configured is < 1 or > IPEndPoint.MaxPort)
            yield break;
        yield return configured;
        var alternative = configured < PrivilegedPortLimit ? PrivilegedPortOffset + configured : configured + 1;
        for (var i = 0; i < AlternativePortCount; i++)
        {
            var port = alternative + i;
            if (port <= IPEndPoint.MaxPort && port != configured)
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
        if (ConfiguredPort > IPEndPoint.MaxPort)
        {
            Error = $"Porta {ConfiguredPort} inválida; use um número de 1 a {IPEndPoint.MaxPort}";
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
        _cts?.Dispose();
        _cts = null;
        CloseListeners();
        ActualPort = null;
    }

    public void Dispose() => Stop();

    private void Bind(int port, bool allowNetwork)
    {
        if (allowNetwork)
        {
            Socket socket;
            try
            {
                socket = Listen(AddressFamily.InterNetworkV6, IPAddress.IPv6Any, port, dualMode: true);
            }
            catch (SocketException) when (Socket.OSSupportsIPv4)
            {
                socket = Listen(AddressFamily.InterNetwork, IPAddress.Any, port);
            }
            _listeners.Add(socket);
            return;
        }

        _listeners.Add(Listen(AddressFamily.InterNetwork, IPAddress.Loopback, port));
        if (!Socket.OSSupportsIPv6)
            return;
        try
        {
            _listeners.Add(Listen(AddressFamily.InterNetworkV6, IPAddress.IPv6Loopback, port));
        }
        catch (SocketException ex) when (ex.SocketErrorCode is not (SocketError.AddressAlreadyInUse or SocketError.AccessDenied))
        {
        }
    }

    private static Socket Listen(AddressFamily family, IPAddress address, int port, bool dualMode = false)
    {
        var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        try
        {
            if (dualMode)
                socket.DualMode = true;
            socket.Bind(new IPEndPoint(address, port));
            socket.Listen(Backlog);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private void CloseListeners()
    {
        foreach (var listener in _listeners)
            listener.Dispose();
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
                try
                {
                    await Task.Delay(AcceptRetryDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken ct)
    {
        Interlocked.Increment(ref _active);
        MailConnection? connection = null;
        ProtocolSession? session = null;
        try
        {
            client.NoDelay = true;
            connection = new MailConnection(client, certificate, ActualPort ?? 0);
            session = log.Start(Protocol, ActualPort ?? 0, connection.RemoteAddress, ImplicitTls);
            connection.Log = session;
            session.Info($"Conexão recebida de {connection.RemoteAddress} na porta {ActualPort} ({Name})");
            if (ImplicitTls)
                await connection.UpgradeToTlsAsync(ct);
            await handler.HandleAsync(connection, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session?.Info("Servidor encerrado");
        }
        catch (TimeoutException ex)
        {
            session?.Info(ex.Message);
        }
        catch (LineTooLongException ex)
        {
            Fail(session, ex.Message, "O cliente enviou uma linha longa demais");
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException)
        {
            Fail(session, ex.Message, "Conexão interrompida: " + ex.Message);
        }
        catch (Exception ex)
        {
            Fail(session, ex.Message, "Erro interno: " + ex);
        }
        finally
        {
            if (connection is not null)
                await connection.DisposeAsync();
            else
                client.Dispose();
            session?.Info("Conexão encerrada");
            session?.End();
            Interlocked.Decrement(ref _active);
        }
    }

    private static void Fail(ProtocolSession? session, string error, string text)
    {
        if (session is null)
            return;
        session.Error = error;
        session.Info(text);
    }
}
