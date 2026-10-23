using System.Threading.Channels;
using FakeServerEmail.Core;

namespace FakeServerEmail.Hosting;

public sealed class ActivityPrinter : IAsyncDisposable
{
    private const int Backlog = 500;
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(1);

    private readonly SessionLog _log;
    private readonly Channel<(DateTime Time, string Text)> _lines = Channel.CreateBounded<(DateTime, string)>(
        new BoundedChannelOptions(Backlog) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _printer;

    private ActivityPrinter(SessionLog log)
    {
        _log = log;
        _log.Activity += OnActivity;
        _printer = Task.Run(PrintAsync);
    }

    public static ActivityPrinter Start(SessionLog log) => new(log);

    public async ValueTask DisposeAsync()
    {
        _log.Activity -= OnActivity;
        _lines.Writer.TryComplete();
        await _printer.WaitAsync(DrainTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private void OnActivity(string text) => _lines.Writer.TryWrite((DateTime.Now, text));

    private async Task PrintAsync()
    {
        await foreach (var (time, text) in _lines.Reader.ReadAllAsync())
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{time:HH:mm:ss}] ");
            Console.ResetColor();
            Console.WriteLine(text);
        }
    }
}
