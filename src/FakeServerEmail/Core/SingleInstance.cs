using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FakeServerEmail.Core;

public static class SingleInstance
{
    private static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static string MutexName(string dataDirectory) =>
        @"Local\FakeServerEmail-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory.ToLowerInvariant())))[..16];

    public static string RunningFile(string dataDirectory) => Path.Combine(dataDirectory, "em-execucao.json");

    public static string? ReadUrl(string dataDirectory)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(RunningFile(dataDirectory)));
            return doc.RootElement.GetProperty("url").GetString();
        }
        catch
        {
            return null;
        }
    }

    public static void WriteUrl(string dataDirectory, string url)
    {
        try
        {
            File.WriteAllText(RunningFile(dataDirectory), JsonSerializer.Serialize(new { url, pid = Environment.ProcessId }));
        }
        catch
        {
        }
    }

    public static void Clear(string dataDirectory)
    {
        try { File.Delete(RunningFile(dataDirectory)); } catch { }
    }

    public static async Task<bool> IsAliveAsync(string url)
    {
        try
        {
            using var response = await Probe.GetAsync(url.TrimEnd('/').Replace("://localhost", "://127.0.0.1") + "/api/status");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
