using Microsoft.EntityFrameworkCore;
using Zentriq.Api.Infrastructure;
using Zentriq.Application.Licensing;
using Zentriq.Domain;
using Zentriq.Domain.Licensing;
using Zentriq.Infrastructure.Data;

namespace Zentriq.Api.Endpoints;

/// <summary>Company (customer) profile. Editable by administrators; schema-2 licenses also carry signed licensee details.</summary>
public static class OrganizationEndpoints
{
    public sealed record OrganizationBody(
        string Name, string? LegalName, string? AddressLine1, string? AddressLine2, string? City, string? StateRegion,
        string? PostalCode, string? Country, string? TaxId, string? Website, string? ContactName, string? ContactEmail, string? ContactPhone);

    public static void MapOrganizationEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/organization").WithTags("Company profile");

        g.MapGet("", async (ZentriqDbContext db, LicenseVerifier verifier, CancellationToken ct) =>
        {
            var org = await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).FirstOrDefaultAsync(ct);
            var licensee = await ActiveLicenseeAsync(db, verifier, ct);
            return Results.Ok(new { profile = org is null ? null : ToDto(org), licensee });
        }).RequireAuthorization();

        g.MapPut("", async (OrganizationBody body, ZentriqDbContext db, RequestContext rc, CancellationToken ct) =>
        {
            var err = Validate(body);
            if (err is not null) return Validation.Problem(err.Value.Field, err.Value.Message);
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstOrDefaultAsync(ct);
            if (org is null) return Results.Problem(statusCode: 409, title: "Complete setup first.");
            org.Name = body.Name.Trim();
            org.LegalName = T(body.LegalName); org.AddressLine1 = T(body.AddressLine1); org.AddressLine2 = T(body.AddressLine2);
            org.City = T(body.City); org.StateRegion = T(body.StateRegion); org.PostalCode = T(body.PostalCode); org.Country = T(body.Country);
            org.TaxId = T(body.TaxId); org.Website = T(body.Website); org.ContactName = T(body.ContactName);
            org.ContactEmail = T(body.ContactEmail); org.ContactPhone = T(body.ContactPhone); org.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("organization.update", org.Name);
            return Results.Ok(ToDto(org));
        }).RequireAuthorization(Permissions.SettingsManage);
    }

    public static async Task<Licensee?> ActiveLicenseeAsync(ZentriqDbContext db, LicenseVerifier verifier, CancellationToken ct)
    {
        var rec = await db.LicenseRecords.AsNoTracking().FirstOrDefaultAsync(l => l.Status == "active", ct);
        var state = await db.InstallState.AsNoTracking().FirstOrDefaultAsync(ct);
        if (rec is null || state is null) return null;
        var v = verifier.Verify(rec.RawDocument, state.InstallationId, DateTimeOffset.UtcNow, enforceValidityWindow: false);
        return v.IsValid ? v.Payload!.Licensee ?? new Licensee(v.Payload.Customer) : null;
    }

    /// <summary>Fills empty profile fields from the signed licensee block (never overwrites administrator edits).</summary>
    public static void ApplyLicensee(Organization org, Licensee l)
    {
        org.LegalName ??= l.Company;
        org.ContactName ??= l.ContactName;
        org.ContactEmail ??= l.ContactEmail;
        org.ContactPhone ??= l.ContactPhone;
        org.AddressLine1 ??= l.Address;
        org.Country ??= l.Country;
        org.TaxId ??= l.TaxId;
        org.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static object ToDto(Organization o) => new
    {
        o.Id, o.Name, o.LegalName, o.AddressLine1, o.AddressLine2, o.City, o.StateRegion, o.PostalCode, o.Country,
        o.TaxId, o.Website, o.ContactName, o.ContactEmail, o.ContactPhone, o.UpdatedAt,
    };

    private static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static (string Field, string Message)? Validate(OrganizationBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Length > 200) return ("name", "Company name is required (max 200 characters).");
        foreach (var (f, v, max) in new[] { ("legalName", b.LegalName, 200), ("addressLine1", b.AddressLine1, 200), ("addressLine2", b.AddressLine2, 200),
                     ("city", b.City, 100), ("stateRegion", b.StateRegion, 100), ("postalCode", b.PostalCode, 20), ("country", b.Country, 100),
                     ("taxId", b.TaxId, 50), ("website", b.Website, 200), ("contactName", b.ContactName, 200), ("contactEmail", b.ContactEmail, 254), ("contactPhone", b.ContactPhone, 40) })
            if (v is { Length: var n } && n > max) return (f, $"Maximum {max} characters.");
        if (!string.IsNullOrWhiteSpace(b.ContactEmail) && !System.Text.RegularExpressions.Regex.IsMatch(b.ContactEmail.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            return ("contactEmail", "Enter a valid e-mail address.");
        if (!string.IsNullOrWhiteSpace(b.Website) && !Uri.TryCreate(b.Website.Trim(), UriKind.Absolute, out _) && !b.Website.Contains('.'))
            return ("website", "Enter a valid web address.");
        return null;
    }
}
