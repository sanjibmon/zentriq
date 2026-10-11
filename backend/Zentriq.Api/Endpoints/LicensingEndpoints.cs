using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
/// Licensing APIs are deliberately NOT gated by license state: status, activation-request export
/// and license import must keep working after a trial or license expires.
/// </summary>
public static class LicensingEndpoints
{
    public sealed record ImportBody(string Document);

    public static void MapLicensingEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/licensing").WithTags("Licensing");

        g.MapGet("/status", async (LicensingService licensing, ZentriqDbContext db, CancellationToken ct) =>
        {
            var status = await licensing.GetStatusAsync(ct);
            var usage = await licensing.GetUsageAsync(ct);
            var state = await db.InstallState.AsNoTracking().FirstOrDefaultAsync(ct);
            var trial = await db.Trial.AsNoTracking().FirstOrDefaultAsync(ct);
            var active = await db.LicenseRecords.AsNoTracking().Where(l => l.Status == "active")
                .Select(l => new { l.LicenseId, l.CustomerName, l.KeyId, l.IssuedAt, l.NotBefore, l.ExpiresAt, l.GraceDays, l.Modules, l.ImportedAt }).FirstOrDefaultAsync(ct);
            return Results.Ok(new
            {
                mode = status.Mode.ToString(), status.Summary, status.ExpiresAt, status.DaysRemaining, status.FeaturesEnabled, status.Modules,
                limits = status.Limits, usage, installationId = state?.InstallationId,
                trial = trial is null ? null : new { trial.StartedAt, trial.ExpiresAt },
                license = active,
            });
        }).RequireAuthorization();

        g.MapGet("/usage/history", async (ZentriqDbContext db, int? days, CancellationToken ct) =>
        {
            var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days ?? 90, 1, 730));
            var rows = await db.WorkloadUsageSnapshots.AsNoTracking().Where(s => s.TakenAt >= since).OrderBy(s => s.TakenAt)
                .Select(s => new { s.TakenAt, s.Workload, s.Billable, s.Entitled, s.Source }).ToListAsync(ct);
            return Results.Ok(rows);
        }).RequireAuthorization(Permissions.LicensingManage);

        g.MapPost("/activation-request", async (ZentriqDbContext db, LicensingService licensing, RequestContext rc, ZentriqOptions options, IClock clock, CancellationToken ct) =>
        {
            var state = await db.InstallState.AsNoTracking().FirstOrDefaultAsync(ct);
            if (state?.InitializedAt is null) return Results.Problem(statusCode: 409, title: "Complete setup before requesting a license.");
            var org = await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).Select(o => o.Name).FirstOrDefaultAsync(ct) ?? "";
            var usage = await licensing.CountBillableAsync(null, null, ct);
            var req = new ActivationRequest(LicenseFormats.ActivationRequest, LicenseFormats.CurrentSchemaVersion, Guid.NewGuid(), state.InstallationId,
                options.ProductVersion, Environment.MachineName, org, clock.UtcNow, Workloads.All.ToDictionary(w => w, w => usage.GetValueOrDefault(w)));
            var json = JsonSerializer.Serialize(req, new JsonSerializerOptions { WriteIndented = true });
            db.LicenseActivations.Add(new LicenseActivation { Id = Guid.NewGuid(), RequestId = req.RequestId, RequestedBy = rc.UserId, RequestedAt = clock.UtcNow, RequestDocument = json });
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("license.activation_request", req.RequestId.ToString());
            return Results.File(Encoding.UTF8.GetBytes(json), "application/json", $"zentriq-activation-request-{req.RequestId:N}.json");
        }).RequireAuthorization(Permissions.LicensingManage);

        g.MapPost("/import", async (ImportBody body, ZentriqDbContext db, LicenseVerifier verifier, RequestContext rc, IClock clock, CancellationToken ct) =>
        {
            var state = await db.InstallState.AsNoTracking().FirstOrDefaultAsync(ct);
            if (state?.InitializedAt is null) return Results.Problem(statusCode: 409, title: "Complete setup before importing a license.");
            var v = verifier.Verify(body.Document ?? "", state.InstallationId, clock.UtcNow);
            if (!v.IsValid)
            {
                await rc.AuditAsync("license.import", null, "Failure", new { reason = v.Rejection.ToString() });
                return Results.Problem(statusCode: 422, title: v.Message, extensions: new Dictionary<string, object?> { ["reason"] = v.Rejection.ToString() });
            }
            var p = v.Payload!;
            if (await db.LicenseRecords.AnyAsync(l => l.LicenseId == p.LicenseId, ct))
                return Results.Problem(statusCode: 409, title: "This license has already been imported.");

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.LicenseRecords.Where(l => l.Status == "active").ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "superseded"), ct);
            var rec = new LicenseRecord
            {
                Id = Guid.NewGuid(), LicenseId = p.LicenseId, SchemaVersion = p.SchemaVersion, KeyId = JsonDocument.Parse(body.Document!).RootElement.GetProperty("keyId").GetString()!,
                CustomerName = p.Customer, InstallationId = p.InstallationId, IssuedAt = p.IssuedAt.ToUniversalTime(), NotBefore = p.NotBefore.ToUniversalTime(),
                ExpiresAt = p.ExpiresAt.ToUniversalTime(), GraceDays = p.GraceDays, Modules = p.Modules.ToArray(), RawDocument = body.Document!, Status = "active",
                ImportedBy = rc.UserId, ImportedAt = clock.UtcNow,
                Entitlements = p.Entitlements.Select(e => new LicenseEntitlement { Workload = e.Key, Quantity = e.Value }).ToList(),
            };
            db.LicenseRecords.Add(rec);
            if (p.RequestId is { } reqId)
                await db.LicenseActivations.Where(a => a.RequestId == reqId).ExecuteUpdateAsync(s => s.SetProperty(a => a.FulfilledByLicense, p.LicenseId), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await rc.AuditAsync("license.import", p.LicenseId, detail: new { p.Customer, p.ExpiresAt, p.Entitlements, p.Modules });
            return Results.Ok(new { imported = true, p.LicenseId, p.Customer, p.ExpiresAt, p.Entitlements });
        }).RequireAuthorization(Permissions.LicensingManage);
    }
}
