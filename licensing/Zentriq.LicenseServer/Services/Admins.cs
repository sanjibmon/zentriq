using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using Zentriq.Application.Security;

namespace Zentriq.LicenseServer.Services;

public sealed record Admin(Guid Id, string Email, string Name, string PwHash, byte[]? TotpSecret, bool IsOwner, bool Disabled,
    int FailedLogins, DateTimeOffset? LockedUntil, DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt)
{
    public const string Columns = "id, email, name, pw_hash, totp_secret, is_owner, disabled, failed_logins, locked_until, last_login_at, created_at";
    public static Admin Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetFieldValue<byte[]>(4), r.GetBoolean(5), r.GetBoolean(6), r.GetInt32(7), r.Time("locked_until"), r.Time("last_login_at"), r.Time("created_at")!.Value);
    public object View() => new { Id, Email, Name, IsOwner, Disabled, Mfa = TotpSecret is not null, LockedUntil, LastLoginAt, CreatedAt };
}

/// <summary>Portal accounts: password (ASP.NET Core Identity hasher), optional authenticator app (TOTP), lockout after repeated failures.</summary>
public sealed class AdminStore(NpgsqlDataSource ds, TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);
    private readonly PasswordHasher<Admin> _hasher = new();

    public static string? CheckPassword(string? pw) =>
        pw is null || pw.Length < 12 ? "Use at least 12 characters." :
        pw.Length > 200 ? "Use at most 200 characters." :
        pw.Distinct().Count() < 5 ? "Use a less repetitive passphrase." : null;

    public string Hash(string pw) => _hasher.HashPassword(null!, pw);
    public bool Verify(Admin a, string pw) => _hasher.VerifyHashedPassword(a, a.PwHash, pw) != PasswordVerificationResult.Failed;

    public async Task<Admin?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        return await conn.FirstAsync($"SELECT {Admin.Columns} FROM zls.admins WHERE id = @id", new { id }, Admin.Read, ct);
    }

    public async Task<Admin?> FindByEmailAsync(string email, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        return await conn.FirstAsync($"SELECT {Admin.Columns} FROM zls.admins WHERE lower(email) = lower(@e)", new { e = email.Trim() }, Admin.Read, ct);
    }

    public async Task<List<Admin>> ListAsync(CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        return await conn.ListAsync($"SELECT {Admin.Columns} FROM zls.admins ORDER BY lower(email)", null, Admin.Read, ct);
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        return Convert.ToInt32(await conn.ScalarAsync("SELECT count(*) FROM zls.admins", null, ct));
    }

    public async Task<Admin> CreateAsync(string email, string name, string pw, bool owner, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var conn = await ds.OpenConnectionAsync(ct);
        await conn.ExecAsync("INSERT INTO zls.admins (id, email, name, pw_hash, is_owner) VALUES (@id, @email, @name, @h, @owner)",
            new { id, email = email.Trim(), name = name.Trim(), h = Hash(pw), owner }, ct);
        return (await conn.FirstAsync($"SELECT {Admin.Columns} FROM zls.admins WHERE id = @id", new { id }, Admin.Read, ct))!;
    }

    public async Task SetAsync(Guid id, string setSql, object args, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var values = args.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(args));
        await using var cmd = conn.Cmd($"UPDATE zls.admins SET {setSql} WHERE id = @id", null);
        cmd.Parameters.AddWithValue("id", id);
        foreach (var (k, v) in values) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public enum LoginOutcome { Ok, Invalid, Locked, NeedCode, BadCode }

    public async Task<(LoginOutcome Outcome, Admin? Admin)> LoginAsync(string? email, string? pw, string? code, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(pw)) return (LoginOutcome.Invalid, null);
        var a = await FindByEmailAsync(email, ct);
        if (a is null) { _hasher.HashPassword(null!, pw); return (LoginOutcome.Invalid, null); }   // same work either way
        if (a.LockedUntil is { } lu && lu > now) return (LoginOutcome.Locked, a);
        if (a.Disabled || !Verify(a, pw))
        {
            var failures = a.FailedLogins + 1;
            await SetAsync(a.Id, "failed_logins = @f, locked_until = @lu", new { f = failures >= MaxFailures ? 0 : failures, lu = failures >= MaxFailures ? now.Add(LockFor) : (DateTimeOffset?)null }, ct);
            return (failures >= MaxFailures ? LoginOutcome.Locked : LoginOutcome.Invalid, a);
        }
        if (a.TotpSecret is not null)
        {
            if (string.IsNullOrWhiteSpace(code)) return (LoginOutcome.NeedCode, a);
            if (!Totp.Verify(a.TotpSecret, code.Trim(), now))
            {
                var failures = a.FailedLogins + 1;
                await SetAsync(a.Id, "failed_logins = @f, locked_until = @lu", new { f = failures >= MaxFailures ? 0 : failures, lu = failures >= MaxFailures ? now.Add(LockFor) : (DateTimeOffset?)null }, ct);
                return (LoginOutcome.BadCode, a);
            }
        }
        await SetAsync(a.Id, "failed_logins = 0, locked_until = NULL, last_login_at = @now", new { now }, ct);
        return (LoginOutcome.Ok, a);
    }

    public static ClaimsPrincipal Principal(Admin a) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, a.Id.ToString()),
        new Claim(ClaimTypes.Email, a.Email),
        new Claim(ClaimTypes.Name, a.Name),
        new Claim(ClaimTypes.Role, a.IsOwner ? "owner" : "admin"),
    ], "zls"));
}
