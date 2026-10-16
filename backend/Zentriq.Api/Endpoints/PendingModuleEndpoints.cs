namespace Zentriq.Api.Endpoints;

/// <summary>
/// Modules present in the prototype but not implemented in this build. They answer 501 with an
/// explicit message rather than returning simulated data.
/// </summary>
public static class PendingModuleEndpoints
{
    private static readonly (string Path, string Name)[] Pending =
    [
        ("/patch", "Patch management"),
        ("/deployment", "Software deployment"),
        ("/cloud", "Cloud & infrastructure connectors"),
    ];

    public static void MapPendingModuleEndpoints(this RouteGroupBuilder api)
    {
        foreach (var (path, name) in Pending)
        {
            api.MapMethods(path + "/{**rest}", ["GET", "POST", "PUT", "PATCH", "DELETE"], () =>
                Results.Problem(statusCode: 501, title: $"{name} is not available in this build.",
                    type: "https://zentriq.local/problems/module-not-implemented"))
                .RequireAuthorization().WithTags("Not yet available");
        }
    }
}
