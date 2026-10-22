using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol;

namespace FakeServerEmail.Mcp;

public sealed class MailApiClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly Func<Task<string?>>? _recover;
    private string _baseUrl;

    public MailApiClient(string baseUrl, Func<Task<string?>>? recover = null)
    {
        _baseUrl = Normalize(baseUrl);
        _recover = recover;
    }

    public string BaseUrl => _baseUrl;

    public async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    public async Task<string?> GetStringAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<(byte[] Data, string ContentType)?> GetBytesAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureAsync(response, ct);
        return (await response.Content.ReadAsByteArrayAsync(ct), response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }

    public async Task<JsonElement?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, path);
            if (body is not null)
                request.Content = JsonContent.Create(body, options: Json);
            return request;
        }, ct);
        await EnsureAsync(response, ct);
        if (response.Content.Headers.ContentLength == 0 || response.StatusCode == HttpStatusCode.NoContent)
            return null;
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(WithBase(create()), ct);
        }
        catch (HttpRequestException) when (_recover is not null)
        {
            var url = await _recover();
            if (url is null)
                throw new McpException("O Fake Server Email não está respondendo. Abra o FakeServerEmail.exe e tente de novo.");
            _baseUrl = Normalize(url);
            return await _http.SendAsync(WithBase(create()), ct);
        }
        catch (HttpRequestException ex)
        {
            throw new McpException($"Não foi possível falar com o Fake Server Email em {_baseUrl}: {ex.Message}");
        }
    }

    private HttpRequestMessage WithBase(HttpRequestMessage request)
    {
        request.RequestUri = new Uri(new Uri(_baseUrl), request.RequestUri!.OriginalString);
        return request;
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
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
        throw new McpException(response.StatusCode == HttpStatusCode.NotFound
            ? "Item não encontrado (o id está correto? use list_messages para ver os ids)."
            : $"O servidor respondeu {(int)response.StatusCode}: {detail}".TrimEnd(' ', ':'));
    }

    private static string Normalize(string url) => url.TrimEnd('/').Replace("://localhost", "://127.0.0.1", StringComparison.OrdinalIgnoreCase);
}
