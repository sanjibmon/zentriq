using Microsoft.EntityFrameworkCore;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Licensing;
using Zentriq.Infrastructure.Setup;

namespace Zentriq.Api.Endpoints;

public static class SetupEndpoints
{
    public sealed record InitializeBody(string SetupToken, string OrganizationName, string AdminEmail, string AdminDisplayName, string AdminPassword);

    public static void MapSetupEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/setup").WithTags("Setup");

        g.MapGet("/status", async (ZentriqDbContext db, LicensingService licensing, CancellationToken ct) =>
        {
            var state = await db.InstallState.AsNoTracking().FirstOrDefaultAsync(ct);
            var lic = await licensing.GetStatusAsync(ct);
            return Results.Ok(new
            {
                installed = state is not null,
                initialized = state?.InitializedAt is not null,
                productVersion = state?.ProductVersion,
                licenseMode = lic.Mode.ToString(),
                trialExpiresAt = lic.Mode is Zentriq.Domain.Licensing.LicenseMode.Trial or Zentriq.Domain.Licensing.LicenseMode.TrialExpired ? lic.ExpiresAt : null,
            });
        }).AllowAnonymous();

        // Called by the installer (zentriqctl) with the one-time setup token it issued.
        g.MapPost("/initialize", async (InitializeBody body, InitializationService init, HttpContext http, CancellationToken ct) =>
        {
            var r = await init.InitializeAsync(new InitializeRequest(body.SetupToken, body.OrganizationName, body.AdminEmail, body.AdminDisplayName, body.AdminPassword),
                http.Connection.RemoteIpAddress?.ToString(), ct);
            return r.Outcome switch
            {
                InitializeOutcome.Initialized => Results.Ok(new { outcome = "initialized", r.TrialStartedAt, r.TrialExpiresAt }),
                InitializeOutcome.AlreadyInitialized => Results.Ok(new { outcome = "already-initialized", r.TrialStartedAt, r.TrialExpiresAt }),
                InitializeOutcome.InvalidToken => Results.Problem(statusCode: 403, title: r.Message),
                _ => Results.Problem(statusCode: 400, title: r.Message),
            };
        }).AllowAnonymous().RequireRateLimiting("login");
    }
}
