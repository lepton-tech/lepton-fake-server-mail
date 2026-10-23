using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FakeServerEmail.Core;

public static class SingleInstance
{
    private const int MutexHashLength = 16;
    private static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static string NormalizeDirectory(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    public static string MutexName(string dataDirectory) =>
        @"Local\FakeServerEmail-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeDirectory(dataDirectory).ToLowerInvariant())))[..MutexHashLength];

    public static string RunningFile(string dataDirectory) => Path.Combine(dataDirectory, DataFiles.RunningInstance);

    public static string? ReadUrl(string dataDirectory)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(RunningFile(dataDirectory)));
            return doc.RootElement.GetProperty("url").GetString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    public static void WriteUrl(string dataDirectory, string url)
    {
        try
        {
            AtomicFile.WriteAllText(RunningFile(dataDirectory), JsonSerializer.Serialize(new { url, pid = Environment.ProcessId }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void Clear(string dataDirectory)
    {
        try
        {
            File.Delete(RunningFile(dataDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static async Task<bool> IsAliveAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var response = await Probe.GetAsync(url.TrimEnd('/').Replace("://localhost", "://127.0.0.1", StringComparison.OrdinalIgnoreCase) + "/api/status", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
