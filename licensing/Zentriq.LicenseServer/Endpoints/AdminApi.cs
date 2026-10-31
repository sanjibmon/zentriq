using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Npgsql;
using QRCoder;
using Zentriq.Application.Security;
using Zentriq.Domain;
using Zentriq.Domain.Licensing;
using Zentriq.LicenseServer.Services;

namespace Zentriq.LicenseServer.Endpoints;

/// <summary>License administration portal API (/admin/api). Cookie sign-in; every change is written to the audit log.</summary>
public static class AdminApi
{
    public const string CookieScheme = "zls";
    public const string CsrfHeader = "X-ZLS-Request";

    public sealed record LoginBody(string? Email, string? Password, string? Code);
    public sealed record CustomerBody(string? Company, string? ContactName, string? ContactEmail, string? ContactPhone, string? Address, string? Country, string? TaxId, string? Notes);
    public sealed record LicenseBody(
        Guid? CustomerId, string? Edition, string? LicenseType, int? TermYears, int? SupportYears, DateTimeOffset? StartsAt,
        int? Devices, int? NonItPack, int? CloudInstances, int? Technicians, string[]? ExtraModules, int? MaxActivations, int? GraceDays, string? Notes);
    public sealed record YearsBody(int? Years);
    public sealed record StatusBody(string? Status, string? Reason);
    public sealed record OfflineIssueBody(JsonElement Request);
    public sealed record VerifyBody(string? Document);
    public sealed record AdminBody(string? Email, string? Name, string? Password, bool? Owner);
    public sealed record PasswordBody(string? Current, string? Password);
    public sealed record CodeBody(string? Code, string? Secret);

