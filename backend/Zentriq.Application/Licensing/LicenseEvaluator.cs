using Zentriq.Domain;
using Zentriq.Domain.Licensing;

namespace Zentriq.Application.Licensing;

public sealed record TrialSnapshot(DateTimeOffset StartedAt, DateTimeOffset ExpiresAt, TrialPolicy Policy, bool IntegrityValid);

public sealed record ActiveLicenseSnapshot(LicensePayload Payload);

/// <param name="ReleaseDate">Release date of this Zentriq build (perpetual licenses: support must cover it).</param>
/// <param name="Usage">Current usage per metric; needed to decide whether the free tier applies.</param>
public sealed record LicenseInputs(
    bool Initialized,
    TrialSnapshot? Trial,
    ActiveLicenseSnapshot? License,
    DateTimeOffset NowUtc,
    DateTimeOffset LastSeenUtc,
    TimeSpan RollbackTolerance,
    DateTimeOffset? ReleaseDate = null,
    IReadOnlyDictionary<string, int>? Usage = null);

public sealed record LicenseStatus(
    LicenseMode Mode,
    string Summary,
    DateTimeOffset? ExpiresAt,
    int? DaysRemaining,
    IReadOnlyList<string> Modules,
    IReadOnlyDictionary<string, int> Limits,
    string? LicenseId,
    string? Customer,
    string? Edition = null,
    IReadOnlyList<string>? Sections = null,
    string? LicenseType = null,
    DateTimeOffset? SupportUntil = null,
    string? Activation = null,
    DateTimeOffset? LeaseUntil = null,
    string? Warning = null)
{
    /// <summary>Installed product sections (all when not recorded).</summary>
    public IReadOnlyList<string> InstalledSections => Sections ?? Zentriq.Domain.ProductSections.All;

    /// <summary>A section is usable when it is installed and the trial or license covers at least one of its modules.</summary>
    public bool SectionEnabled(string section) =>
        InstalledSections.Contains(section) && Zentriq.Domain.ProductSections.ModulesOf.TryGetValue(section, out var m) && m.Any(ModuleEnabled);

    /// <summary>
    /// The same status limited to the sections installed on this server: a module of a section that was not installed is
    /// not available, whatever the license says.
    /// </summary>
    public LicenseStatus RestrictTo(IReadOnlyList<string> installed)
    {
        var all = Zentriq.Domain.ProductSections.All;
        var allowed = Zentriq.Domain.ProductSections.ModulesFor(installed);
        IReadOnlyList<string> modules = Modules.Contains("*")
            ? (all.All(installed.Contains) ? Modules : allowed)
            : Modules.Where(allowed.Contains).ToList();
        return this with { Modules = modules, Sections = installed };
    }

    /// <summary>Feature APIs (scans, inventory changes, reports...) are usable.</summary>
    public bool FeaturesEnabled => Mode is LicenseMode.Trial or LicenseMode.Licensed or LicenseMode.LicensedGrace or LicenseMode.Free;

    /// <summary>Limits are exact (no overage grace) for the trial and the free tier.</summary>
    public bool ExactLimits => Mode is LicenseMode.Trial or LicenseMode.Free or LicenseMode.FreeOverLimit;

    public bool ModuleEnabled(string module) =>
        FeaturesEnabled && (Modules.Contains("*") || Modules.Contains(module));
}

