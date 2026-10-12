namespace Zentriq.Api.Endpoints;

/// <summary>
/// Modules present in the prototype but not implemented in this build. They answer 501 with an
/// explicit message rather than returning simulated data.
/// </summary>
public static class PendingModuleEndpoints
{
    private static readonly (string Path, string Name)[] Pending =
    [
        ("/reports", "Report designer, scheduled reports and XLSX/PDF exports"),
        ("/patch", "Patch management"),
        ("/vulnerabilities", "Vulnerability management"),
        ("/configuration", "Security configuration assessment"),
        ("/deployment", "Software deployment"),
        ("/remote", "Remote support"),
        ("/cloud", "Cloud & infrastructure connectors"),
        ("/integrations", "Integrations & automation"),
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
