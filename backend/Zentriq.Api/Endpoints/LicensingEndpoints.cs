using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zentriq.Api.Infrastructure;
using Zentriq.Application.Common;
using Zentriq.Application.Licensing;
using Zentriq.Domain;
using Zentriq.Domain.Licensing;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Licensing;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Licensing APIs are deliberately NOT gated by license state: status, activation (online with a license key, or offline
/// with an activation request and a license file) and check-ins must keep working after a trial or license ends.
/// </summary>
public static class LicensingEndpoints
{
    public sealed record ImportBody(string Document);
    public sealed record ActivateBody(string? LicenseKey);
    public sealed record ServerBody(string? Url);

    public static void MapLicensingEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/licensing").WithTags("Licensing");

        g.MapGet("/status", async (LicensingService licensing, OnlineLicensing online, ZentriqDbContext db, LicenseVerifier verifier, CancellationToken ct) =>
        {
            var licensee = await OrganizationEndpoints.ActiveLicenseeAsync(db, verifier, ct);
            var status = await licensing.GetStatusAsync(ct);
            var usage = await licensing.GetUsageAsync(ct, status);
            var state = await db.InstallState.AsNoTracking().FirstOrDefaultAsync(ct);
            var trial = await db.Trial.AsNoTracking().FirstOrDefaultAsync(ct);
            var active = await db.LicenseRecords.AsNoTracking().Where(l => l.Status == "active")
                .Select(l => new { l.LicenseId, l.CustomerName, l.KeyId, l.IssuedAt, l.NotBefore, l.ExpiresAt, l.GraceDays, l.Modules, l.ImportedAt }).FirstOrDefaultAsync(ct);
            var revoked = active is null ? await db.LicenseRecords.AsNoTracking().Where(l => l.Status == "revoked").OrderByDescending(l => l.ImportedAt)
                .Select(l => new { l.LicenseId, l.CustomerName }).FirstOrDefaultAsync(ct) : null;
            var o = await online.LoadStateAsync(ct);
            return Results.Ok(new
            {
                mode = status.Mode.ToString(), status.Summary, status.Warning, status.ExpiresAt, status.DaysRemaining, status.FeaturesEnabled, status.Modules,
                edition = status.Edition, editionTitle = Editions.Title(status.Edition),
                licenseType = status.LicenseType, status.SupportUntil, activation = status.Activation, status.LeaseUntil,
                sections = ProductSections.All.Select(x => new { id = x, title = ProductSections.Titles[x], installed = status.InstalledSections.Contains(x), enabled = status.SectionEnabled(x), standard = ProductSections.Standard.Contains(x) }),
                limits = status.Limits, usage, installationId = state?.InstallationId,
                trial = trial is null ? null : new { trial.StartedAt, trial.ExpiresAt },
                license = active, revoked,
                licensee,
                online = new
                {
                    serverUrl = o.ServerUrl ?? await online.ServerUrlAsync(ct), o.Active, o.KeyHint, o.LicenseId, o.ActivatedAt, o.ActivatedBy,
                    o.LastAttemptAt, o.LastCheckinAt, o.LastStatus, o.LastMessage,
                },
                plans = new { trial = Plans.TrialLimits, free = Plans.FreeLimits, nonItPacks = Plans.NonItPacks, subscriptionYears = Plans.SubscriptionYears },
                releaseDate = ProductInfo.ReleaseDate,
            });
        }).RequireAuthorization();

        g.MapGet("/usage/history", async (ZentriqDbContext db, int? days, CancellationToken ct) =>
        {
            var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days ?? 90, 1, 730));
            var rows = await db.WorkloadUsageSnapshots.AsNoTracking().Where(s => s.TakenAt >= since).OrderBy(s => s.TakenAt)
                .Select(s => new { s.TakenAt, s.Workload, s.Billable, s.Entitled, s.Source }).ToListAsync(ct);
            return Results.Ok(rows);
        }).RequireAuthorization(Permissions.LicensingManage);

        // ---- online activation -------------------------------------------------------------------------------

        g.MapPost("/activate", async (ActivateBody body, OnlineLicensing online, RequestContext rc, CancellationToken ct) =>
        {
            var r = await online.ActivateAsync(body.LicenseKey ?? "", rc.UserId, rc.UserName, ct);
            if (!r.Ok) await rc.AuditAsync("license.activate_online", null, "Failure", new { reason = r.Message });
            return r.Ok ? Results.Ok(new { message = r.Message }) : Results.Problem(statusCode: r.Status, title: r.Message);
        }).RequireAuthorization(Permissions.LicensingManage);

        g.MapPost("/checkin", async (OnlineLicensing online, CancellationToken ct) =>
        {
            var r = await online.CheckinAsync("manual", ct);
            return r.Ok ? Results.Ok(new { message = r.Message }) : Results.Problem(statusCode: r.Status, title: r.Message);
        }).RequireAuthorization(Permissions.LicensingManage);

        g.MapPost("/deactivate", async (OnlineLicensing online, RequestContext rc, CancellationToken ct) =>
        {
            var r = await online.DeactivateAsync(rc.UserId, rc.UserName, ct);
            return r.Ok ? Results.Ok(new { message = r.Message }) : Results.Problem(statusCode: r.Status, title: r.Message);
        }).RequireAuthorization(Permissions.LicensingManage);

        g.MapPut("/server", async (ServerBody body, OnlineLicensing online, RequestContext rc, CancellationToken ct) =>
        {
            var url = body.Url?.Trim();
            if (string.IsNullOrEmpty(url)) url = LicenseServerProtocol.DefaultServerUrl;
            // https only; plain http is accepted for a license server on this same machine (local testing), never across the network.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !string.IsNullOrEmpty(u.Query) ||
                !(u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)))
                return Validation.Problem("url", "Enter the https address of the license server, e.g. https://license.zentriq.com (http only for http://localhost).");
            await online.SetServerUrlAsync(url, ct);
            await rc.AuditAsync("license.server", url);
            return Results.Ok(new { url });
        }).RequireAuthorization(Permissions.LicensingManage);

        // ---- offline activation (air-gapped) --------------------------------------------------------------------

        g.MapPost("/activation-request", async (ZentriqDbContext db, LicensingService licensing, RequestContext rc, ZentriqOptions options, IClock clock, CancellationToken ct) =>
        {
            var state = await db.InstallState.AsNoTracking().FirstOrDefaultAsync(ct);
            if (state?.InitializedAt is null) return Results.Problem(statusCode: 409, title: "Complete setup before requesting a license.");
            var org = await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).Select(o => o.Name).FirstOrDefaultAsync(ct) ?? "";
            var usage = await licensing.CountUsageAsync(null, null, ct);
            var req = new ActivationRequest(LicenseFormats.ActivationRequest, LicenseFormats.CurrentSchemaVersion, Guid.NewGuid(), state.InstallationId,
                options.ProductVersion, Environment.MachineName, org, clock.UtcNow, usage);
            var json = JsonSerializer.Serialize(req, new JsonSerializerOptions { WriteIndented = true });
            db.LicenseActivations.Add(new LicenseActivation { Id = Guid.NewGuid(), RequestId = req.RequestId, RequestedBy = rc.UserId, RequestedAt = clock.UtcNow, RequestDocument = json });
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("license.activation_request", req.RequestId.ToString());
            return Results.File(Encoding.UTF8.GetBytes(json), "application/json", $"zentriq-activation-request-{req.RequestId:N}.json");
        }).RequireAuthorization(Permissions.LicensingManage);

        g.MapPost("/import", async (ImportBody body, LicenseImporter importer, OnlineLicensing online, RequestContext rc, CancellationToken ct) =>
        {
            var r = await importer.ImportAsync(body.Document ?? "", rc.UserId, ct);
            if (r.Reason == nameof(LicenseRejection.ActivationRequired))
            {
                // The license file Zentriq sent: active only after the license server has validated it.
                var a = await online.ActivateWithFileAsync(body.Document!, rc.UserId, rc.UserName, ct);
                if (!a.Ok) await rc.AuditAsync("license.activate_file", null, "Failure", new { reason = a.Message });
                return a.Ok ? Results.Ok(new { imported = true, validated = true, message = a.Message })
                            : Results.Problem(statusCode: a.Status, title: a.Message, extensions: new Dictionary<string, object?> { ["reason"] = a.Status == 502 ? "LicenseServerUnreachable" : "ValidationFailed" });
            }
            if (!r.Ok)
            {
                await rc.AuditAsync("license.import", null, "Failure", new { reason = r.Reason ?? r.Message });
                return Results.Problem(statusCode: r.Status, title: r.Message, extensions: r.Reason is null ? null : new Dictionary<string, object?> { ["reason"] = r.Reason });
            }
            var p = r.Payload!;
            await rc.AuditAsync("license.import", p.LicenseId, detail: new { p.Customer, p.ExpiresAt, p.Entitlements, p.Modules, p.LicenseType, p.SupportUntil });
            return Results.Ok(new { imported = true, validated = false, message = "License imported.", p.LicenseId, p.Customer, p.ExpiresAt, p.Entitlements });
        }).RequireAuthorization(Permissions.LicensingManage);
    }
}
