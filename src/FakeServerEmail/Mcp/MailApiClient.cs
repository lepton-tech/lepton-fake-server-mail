using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol;

namespace FakeServerEmail.Mcp;

public sealed class MailApiClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string DefaultNotFound = "Item não encontrado (o id está correto? use list_messages para ver os ids).";
    private const string NotRunning = "O Fake Server Email não está respondendo. Abra o FakeServerEmail.exe e tente de novo.";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http = new() { Timeout = RequestTimeout };
    private readonly Func<CancellationToken, Task<string?>>? _recover;
    private string _baseUrl;

    public MailApiClient(string baseUrl, Func<CancellationToken, Task<string?>>? recover = null)
    {
        _baseUrl = Normalize(baseUrl);
        _recover = recover;
    }

    public string BaseUrl => _baseUrl;

    public async Task<T> GetAsync<T>(string path, CancellationToken ct, string? notFound = null)
    {
        using var response = await SendCoreAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct);
        await EnsureAsync(response, notFound, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    public async Task<string?> GetStringAsync(string path, CancellationToken ct)
    {
        using var response = await SendCoreAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureAsync(response, null, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<(byte[] Data, string ContentType)?> GetBytesAsync(string path, CancellationToken ct)
    {
        using var response = await SendCoreAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureAsync(response, null, ct);
        return (await response.Content.ReadAsByteArrayAsync(ct), response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }

    public async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string? notFound = null)
    {
        using var response = await SendCoreAsync(() => CreateRequest(method, path, body), ct);
        await EnsureAsync(response, notFound, ct);
    }

    public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendCoreAsync(() => CreateRequest(method, path, body), ct);
        await EnsureAsync(response, null, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    public void Dispose() => _http.Dispose();

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        return request;
    }

    private async Task<HttpResponseMessage> SendCoreAsync(Func<HttpRequestMessage> create, CancellationToken ct)
    {
        try
        {
            return await SendOnceAsync(create, ct);
        }
        catch (HttpRequestException) when (_recover is not null)
        {
            var url = await _recover(ct) ?? throw new McpException(NotRunning);
            _baseUrl = Normalize(url);
            try
            {
                return await SendOnceAsync(create, ct);
            }
            catch (HttpRequestException ex)
            {
                throw Unreachable(ex);
            }
        }
        catch (HttpRequestException ex)
        {
            throw Unreachable(ex);
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Func<HttpRequestMessage> create, CancellationToken ct)
    {
        using var request = create();
        request.RequestUri = new Uri(new Uri(_baseUrl), request.RequestUri!.OriginalString);
        try
        {
            return await _http.SendAsync(request, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new McpException($"O Fake Server Email não respondeu em {RequestTimeout.TotalSeconds:0} segundos.", ex);
        }
    }

    private McpException Unreachable(HttpRequestException ex) => new($"Não foi possível falar com o Fake Server Email em {_baseUrl}: {ex.Message}", ex);

    private static async Task EnsureAsync(HttpResponseMessage response, string? notFound, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new McpException(notFound ?? DefaultNotFound);
        var detail = "";
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("detail", out var d))
                detail = d.GetString() ?? "";
        }
        catch (JsonException)
        {
        }
        throw new McpException(detail.Length > 0 ? detail : $"O servidor respondeu {(int)response.StatusCode}.");
    }

    private static string Normalize(string url) => url.TrimEnd('/').Replace("://localhost", "://127.0.0.1", StringComparison.OrdinalIgnoreCase);
}
