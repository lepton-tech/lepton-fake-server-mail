using FakeServerEmail.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FakeServerEmail.Web;

public static partial class ApiEndpoints
{
    private static void MapStatusAndSettings(RouteGroupBuilder api)
    {
        api.MapGet("/status", (FakeMailServer server) => TypedResults.Ok(server.Status()));
        api.MapGet("/settings", (SettingsStore settings) => TypedResults.Ok(settings.Current));

        api.MapPut("/settings", IResult (AppSettings changed, SettingsStore settings, FakeMailServer server) =>
        {
            if (PortSetting.All.FirstOrDefault(p => !p.IsValid(p.Get(changed))) is { } invalid)
                return TypedResults.Problem(invalid.InvalidMessage, statusCode: StatusCodes.Status400BadRequest);

            var before = settings.Current;
            var saved = settings.Update(changed);
            var after = settings.Current;
            var networkChanged = before.AllowNetworkAccess != after.AllowNetworkAccess;
            var servicesChanged = networkChanged || PortSetting.All.Any(p => !p.IsWeb && p.Get(before) != p.Get(after));
            var webChanged = networkChanged || PortSetting.All.Any(p => p.IsWeb && p.Get(before) != p.Get(after));
            if (servicesChanged)
                server.RestartProtocolServices();
            if (!saved)
                return TypedResults.Problem("As configurações valem até fechar o programa, mas não foi possível gravá-las em disco.", statusCode: StatusCodes.Status500InternalServerError);
            return TypedResults.Ok(new SettingsUpdateResult(saved, servicesChanged, webChanged, server.Status()));
        });
    }
}
