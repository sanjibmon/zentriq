using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Zentriq.Application.Common;
using Zentriq.Application.Licensing;
using Zentriq.Application.Security;
using Zentriq.Domain.Licensing;
using Zentriq.Infrastructure.Audit;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Licensing;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Infrastructure.Setup;

public sealed record InitializeRequest(string SetupToken, string OrganizationName, string AdminEmail, string AdminDisplayName, string AdminPassword);

public enum InitializeOutcome { Initialized, AlreadyInitialized, InvalidToken, ValidationFailed }

public sealed record InitializeResult(InitializeOutcome Outcome, string Message, DateTimeOffset? TrialStartedAt = null, DateTimeOffset? TrialExpiresAt = null);

/// <summary>
/// First-time initialization. In ONE transaction: create the organization and first administrator,
/// start the trial and mark the installation initialized. Either everything commits or nothing
/// does, so a failed setup never starts the trial. Re-running is a no-op (the trial row is
/// insert-once and protected by database triggers).
/// </summary>
public sealed class InitializationService(ZentriqDbContext db, IClock clock, Func<AppSecrets> appSecrets, IPasswordHasher<User> hasher)
{
    public static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public async Task<InitializeResult> InitializeAsync(InitializeRequest req, string? sourceIp, CancellationToken ct = default)
    {
        var validation = Validate(req);
        if (validation is not null) return new(InitializeOutcome.ValidationFailed, validation);

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var state = (await db.InstallState.FromSqlRaw("SELECT * FROM zentriq.install_state WHERE id = 1 FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if (state is null)
            return new(InitializeOutcome.ValidationFailed, "Database is not migrated. Run the installer repair.");
        if (state.InitializedAt is not null)
        {
            var existing = await db.Trial.AsNoTracking().FirstOrDefaultAsync(ct);
            return new(InitializeOutcome.AlreadyInitialized, "Zentriq is already initialized.", existing?.StartedAt, existing?.ExpiresAt);
        }
        if (state.SetupTokenHash is null || state.SetupTokenExpiresAt is null || state.SetupTokenExpiresAt < clock.UtcNow ||
            !CryptographicOperations.FixedTimeEquals(state.SetupTokenHash, HashToken(req.SetupToken)))
            return new(InitializeOutcome.InvalidToken, "The setup token is invalid or has expired. Run 'zentriqctl repair' to issue a new one.");

        var now = TruncateToMs(clock.UtcNow);
        var org = new Organization { Id = Guid.NewGuid(), Name = req.OrganizationName.Trim(), CreatedAt = now };
        db.Organizations.Add(org);

        var admin = new User
        {
            Id = Guid.NewGuid(),
            Email = req.AdminEmail.Trim(),
            DisplayName = req.AdminDisplayName.Trim(),
            Status = "active",
            SecurityStamp = Guid.NewGuid(),
            PasswordChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        admin.PasswordHash = hasher.HashPassword(admin, req.AdminPassword);
        db.Users.Add(admin);
        db.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = "admin" });

        var policyJson = (await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "trial.policy", ct))?.Value;
        var policy = policyJson is null ? TrialPolicy.Default : JsonSerializer.Deserialize<TrialPolicy>(policyJson, LicensingService.JsonOptions) ?? TrialPolicy.Default;
        if (policy.Days != 14) policy = policy with { Days = 14 }; // the trial length is a product rule, not a setting
        var expires = now.AddDays(policy.Days);
        db.Trial.Add(new TrialRecord
        {
            Id = 1,
            InstallationId = state.InstallationId,
            StartedAt = now,
            ExpiresAt = expires,
            Policy = JsonSerializer.Serialize(policy, LicensingService.JsonOptions),
            IntegrityMac = TrialIntegrity.Compute(appSecrets().InstallSecretBytes(), state.InstallationId, now, expires),
        });

        state.InitializedAt = now;
        state.InitializedBy = admin.Id;
        state.SetupTokenHash = null;
        state.SetupTokenExpiresAt = null;
        state.LastSeenUtc = now;

        db.InstallationHistory.Add(new InstallationHistory
        {
            OccurredAt = now, Action = "initialize", Outcome = "succeeded",
            Detail = JsonSerializer.Serialize(new { trialStartedAt = now, trialExpiresAt = expires, admin = admin.Email }),
        });
        await db.SaveChangesAsync(ct);

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var npgTx = (NpgsqlTransaction)tx.GetDbTransaction();
        var audit = new AuditWriter(null!);
        await audit.WriteAsync(new AuditActor(null, "setup", sourceIp), "setup.initialize", admin.Email, "Success",
            new { organization = org.Name, trialExpiresAt = expires }, conn, npgTx, ct);

        await tx.CommitAsync(ct);
        return new(InitializeOutcome.Initialized, "Zentriq initialized; 14-day trial started.", now, expires);
    }

    private static string? Validate(InitializeRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.SetupToken)) return "Setup token is required.";
        if (string.IsNullOrWhiteSpace(r.OrganizationName) || r.OrganizationName.Length > 200) return "Organization name is required (max 200 characters).";
        if (string.IsNullOrWhiteSpace(r.AdminEmail) || !r.AdminEmail.Contains('@') || r.AdminEmail.Length > 254) return "A valid administrator e-mail is required.";
        if (string.IsNullOrWhiteSpace(r.AdminDisplayName) || r.AdminDisplayName.Length > 200) return "Administrator name is required.";
        return PasswordPolicy.Validate(r.AdminPassword, r.AdminEmail);
    }

    public static DateTimeOffset TruncateToMs(DateTimeOffset t) =>
        DateTimeOffset.FromUnixTimeMilliseconds(t.ToUniversalTime().ToUnixTimeMilliseconds());
}
