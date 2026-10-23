using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace FakeServerEmail.Tests;

internal static class McpTestExtensions
{
    public static async Task<JsonElement> CallJsonAsync(this McpClient client, string tool, Dictionary<string, object?>? args, CancellationToken ct) =>
        JsonElement.Parse(await client.CallTextAsync(tool, args, ct));

    public static async Task<string> CallTextAsync(this McpClient client, string tool, Dictionary<string, object?>? args, CancellationToken ct) =>
        string.Concat((await client.CallBlocksAsync(tool, args, ct)).OfType<TextContentBlock>().Select(t => t.Text));

    public static async Task<IList<ContentBlock>> CallBlocksAsync(this McpClient client, string tool, Dictionary<string, object?>? args, CancellationToken ct)
    {
        var result = await client.CallToolAsync(tool, args ?? [], cancellationToken: ct);
        Assert.False(result.IsError == true, $"{tool} retornou erro: {string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text))}");
        return result.Content;
    }

    public static async Task<string> CallErrorAsync(this McpClient client, string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
        Assert.True(result.IsError == true, $"{tool} deveria falhar");
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
    }
}
