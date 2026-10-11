using Zentriq.Domain.Licensing;

namespace Zentriq.Application.Licensing;

public sealed record TrialSnapshot(DateTimeOffset StartedAt, DateTimeOffset ExpiresAt, TrialPolicy Policy, bool IntegrityValid);

public sealed record ActiveLicenseSnapshot(LicensePayload Payload);

public sealed record LicenseInputs(
    bool Initialized,
    TrialSnapshot? Trial,
    ActiveLicenseSnapshot? License,
    DateTimeOffset NowUtc,
    DateTimeOffset LastSeenUtc,
    TimeSpan RollbackTolerance);

public sealed record LicenseStatus(
    LicenseMode Mode,
    string Summary,
    DateTimeOffset? ExpiresAt,
    int? DaysRemaining,
    IReadOnlyList<string> Modules,
    IReadOnlyDictionary<string, int> Limits,
    string? LicenseId,
    string? Customer)
{
    /// <summary>Feature APIs (scans, inventory changes, reports...) are usable.</summary>
    public bool FeaturesEnabled => Mode is LicenseMode.Trial or LicenseMode.Licensed or LicenseMode.LicensedGrace;

    public bool ModuleEnabled(string module) =>
        FeaturesEnabled && (Modules.Contains("*") || Modules.Contains(module));
}

/// <summary>
/// Pure licensing policy. Clock-tampering policy (documented in docs/licensing.md):
/// the database keeps a monotonic high-water mark of observed UTC time (install_state.last_seen_utc).
/// If the current clock is earlier than that mark by more than the tolerance (default 24 h),
/// the installation enters <see cref="LicenseMode.ClockRollback"/>: data stays readable, backup,
/// restore and license import keep working, but feature APIs are blocked until the clock is fixed.
/// Trial and license expiry are always computed against max(now, high-water mark), so moving the
/// clock back can never extend a trial or license.
/// </summary>
public static class LicenseEvaluator
{
    private static readonly IReadOnlyDictionary<string, int> NoLimits = new Dictionary<string, int>();

    public static LicenseStatus Evaluate(LicenseInputs i)
    {
        if (!i.Initialized || i.Trial is null)
            return new(LicenseMode.Uninitialized, "Setup has not been completed.", null, null, [], NoLimits, null, null);

        if (i.NowUtc < i.LastSeenUtc - i.RollbackTolerance)
            return new(LicenseMode.ClockRollback,
                "The system clock is behind the last time Zentriq observed. Correct the server time to continue.",
                null, null, [], NoLimits, i.License?.Payload.LicenseId, i.License?.Payload.Customer);

        var effectiveNow = i.NowUtc > i.LastSeenUtc ? i.NowUtc : i.LastSeenUtc;

        if (i.License is { } lic)
        {
            var p = lic.Payload;
            var limits = p.Entitlements;
            if (effectiveNow < p.ExpiresAt)
                return new(LicenseMode.Licensed, $"Licensed to {p.Customer}.", p.ExpiresAt, DaysLeft(p.ExpiresAt, effectiveNow), p.Modules, limits, p.LicenseId, p.Customer);
            var graceEnd = p.ExpiresAt.AddDays(p.GraceDays);
            if (effectiveNow < graceEnd)
                return new(LicenseMode.LicensedGrace, $"License expired; grace period ends {graceEnd:yyyy-MM-dd}.", graceEnd, DaysLeft(graceEnd, effectiveNow), p.Modules, limits, p.LicenseId, p.Customer);
            return new(LicenseMode.LicenseExpired, "The commercial license has expired. Import a renewed license to continue.", p.ExpiresAt, 0, [], limits, p.LicenseId, p.Customer);
        }

        var t = i.Trial;
        if (!t.IntegrityValid)
            return new(LicenseMode.TrialExpired, "Trial record failed its integrity check. Import a license to continue.", t.ExpiresAt, 0, [], t.Policy.Limits, null, null);
        if (effectiveNow < t.ExpiresAt)
            return new(LicenseMode.Trial, $"Trial: {DaysLeft(t.ExpiresAt, effectiveNow)} day(s) remaining.", t.ExpiresAt, DaysLeft(t.ExpiresAt, effectiveNow), t.Policy.Modules, t.Policy.Limits, null, null);
        return new(LicenseMode.TrialExpired, "The 14-day trial has ended. Import a license to continue. Your data is retained.", t.ExpiresAt, 0, [], t.Policy.Limits, null, null);
    }

    private static int DaysLeft(DateTimeOffset end, DateTimeOffset now) =>
        (int)Math.Max(0, Math.Ceiling((end - now).TotalDays));
}