/// <summary>
/// Pure licensing policy (docs/licensing.md).
/// <list type="bullet">
/// <item>Clock tampering: the database keeps a monotonic high-water mark of observed UTC time. If the clock is earlier than
/// that mark by more than the tolerance (default 24 h) feature APIs are blocked until the clock is fixed; trial and
/// license expiry are computed against max(now, high-water mark).</item>
/// <item>A license applies until it expires (plus grace). Perpetual licenses do not expire, but need support that covers
/// the release date of the running version. Online licenses must be refreshed by a check-in (lease) — 30 days of grace.</item>
/// <item>Without a valid license or trial the free tier applies, as long as the installation stays within its limits.</item>
/// </list>
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

        var now = i.NowUtc > i.LastSeenUtc ? i.NowUtc : i.LastSeenUtc;

        if (i.License is { } lic)
        {
            var p = lic.Payload;
            var edition = p.Edition?.ToLowerInvariant();
            var modules = Editions.Expand(p.Edition, p.Modules);
            var perpetual = p.LicenseType == LicenseFormats.Perpetual;
            LicenseStatus Make(LicenseMode mode, string summary, DateTimeOffset? until, IReadOnlyList<string> mods, string? warning = null) =>
                new(mode, summary, until, until is { } u ? DaysLeft(u, now) : null, mods, p.Entitlements, p.LicenseId, p.Customer, edition,
                    LicenseType: p.LicenseType ?? LicenseFormats.Subscription, SupportUntil: p.SupportUntil, Activation: p.Activation ?? LicenseFormats.Offline,
                    LeaseUntil: p.LeaseUntil, Warning: warning);

            if (perpetual && p.SupportUntil is { } support && i.ReleaseDate is { } released && released > support.AddDays(1))
                return Make(LicenseMode.SupportExpired,
                    $"Support for this perpetual license ended on {support:yyyy-MM-dd}, before this Zentriq version was released ({released:yyyy-MM-dd}). " +
                    "Renew support (the license updates itself at the next check-in) or install a version released before that date.", support, []);

            string? warning = null;
            if (p.LeaseUntil is { } lease)
            {
                var hardEnd = lease.AddDays(LicenseFormats.LeaseGraceDays);
                if (now >= hardEnd)
                    return Make(LicenseMode.LeaseExpired,
                        $"The license could not be refreshed from the Zentriq license server since {lease:yyyy-MM-dd}. Allow this server to reach the license server " +
                        "(Licensing › Check now), or activate offline with a license file.", hardEnd, []);
                if (now >= lease)
                    warning = $"The license server has not been reached since the license was last refreshed. Features stop on {hardEnd:yyyy-MM-dd} unless a check-in succeeds.";
            }

            var title = Editions.Title(p.Edition);
            if (perpetual)
            {
                var supportNote = p.SupportUntil is { } su ? (now < su ? $" Support until {su:yyyy-MM-dd}." : $" Support ended {su:yyyy-MM-dd}: newer versions need renewed support.") : "";
                if (p.SupportUntil is { } su2 && now >= su2 && warning is null)
                    warning = $"Support ended on {su2:yyyy-MM-dd}. This version keeps working; updates released later need renewed support.";
                return Make(LicenseMode.Licensed, $"Licensed to {p.Customer} ({title} edition, perpetual).{supportNote}", p.SupportUntil, modules, warning);
            }
            if (now < p.ExpiresAt)
                return Make(LicenseMode.Licensed, $"Licensed to {p.Customer} ({title} edition{(p.TermYears is { } y ? $", {y}-year subscription" : "")}).", p.ExpiresAt, modules, warning);
            var graceEnd = p.ExpiresAt.AddDays(p.GraceDays);
            if (now < graceEnd)
                return Make(LicenseMode.LicensedGrace, $"The license expired on {p.ExpiresAt:yyyy-MM-dd}; the grace period ends {graceEnd:yyyy-MM-dd}.", graceEnd, modules,
                    "Renew the subscription before the grace period ends.");
            return FreeTier(i, $"The license expired on {p.ExpiresAt:yyyy-MM-dd}. ", p.LicenseId, p.Customer);
        }

        var t = i.Trial;
        if (t.IntegrityValid && now < t.ExpiresAt)
        {
            var policy = t.Policy.WithCurrentLimits();
            return new(LicenseMode.Trial, $"Trial: {DaysLeft(t.ExpiresAt, now)} day(s) remaining.", t.ExpiresAt, DaysLeft(t.ExpiresAt, now), policy.Modules, policy.Limits, null, null, "trial");
        }
        return FreeTier(i, t.IntegrityValid ? "The trial has ended. " : "The trial record failed its integrity check. ", null, null);
    }

    /// <summary>The free tier, or — when the installation holds more than it allows — blocked features with the reason.</summary>
    private static LicenseStatus FreeTier(LicenseInputs i, string prefix, string? licenseId, string? customer)
    {
        var limits = Plans.FreeLimits;
        var over = i.Usage is null ? [] : limits
            .Where(l => l.Key != Workloads.Technicians && i.Usage.TryGetValue(l.Key, out var used) && used > l.Value)
            .Select(l => $"{i.Usage[l.Key]} {Workloads.Title(l.Key).ToLowerInvariant()} (free: {l.Value})").ToList();
        if (over.Count > 0)
            return new(LicenseMode.FreeOverLimit,
                $"{prefix}This installation holds more than the free edition allows: {string.Join(", ", over)}. Activate a license, or retire assets to stay within the free limits. Your data is kept.",
                null, null, [], limits, licenseId, customer, "free");
        return new(LicenseMode.Free,
            $"{prefix}Free edition: up to {limits[Workloads.TotalDevices]} IT devices, {limits[Workloads.NonItAssets]} non-IT assets and {limits[Workloads.CloudInstances]} cloud or virtual instances; IT accessories unlimited.",
            null, null, Plans.FreeModules, limits, licenseId, customer, "free");
    }

    private static int DaysLeft(DateTimeOffset end, DateTimeOffset now) =>
        (int)Math.Max(0, Math.Ceiling((end - now).TotalDays));
}