    private static Guid Me(ClaimsPrincipal u) => Guid.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static string Actor(ClaimsPrincipal u) => u.FindFirstValue(ClaimTypes.Email) ?? "admin";
    private static IResult Bad(string field, string message) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }, title: message);
    private static string? Trim(string? s, int max = 500) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

    public static void MapAdminApi(this WebApplication app)
    {
        var open = app.MapGroup("/admin/api").DisableAntiforgery();
        var g = app.MapGroup("/admin/api").RequireAuthorization().DisableAntiforgery()
            .AddEndpointFilter(async (ctx, next) =>
            {
                // CSRF: state-changing calls must come from the portal's own script (custom header + SameSite=Strict cookie).
                var req = ctx.HttpContext.Request;
                if (!HttpMethods.IsGet(req.Method) && !HttpMethods.IsHead(req.Method) && req.Headers[CsrfHeader] != "1")
                    return Results.Problem(statusCode: 403, title: "Missing request header.");
                return await next(ctx);
            });

        // ---- sign-in -------------------------------------------------------------------------------------------

        open.MapPost("/login", async (LoginBody body, AdminStore admins, Audit audit, HttpContext http, CancellationToken ct) =>
        {
            if (http.Request.Headers[CsrfHeader] != "1") return Results.Problem(statusCode: 403, title: "Missing request header.");
            var (outcome, a) = await admins.LoginAsync(body.Email, body.Password, body.Code, ct);
            switch (outcome)
            {
                case AdminStore.LoginOutcome.NeedCode: return Results.Ok(new { needCode = true });
                case AdminStore.LoginOutcome.Locked:
                    await audit.WriteAsync(body.Email ?? "?", "admin.login_locked", a?.Id.ToString(), PublicApi.Ip(http), null, ct);
                    return Results.Problem(statusCode: 429, title: "Too many failed sign-ins. Try again in 15 minutes.");
                case AdminStore.LoginOutcome.Invalid or AdminStore.LoginOutcome.BadCode:
                    await audit.WriteAsync(body.Email ?? "?", "admin.login_failed", a?.Id.ToString(), PublicApi.Ip(http), new { outcome = outcome.ToString() }, ct);
                    return Results.Problem(statusCode: 401, title: outcome == AdminStore.LoginOutcome.BadCode ? "The authenticator code is not correct." : "The email or password is not correct.");
            }
            await http.SignInAsync(CookieScheme, AdminStore.Principal(a!));
            await audit.WriteAsync(a!.Email, "admin.login", a.Id.ToString(), PublicApi.Ip(http), null, ct);
            return Results.Ok(a.View());
        }).RequireRateLimiting(PublicApi.RatePolicy);

        g.MapPost("/logout", async (HttpContext http) => { await http.SignOutAsync(CookieScheme); return Results.NoContent(); });

        g.MapGet("/me", async (ClaimsPrincipal u, AdminStore admins, CancellationToken ct) =>
            await admins.FindAsync(Me(u), ct) is { Disabled: false } a ? Results.Ok(a.View()) : Results.Unauthorized());

        g.MapPost("/me/password", async (PasswordBody body, ClaimsPrincipal u, AdminStore admins, Audit audit, HttpContext http, CancellationToken ct) =>
        {
            var a = await admins.FindAsync(Me(u), ct);
            if (a is null || !admins.Verify(a, body.Current ?? "")) return Bad("current", "The current password is not correct.");
            if (AdminStore.CheckPassword(body.Password) is { } e) return Bad("password", e);
            await admins.SetAsync(a.Id, "pw_hash = @h", new { h = admins.Hash(body.Password!) }, ct);
            await audit.WriteAsync(a.Email, "admin.password", a.Id.ToString(), PublicApi.Ip(http), null, ct);
            return Results.NoContent();
        });

        g.MapPost("/me/mfa/setup", (ClaimsPrincipal u) =>
        {
            var secret = Totp.NewSecret();
            var uri = Totp.OtpAuthUri("Zentriq License Server", Actor(u), secret);
            using var qr = new QRCodeGenerator();
            var svg = new SvgQRCode(qr.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M)).GetGraphic(4);
            return Results.Ok(new { secret = Totp.ToBase32(secret), uri, svg });
        });

        g.MapPost("/me/mfa/enable", async (CodeBody body, ClaimsPrincipal u, AdminStore admins, Audit audit, TimeProvider time, HttpContext http, CancellationToken ct) =>
        {
            var secret = FromBase32(body.Secret);
            if (secret is null || !Totp.Verify(secret, body.Code, time.GetUtcNow())) return Bad("code", "The code does not match. Check the time on your phone and try again.");
            await admins.SetAsync(Me(u), "totp_secret = @s", new { s = secret }, ct);
            await audit.WriteAsync(Actor(u), "admin.mfa_enabled", Me(u).ToString(), PublicApi.Ip(http), null, ct);
            return Results.NoContent();
        });

        g.MapPost("/me/mfa/disable", async (CodeBody body, ClaimsPrincipal u, AdminStore admins, Audit audit, TimeProvider time, HttpContext http, CancellationToken ct) =>
        {
            var a = await admins.FindAsync(Me(u), ct);
            if (a?.TotpSecret is null) return Results.NoContent();
            if (!Totp.Verify(a.TotpSecret, body.Code, time.GetUtcNow())) return Bad("code", "The code is not correct.");
            await admins.SetAsync(a.Id, "totp_secret = NULL", new { }, ct);
            await audit.WriteAsync(a.Email, "admin.mfa_disabled", a.Id.ToString(), PublicApi.Ip(http), null, ct);
            return Results.NoContent();
        });

        // ---- catalog / dashboard --------------------------------------------------------------------------------

        g.MapGet("/catalog", (SigningKeyHolder keys, LicenseServerOptions o) =>
        {
            var key = keys.Require();
            return Results.Ok(new
        {
            editions = Catalog.Editions.Select(e => new { id = e, title = Editions.Title(e), modules = Editions.ModulesOf(e) }),
            modules = Modules.All.OrderBy(m => m),
            nonItPacks = Catalog.NonItPacks, subscriptionYears = Catalog.SubscriptionYears, supportYears = Catalog.SupportYears,
            trial = Plans.TrialLimits, free = Plans.FreeLimits,
            signing = new { key.KeyId, key.PublicPem, key.Fingerprint },
            productKeyEntry = new { keyId = key.KeyId, publicKeyPem = key.PublicPem, notAfter = (string?)null, revoked = false },
            publicUrl = o.PublicUrl, leaseDays = o.LeaseDays,
        });
        });

        g.MapGet("/dashboard", async (NpgsqlDataSource ds, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            await using var conn = await ds.OpenConnectionAsync(ct);
            var counts = await conn.FirstAsync("""
                SELECT (SELECT count(*) FROM zls.customers) AS customers,
                       (SELECT count(*) FROM zls.licenses WHERE status = 'active') AS active,
                       (SELECT count(*) FROM zls.licenses WHERE status <> 'active') AS inactive,
                       (SELECT count(*) FROM zls.activations WHERE status = 'active') AS activations,
                       (SELECT count(*) FROM zls.activations WHERE status = 'active' AND mode = 'online' AND last_checkin_at < @stale) AS stale
                """, new { stale = now.AddDays(-7) }, r => new { customers = r.GetInt64(0), active = r.GetInt64(1), inactive = r.GetInt64(2), activations = r.GetInt64(3), stale = r.GetInt64(4) }, ct);
            var expiring = await conn.ListAsync($"""
                SELECT l.id, c.company, l.license_type, CASE WHEN l.license_type = 'perpetual' THEN l.support_until ELSE l.expires_at END AS ends
                FROM zls.licenses l JOIN zls.customers c ON c.id = l.customer_id
                WHERE l.status = 'active' AND (CASE WHEN l.license_type = 'perpetual' THEN l.support_until ELSE l.expires_at END) BETWEEN @from AND @to
                ORDER BY ends LIMIT 50
                """, new { from = now.AddDays(-30), to = now.AddDays(90) },
                r => new { licenseId = r.GetString(0), company = r.GetString(1), licenseType = r.GetString(2), ends = r.GetFieldValue<DateTimeOffset>(3) }, ct);
            var recent = await conn.ListAsync("SELECT at, actor, action, target FROM zls.audit_log ORDER BY at DESC LIMIT 15", null,
                r => new { at = r.GetFieldValue<DateTimeOffset>(0), actor = r.GetString(1), action = r.GetString(2), target = r.Str("target") }, ct);
            return Results.Ok(new { counts, expiring, recent });
        });

        // ---- customers ------------------------------------------------------------------------------------------

        g.MapGet("/customers", async (NpgsqlDataSource ds, string? q, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var rows = await conn.ListAsync($"""
                SELECT {Customer.Columns}, (SELECT count(*) FROM zls.licenses l WHERE l.customer_id = c.id) AS licenses
                FROM zls.customers c WHERE @q = '' OR c.company ILIKE '%' || @q || '%' OR c.contact_email ILIKE '%' || @q || '%'
                ORDER BY lower(c.company) LIMIT 500
                """, new { q = q?.Trim() ?? "" }, r => new { customer = Customer.Read(r), licenses = r.GetInt64(r.GetOrdinal("licenses")) }, ct);
            return Results.Ok(rows.Select(x => new { x.customer.Id, x.customer.Company, x.customer.ContactName, x.customer.ContactEmail, x.customer.Country, x.customer.CreatedAt, x.licenses }));
        });

        g.MapGet("/customers/{id:guid}", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var c = await conn.FirstAsync($"SELECT {Customer.Columns} FROM zls.customers WHERE id = @id", new { id }, Customer.Read, ct);
            if (c is null) return Results.NotFound();
            var licenses = await conn.ListAsync($"SELECT {License.Columns} FROM zls.licenses WHERE customer_id = @id ORDER BY created_at DESC", new { id }, License.Read, ct);
            return Results.Ok(new { customer = c, licenses });
        });

        g.MapPost("/customers", async (CustomerBody body, NpgsqlDataSource ds, Audit audit, ClaimsPrincipal u, HttpContext http, CancellationToken ct) =>
            await SaveCustomerAsync(null, body, ds, audit, u, http, ct));
        g.MapPut("/customers/{id:guid}", async (Guid id, CustomerBody body, NpgsqlDataSource ds, Audit audit, ClaimsPrincipal u, HttpContext http, CancellationToken ct) =>
            await SaveCustomerAsync(id, body, ds, audit, u, http, ct));

        // ---- licenses -------------------------------------------------------------------------------------------

        g.MapGet("/licenses", async (NpgsqlDataSource ds, string? q, string? status, Guid? customerId, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var rows = await conn.ListAsync($"""
                SELECT {Prefixed("l")}, c.company,
                       (SELECT count(*) FROM zls.activations a WHERE a.license_id = l.id AND a.status = 'active') AS activations
                FROM zls.licenses l JOIN zls.customers c ON c.id = l.customer_id
                WHERE (@q = '' OR l.id ILIKE '%' || @q || '%' OR c.company ILIKE '%' || @q || '%' OR l.key_hint ILIKE '%' || @q || '%')
                  AND (@status = '' OR l.status = @status) AND (@cid::uuid IS NULL OR l.customer_id = @cid::uuid)
                ORDER BY l.created_at DESC LIMIT 1000
                """, new { q = q?.Trim() ?? "", status = status?.Trim() ?? "", cid = (object?)customerId },
                r => new { license = License.Read(r), company = r.GetString(r.GetOrdinal("company")), activations = r.GetInt64(r.GetOrdinal("activations")) }, ct);
            return Results.Ok(rows.Select(x => new { x.license, x.company, x.activations }));
        });

        g.MapGet("/licenses/{id}", async (string id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var l = await conn.FirstAsync($"SELECT {License.Columns} FROM zls.licenses WHERE id = @id", new { id }, License.Read, ct);
            if (l is null) return Results.NotFound();
            var c = await conn.FirstAsync($"SELECT {Customer.Columns} FROM zls.customers WHERE id = @id", new { id = l.CustomerId }, Customer.Read, ct);
            var activations = await conn.ListAsync($"SELECT {Activation.Columns} FROM zls.activations WHERE license_id = @id ORDER BY status, activated_at DESC", new { id }, Activation.Read, ct);
            var issued = await conn.ListAsync("SELECT id, activation_id, issued_at, lease_until, reason FROM zls.issued_documents WHERE license_id = @id ORDER BY issued_at DESC LIMIT 100",
                new { id }, r => new { id = r.GetGuid(0), activationId = r.Uuid("activation_id"), issuedAt = r.GetFieldValue<DateTimeOffset>(2), leaseUntil = r.Time("lease_until"), reason = r.GetString(4) }, ct);
            var audit = await conn.ListAsync("SELECT at, actor, action, ip, detail::text FROM zls.audit_log WHERE target = @id ORDER BY at DESC LIMIT 200",
                new { id }, r => new { at = r.GetFieldValue<DateTimeOffset>(0), actor = r.GetString(1), action = r.GetString(2), ip = r.Str("ip"), detail = r.Str("detail") }, ct);
            return Results.Ok(new { license = l, customer = c, entitlements = l.Entitlements(), activations = activations.Select(a => new
            {
                a.Id, a.InstallationId, a.Mode, a.MachineName, a.Organization, a.ProductVersion, a.ReleaseDate, a.Status, a.ActivatedAt, a.LastCheckinAt, a.DeactivatedAt, a.DeactivatedBy,
                usage = JsonSerializer.Deserialize<Dictionary<string, int>>(a.Usage),
            }), issued, audit });
        });

        g.MapPost("/licenses", async (LicenseBody body, NpgsqlDataSource ds, LicenseIssuer issuer, Audit audit, ClaimsPrincipal u, HttpContext http, CancellationToken ct) =>
        {
            if (Check(body, create: true) is { } err) return err;
            var now = issuer.Now;
            var starts = (body.StartsAt ?? now).ToUniversalTime();
            var type = body.LicenseType!;
            var years = type == LicenseFormats.Subscription ? body.TermYears!.Value : body.SupportYears!.Value;
            var expires = type == LicenseFormats.Subscription ? starts.AddYears(years) : LicenseFormats.PerpetualExpiry;
            var support = starts.AddYears(years);
            var key = LicenseKeys.New();
            var id = issuer.NewLicenseId();
            await using var conn = await ds.OpenConnectionAsync(ct);
            if (await conn.ScalarAsync("SELECT 1 FROM zls.customers WHERE id = @id", new { id = body.CustomerId!.Value }, ct) is null) return Bad("customerId", "Choose a customer.");
            await conn.ExecAsync("""
                INSERT INTO zls.licenses (id, customer_id, key_hash, key_hint, edition, license_type, term_years, starts_at, expires_at, support_until,
                    devices, nonit_pack, cloud_instances, technicians, extra_modules, max_activations, grace_days, notes)
                VALUES (@id, @cid, @kh, @hint, @edition, @type, @term, @starts, @expires, @support, @devices, @nonit, @cloud, @tech, @mods, @maxact, @grace, @notes)
                """, new
            {
                id, cid = body.CustomerId.Value, kh = LicenseKeys.Hash(key), hint = LicenseKeys.Hint(key), edition = body.Edition!, type,
                term = (object?)(type == LicenseFormats.Subscription ? years : null), starts, expires, support,
                devices = body.Devices!.Value, nonit = (object?)body.NonItPack, cloud = body.CloudInstances ?? 0, tech = body.Technicians ?? 0,
                mods = Mods(body.ExtraModules), maxact = body.MaxActivations ?? 1, grace = body.GraceDays ?? 30, notes = (object?)Trim(body.Notes, 2000),
            }, ct);
            await audit.WriteAsync(Actor(u), "license.create", id, PublicApi.Ip(http), new { body.CustomerId, body.Edition, type, years, body.Devices, body.NonItPack, body.CloudInstances, body.Technicians, key = LicenseKeys.Hint(key) }, ct);
            // The key is shown once: only its hash is stored.
            return Results.Ok(new { id, licenseKey = key });
        });

        g.MapPut("/licenses/{id}", async (string id, LicenseBody body, NpgsqlDataSource ds, Audit audit, ClaimsPrincipal u, HttpContext http, CancellationToken ct) =>
        {
            if (Check(body, create: false) is { } err) return err;
            await using var conn = await ds.OpenConnectionAsync(ct);
            var n = await conn.ExecAsync("""
                UPDATE zls.licenses SET edition = @edition, devices = @devices, nonit_pack = @nonit, cloud_instances = @cloud, technicians = @tech,
                       extra_modules = @mods, max_activations = @maxact, grace_days = @grace, notes = @notes, updated_at = now()
                WHERE id = @id
                """, new
            {
                id, edition = body.Edition!, devices = body.Devices!.Value, nonit = (object?)body.NonItPack, cloud = body.CloudInstances ?? 0, tech = body.Technicians ?? 0,
                mods = Mods(body.ExtraModules), maxact = body.MaxActivations ?? 1, grace = body.GraceDays ?? 30, notes = (object?)Trim(body.Notes, 2000),
            }, ct);
            if (n == 0) return Results.NotFound();
            await audit.WriteAsync(Actor(u), "license.update", id, PublicApi.Ip(http), new { body.Edition, body.Devices, body.NonItPack, body.CloudInstances, body.Technicians, body.ExtraModules, body.MaxActivations, body.GraceDays }, ct);
            return Results.NoContent();
        });

        // Subscription renewal: 1, 3 or 5 more years from the current end (or from today when already ended).
        g.MapPost("/licenses/{id}/renew", async (string id, YearsBody body, NpgsqlDataSource ds, TimeProvider time, Audit audit, ClaimsPrincipal u, HttpContext http, CancellationToken ct) =>
        {
            if (body.Years is not { } y || !Catalog.SubscriptionYears.Contains(y)) return Bad("years", "Renew for 1, 3 or 5 years.");
            await using var conn = await ds.OpenConnectionAsync(ct);
            var l = await conn.FirstAsync($"SELECT {License.Columns} FROM zls.licenses WHERE id = @id", new { id }, License.Read, ct);
            if (l is null) return Results.NotFound();
            if (l.IsPerpetual) return Bad("years", "Perpetual licenses do not expire; extend their support instead.");
            var from = l.ExpiresAt > time.GetUtcNow() ? l.ExpiresAt : time.GetUtcNow();
            var to = from.AddYears(y);
            await conn.ExecAsync("UPDATE zls.licenses SET expires_at = @to, support_until = @to, term_years = @y, updated_at = now() WHERE id = @id", new { id, to, y }, ct);
            await audit.WriteAsync(Actor(u), "license.renew", id, PublicApi.Ip(http), new { years = y, from = l.ExpiresAt, to }, ct);
            return Results.Ok(new { expiresAt = to });
        });

        // Perpetual support: one or more years at a time, from the current end of support (or from today when it has lapsed).
        g.MapPost("/licenses/{id}/support", async (string id, YearsBody body, NpgsqlDataSource ds, TimeProvider time, Audit audit, ClaimsPrincipal u, HttpContext http, CancellationToken ct) =>
        {
            if (body.Years is not { } y || y < 1 || y > 10) return Bad("years", "Extend support by 1 to 10 years.");
            await using var conn = await ds.OpenConnectionAsync(ct);
            var l = await conn.FirstAsync($"SELECT {License.Columns} FROM zls.licenses WHERE id = @id", new { id }, License.Read, ct);
            if (l is null) return Results.NotFound();
            if (!l.IsPerpetual) return Bad("years", "Subscriptions include support; renew the subscription instead.");
            var from = l.SupportUntil > time.GetUtcNow() ? l.SupportUntil : time.GetUtcNow();
            var to = from.AddYears(y);
            await conn.ExecAsync("UPDATE zls.licenses SET support_until = @to, updated_at = now() WHERE id = @id", new { id, to }, ct);
            await audit.WriteAsync(Actor(u), "license.support", id, PublicApi.Ip(http), new { years = y, from = l.SupportUntil, to }, ct);
            return Results.Ok(new { supportUntil = to });
        });

        g.MapPost("/licenses/{id}/status", async (string id, StatusBody body, NpgsqlDataSource ds, Audit audit, ClaimsPrincipal u, HttpContext http, CancellationToken ct) =>
        {
            if (body.Status is not ("active" or "suspended" or "revoked")) return Bad("status", "Choose active, suspended or revoked.");
            await using var conn = await ds.OpenConnectionAsync(ct);
            var current = await conn.ScalarAsync("SELECT status FROM zls.licenses WHERE id = @id", new { id }, ct) as string;
            if (current is null) return Results.NotFound();
            if (current == "revoked" && body.Status != "revoked") return Bad("status", "A revoked license cannot be reinstated; issue a new one.");
            await conn.ExecAsync("UPDATE zls.licenses SET status = @s, updated_at = now() WHERE id = @id", new { id, s = body.Status }, ct);
            await audit.WriteAsync(Actor(u), "license.status", id, PublicApi.Ip(http), new { from = current, to = body.Status, reason = Trim(body.Reason) }, ct);
            return Results.NoContent();
        });

        // The license file for the customer. Issuing a new one makes the earlier file unusable for activation.
        g.MapPost("/licenses/{id}/file", async (string id, NpgsqlDataSource ds, LicenseIssuer issuer, Audit audit, ClaimsPrincipal u, HttpContext http, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var l = await conn.FirstAsync($"SELECT {License.Columns} FROM zls.licenses WHERE id = @id FOR UPDATE", new { id }, License.Read, ct, tx);
            if (l is null) return Results.NotFound();
            if (l.Status != "active") return Bad("id", $"The license is {l.Status}.");
            var c = (await conn.FirstAsync($"SELECT {Customer.Columns} FROM zls.customers WHERE id = @id", new { id = l.CustomerId }, Customer.Read, ct, tx))!;
            var (doc, _) = await issuer.IssueLicenseFileAsync(conn, tx, l, c, ct);
            await audit.WriteAsync(Actor(u), "license.file", id, PublicApi.Ip(http), new { replaced = l.FileHash is not null }, ct, conn, tx);
            await tx.CommitAsync(ct);
            return Results.Ok(new { license = doc, fileName = $"zentriq-license-{id}.zlic", replaced = l.FileHash is not null });
        });

        // New key (lost or leaked). Existing activations keep working through their tokens; the old key no longer activates.
        g.MapPost("/licenses/{id}/rekey", async (string id, NpgsqlDataSource ds, Audit audit, ClaimsPrincipal u, HttpContext http, CancellationToken ct) =>
        {
            var key = LicenseKeys.New();
            await using var conn = await ds.OpenConnectionAsync(ct);
            var n = await conn.ExecAsync("UPDATE zls.licenses SET key_hash = @h, key_hint = @hint, updated_at = now() WHERE id = @id",
                new { id, h = LicenseKeys.Hash(key), hint = LicenseKeys.Hint(key) }, ct);
            if (n == 0) return Results.NotFound();
            await audit.WriteAsync(Actor(u), "license.rekey", id, PublicApi.Ip(http), new { key = LicenseKeys.Hint(key) }, ct);
            return Results.Ok(new { licenseKey = key });
        });

        // Offline activation on the customer's behalf (they emailed the activation request): no key needed.
        g.MapPost("/licenses/{id}/offline", async (string id, OfflineIssueBody body, NpgsqlDataSource ds, LicenseIssuer issuer, Audit audit, ClaimsPrincipal u, HttpContext http, CancellationToken ct) =>
        {
            ActivationRequest? req;
            try { req = body.Request.ValueKind == JsonValueKind.Object ? body.Request.Deserialize<ActivationRequest>(JsonDefaults.Web) : null; }
            catch (JsonException) { req = null; }
            if (req is null || req.Format != LicenseFormats.ActivationRequest || req.InstallationId == Guid.Empty) return Bad("request", "Attach the customer's activation request file.");
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var l = await conn.FirstAsync($"SELECT {License.Columns} FROM zls.licenses WHERE id = @id FOR UPDATE", new { id }, License.Read, ct, tx);
            if (l is null) return Results.NotFound();
            if (l.Status != "active") return Bad("request", $"The license is {l.Status}.");
            var c = (await conn.FirstAsync($"SELECT {Customer.Columns} FROM zls.customers WHERE id = @id", new { id = l.CustomerId }, Customer.Read, ct, tx))!;
            var existing = await conn.ScalarAsync("SELECT id FROM zls.activations WHERE license_id = @l AND installation_id = @i AND status = 'active'", new { l = id, i = req.InstallationId }, ct, tx) as Guid?;
            if (existing is null)
            {
                var active = Convert.ToInt32(await conn.ScalarAsync("SELECT count(*) FROM zls.activations WHERE license_id = @l AND status = 'active'", new { l = id }, ct, tx));
                if (active >= l.MaxActivations) return Bad("request", $"The license is active on {active} server(s), the most it allows. Deactivate one first, or raise the activation limit.");
            }
            var actId = existing ?? Guid.NewGuid();
            if (existing is null)
                await conn.ExecAsync("""
                    INSERT INTO zls.activations (id, license_id, installation_id, mode, request_id, machine_name, organization, product_version)
                    VALUES (@id, @l, @i, 'offline', @r, @m, @o, @v)
                    """, new { id = actId, l = id, i = req.InstallationId, r = (object?)req.RequestId, m = (object?)Trim(req.MachineName, 255), o = (object?)Trim(req.Organization, 255), v = (object?)Trim(req.ProductVersion, 64) }, ct, tx);
            else
                await conn.ExecAsync("UPDATE zls.activations SET mode = 'offline', token_hash = NULL, request_id = @r WHERE id = @id", new { id = actId, r = (object?)req.RequestId }, ct, tx);
            var (doc, payload) = await issuer.IssueAsync(conn, tx, l, c, req.InstallationId, req.RequestId, actId, LicenseFormats.Offline, "portal-offline", ct);
            await audit.WriteAsync(Actor(u), "activate.offline_portal", id, PublicApi.Ip(http), new { req.InstallationId, req.MachineName, req.Organization }, ct, conn, tx);
            await tx.CommitAsync(ct);
            return Results.Ok(new { license = doc, fileName = $"zentriq-license-{payload.LicenseId}.zlic" });
        });

        // Checks a license file exactly as Zentriq does (signature first, then content), against this server's signing key.
        g.MapPost("/verify", (VerifyBody body, SigningKeyHolder keys, TimeProvider time) =>
        {
            var key = keys.Require();
            var doc = body.Document?.Trim() ?? "";
            if (doc.Length == 0) return Bad("document", "Choose a license file (.zlic).");
            Guid installation = Guid.Empty; string? keyId = null; JsonElement? payload = null;
            try
            {
                using var env = JsonDocument.Parse(doc);
                keyId = env.RootElement.TryGetProperty("keyId", out var k) ? k.GetString() : null;
                if (env.RootElement.TryGetProperty("payload", out var p) && p.GetString() is { } b64)
                {
                    using var pd = JsonDocument.Parse(Convert.FromBase64String(b64));
                    payload = pd.RootElement.Clone();
                    if (pd.RootElement.TryGetProperty("installationId", out var i) && i.TryGetGuid(out var g2)) installation = g2;
                }
            }
            catch (Exception ex) when (ex is JsonException or FormatException) { }
            var verifier = new Zentriq.Application.Licensing.LicenseVerifier(new Zentriq.Application.Licensing.InMemoryPublicKeyStore(
                [new Zentriq.Application.Licensing.LicensePublicKey(key.KeyId, key.PublicPem, null, false)]));
            var r = verifier.Verify(doc, installation, time.GetUtcNow(), enforceValidityWindow: false);
            var current = r.IsValid ? verifier.Verify(doc, installation, time.GetUtcNow()) : r;
            return Results.Ok(new
            {
                valid = r.IsValid, rejection = r.Rejection.ToString(), message = r.Message, currentlyValid = current.IsValid, currentMessage = current.Message,
                keyId, signingKeyId = key.KeyId, fingerprint = key.Fingerprint,
                payload = r.IsValid ? payload : null,   // content is shown only for files whose signature verified
            });
        });

        g.MapGet("/documents/{id:guid}", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var doc = await conn.FirstAsync("SELECT license_id, document FROM zls.issued_documents WHERE id = @id", new { id }, r => new { lic = r.GetString(0), doc = r.GetString(1) }, ct);
            return doc is null ? Results.NotFound() : Results.File(System.Text.Encoding.UTF8.GetBytes(doc.doc), "application/json", $"zentriq-license-{doc.lic}.zlic");
        });

        g.MapPost("/activations/{id:guid}/deactivate", async (Guid id, NpgsqlDataSource ds, Audit audit, ClaimsPrincipal u, HttpContext http, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            // The token is kept so the installation's next check-in learns that it was deactivated (and falls back to the free tier).
            var lic = await conn.ScalarAsync("""
                UPDATE zls.activations SET status = 'deactivated', deactivated_at = now(), deactivated_by = @by WHERE id = @id AND status = 'active' RETURNING license_id
                """, new { id, by = Actor(u) }, ct) as string;
            if (lic is null) return Results.NotFound();
            await audit.WriteAsync(Actor(u), "activation.deactivate", lic, PublicApi.Ip(http), new { activation = id }, ct);
            return Results.NoContent();
        });

        g.MapGet("/audit", async (NpgsqlDataSource ds, string? q, int? limit, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var rows = await conn.ListAsync("""
                SELECT at, actor, action, target, ip, detail::text FROM zls.audit_log
                WHERE @q = '' OR actor ILIKE '%' || @q || '%' OR action ILIKE '%' || @q || '%' OR target ILIKE '%' || @q || '%'
                ORDER BY at DESC LIMIT @lim
                """, new { q = q?.Trim() ?? "", lim = Math.Clamp(limit ?? 300, 1, 2000) },
                r => new { at = r.GetFieldValue<DateTimeOffset>(0), actor = r.GetString(1), action = r.GetString(2), target = r.Str("target"), ip = r.Str("ip"), detail = r.Str("detail") }, ct);
            return Results.Ok(rows);
        });

        // ---- portal accounts (owners manage them) -----------------------------------------------------------------

        g.MapGet("/admins", async (AdminStore admins, CancellationToken ct) => Results.Ok((await admins.ListAsync(ct)).Select(a => a.View())));

        g.MapPost("/admins", async (AdminBody body, AdminStore admins, Audit audit, ClaimsPrincipal u, HttpContext http, CancellationToken ct) =>
        {
            if (!u.IsInRole("owner")) return Results.Problem(statusCode: 403, title: "Only owners manage portal accounts.");
            if (string.IsNullOrWhiteSpace(body.Email) || !body.Email.Contains('@')) return Bad("email", "Enter an email address.");
            if (string.IsNullOrWhiteSpace(body.Name)) return Bad("name", "Enter a name.");
            if (AdminStore.CheckPassword(body.Password) is { } e) return Bad("password", e);
            if (await admins.FindByEmailAsync(body.Email, ct) is not null) return Bad("email", "An account with this email exists.");
            var a = await admins.CreateAsync(body.Email, body.Name, body.Password!, body.Owner == true, ct);
            await audit.WriteAsync(Actor(u), "admin.create", a.Id.ToString(), PublicApi.Ip(http), new { a.Email, a.IsOwner }, ct);
            return Results.Ok(a.View());
        });

        g.MapPost("/admins/{id:guid}/{action:regex(^(disable|enable|reset-mfa|unlock)$)}", async (Guid id, string action, AdminStore admins, Audit audit, ClaimsPrincipal u, HttpContext http, CancellationToken ct) =>
        {
            if (!u.IsInRole("owner")) return Results.Problem(statusCode: 403, title: "Only owners manage portal accounts.");
            if (id == Me(u) && action == "disable") return Bad("id", "You cannot disable your own account.");
            if (await admins.FindAsync(id, ct) is null) return Results.NotFound();
            var (sql, args) = action switch
            {
                "disable" => ("disabled = true", (object)new { }),
                "enable" => ("disabled = false", new { }),
                "reset-mfa" => ("totp_secret = NULL", new { }),
                _ => ("failed_logins = 0, locked_until = NULL", new { }),
            };
            await admins.SetAsync(id, sql, args, ct);
            await audit.WriteAsync(Actor(u), "admin." + action, id.ToString(), PublicApi.Ip(http), null, ct);
            return Results.NoContent();
        });
    }

    private static string Prefixed(string alias) =>
        string.Join(", ", License.Columns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(c => $"{alias}.{c}"));

    private static string[] Mods(string[]? m) => (m ?? []).Select(x => x.Trim()).Where(Modules.All.Contains).Distinct().OrderBy(x => x).ToArray();

    private static IResult? Check(LicenseBody b, bool create)
    {
        if (create)
        {
            if (b.CustomerId is null) return Bad("customerId", "Choose a customer.");
            if (b.LicenseType is not (LicenseFormats.Subscription or LicenseFormats.Perpetual)) return Bad("licenseType", "Choose subscription or perpetual.");
            if (b.LicenseType == LicenseFormats.Subscription && (b.TermYears is not { } t || !Catalog.SubscriptionYears.Contains(t))) return Bad("termYears", "Subscriptions run for 1, 3 or 5 years.");
            if (b.LicenseType == LicenseFormats.Perpetual && (b.SupportYears is not { } s || s < 1 || s > 10)) return Bad("supportYears", "Include 1 to 10 years of support.");
        }
        if (b.Edition is null || !Catalog.Editions.Contains(b.Edition)) return Bad("edition", "Choose Standard or Advanced.");
        if (b.Devices is not { } d || d < 0 || d > 1_000_000) return Bad("devices", "Enter the number of IT devices (0 to 1,000,000).");
        if (b.NonItPack is { } p && !Catalog.NonItPacks.Contains(p)) return Bad("nonItPack", "Choose a non-IT pack: 250, 500, 1000, 2000, 4000 or unlimited.");
        if (b.CloudInstances is < 0 or > 1_000_000) return Bad("cloudInstances", "Enter the number of cloud and virtual machine instances.");
        if (b.Technicians is < 0 or > 10_000) return Bad("technicians", "Enter the number of remote support technicians.");
        if (b.MaxActivations is < 1 or > 100) return Bad("maxActivations", "Allow 1 to 100 activations.");
        if (b.GraceDays is < 0 or > 90) return Bad("graceDays", "Grace is 0 to 90 days.");
        return null;
    }

    private static async Task<IResult> SaveCustomerAsync(Guid? id, CustomerBody b, NpgsqlDataSource ds, Audit audit, ClaimsPrincipal u, HttpContext http, CancellationToken ct)
    {
        var company = Trim(b.Company, 200);
        if (company is null) return Bad("company", "Enter the company name.");
        if (Trim(b.ContactEmail) is { } em && !em.Contains('@')) return Bad("contactEmail", "Enter a valid email address.");
        await using var conn = await ds.OpenConnectionAsync(ct);
        var dup = await conn.ScalarAsync("SELECT id FROM zls.customers WHERE lower(company) = lower(@c) AND (@id::uuid IS NULL OR id <> @id::uuid)", new { c = company, id = (object?)id }, ct);
        if (dup is not null) return Bad("company", "A customer with this name exists.");
        var args = new
        {
            id = id ?? Guid.NewGuid(), company, cn = (object?)Trim(b.ContactName, 200), ce = (object?)Trim(b.ContactEmail, 200), cp = (object?)Trim(b.ContactPhone, 100),
            addr = (object?)Trim(b.Address, 1000), country = (object?)Trim(b.Country, 100), tax = (object?)Trim(b.TaxId, 100), notes = (object?)Trim(b.Notes, 4000),
        };
        if (id is null)
            await conn.ExecAsync("""
                INSERT INTO zls.customers (id, company, contact_name, contact_email, contact_phone, address, country, tax_id, notes)
                VALUES (@id, @company, @cn, @ce, @cp, @addr, @country, @tax, @notes)
                """, args, ct);
        else if (await conn.ExecAsync("""
                UPDATE zls.customers SET company = @company, contact_name = @cn, contact_email = @ce, contact_phone = @cp, address = @addr,
                       country = @country, tax_id = @tax, notes = @notes, updated_at = now() WHERE id = @id
                """, args, ct) == 0) return Results.NotFound();
        await audit.WriteAsync(Actor(u), id is null ? "customer.create" : "customer.update", args.id.ToString(), PublicApi.Ip(http), new { company }, ct);
        return Results.Ok(new { id = args.id });
    }

    private static byte[]? FromBase32(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var ch in s.Trim().ToUpperInvariant().TrimEnd('='))
        {
            var v = alphabet.IndexOf(ch);
            if (v < 0) return null;
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return bytes.Count >= 10 ? bytes.ToArray() : null;
    }
}
