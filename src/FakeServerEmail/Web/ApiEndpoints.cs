using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FakeServerEmail.Web;

public static partial class ApiEndpoints
{
    private const string SmtpProtocol = "SMTP";

    public static void MapFakeMailApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(RejectCrossSiteWritesAsync);
        MapStatusAndSettings(api);
        MapMessages(api);
        MapSessions(api);
        MapOAuthTokens(api);
        MapCertificate(api);
        MapData(api);
        MapEvents(api);
    }

    private static async ValueTask<object?> RejectCrossSiteWritesAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && IsCrossSite(request))
            return TypedResults.Problem("Pedido recusado: ele veio de outra página aberta no navegador, e não da tela do Fake Server Email.", statusCode: StatusCodes.Status403Forbidden);
        return await next(context);
    }

    private static bool IsCrossSite(HttpRequest request)
    {
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0)
            return site is not ("same-origin" or "none");
        var origin = request.Headers.Origin.ToString();
        return origin.Length > 0
            && !(Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FolderOrAll(string? folder) => string.IsNullOrEmpty(folder) || folder == "*" ? null : folder;
}
