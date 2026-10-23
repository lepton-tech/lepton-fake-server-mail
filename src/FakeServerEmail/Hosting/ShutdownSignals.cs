using System.Runtime.InteropServices;

namespace FakeServerEmail.Hosting;

public sealed class ShutdownSignals : IDisposable
{
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(4);

    private readonly FakeMailServer _server;
    private readonly ManualResetEventSlim _stopped = new();
    private readonly PosixSignalRegistration[] _registrations;

    public ShutdownSignals(FakeMailServer server)
    {
        _server = server;
        _registrations =
        [
            PosixSignalRegistration.Create(PosixSignal.SIGINT, Cancel),
            PosixSignalRegistration.Create(PosixSignal.SIGQUIT, Cancel),
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => StopAndWait()),
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => StopAndWait()),
        ];
    }

    public void MarkStopped() => _stopped.Set();

    public void Dispose()
    {
        foreach (var registration in _registrations)
            registration.Dispose();
        _stopped.Dispose();
    }

    private void Cancel(PosixSignalContext context)
    {
        context.Cancel = true;
        _server.RequestShutdown();
    }

    private void StopAndWait()
    {
        _server.Context.Store.Flush();
        _server.RequestShutdown();
        _stopped.Wait(CloseWait);
    }
}
