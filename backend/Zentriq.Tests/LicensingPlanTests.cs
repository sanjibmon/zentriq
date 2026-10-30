using Zentriq.Application.Licensing;
using Zentriq.Domain;
using Zentriq.Domain.Licensing;

namespace Zentriq.Tests;

/// <summary>Build 93 licensing model: free tier, subscription / perpetual, online lease, keys and signing.</summary>
public sealed class LicensingPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Install = Guid.NewGuid();

    private static TrialSnapshot EndedTrial() => new(Now.AddDays(-60), Now.AddDays(-46), TrialPolicy.Default, true);
    private static TrialSnapshot RunningTrial() => new(Now.AddDays(-2), Now.AddDays(12), TrialPolicy.Default, true);

    private static LicensePayload Lic(string type = LicenseFormats.Subscription, DateTimeOffset? expires = null, DateTimeOffset? support = null,
        string activation = LicenseFormats.Offline, DateTimeOffset? lease = null) =>
        new(LicenseFormats.CurrentSchemaVersion, "ZQ-2026-PLAN01", "Acme", Install, Now.AddDays(-30), Now.AddDays(-30),
            type == LicenseFormats.Perpetual ? LicenseFormats.PerpetualExpiry : expires ?? Now.AddYears(1), 30, [],
            new Dictionary<string, int> { [Workloads.TotalDevices] = 500, [Workloads.CloudInstances] = 20, [Workloads.Technicians] = 2 },
            Edition: Editions.Advanced, LicenseType: type, TermYears: type == LicenseFormats.Subscription ? 1 : null,
            SupportUntil: support ?? Now.AddYears(1), Activation: activation, LeaseUntil: lease);

    private static LicenseStatus Eval(TrialSnapshot? trial, LicensePayload? lic, IReadOnlyDictionary<string, int>? usage = null, DateTimeOffset? release = null) =>
        LicenseEvaluator.Evaluate(new LicenseInputs(true, trial, lic is null ? null : new ActiveLicenseSnapshot(lic), Now, Now, TimeSpan.FromHours(24),
            release ?? Now.AddDays(-10), usage ?? new Dictionary<string, int>()));

    [Fact]
    public void Plans_match_the_price_list()
    {
        Assert.Equal(30, Plans.TrialLimits[Workloads.TotalDevices]);
        Assert.Equal(50, Plans.TrialLimits[Workloads.NonItAssets]);
        Assert.Equal(10, Plans.TrialLimits[Workloads.CloudInstances]);
        Assert.Equal(1, Plans.TrialLimits[Workloads.Technicians]);
        Assert.Equal(25, Plans.FreeLimits[Workloads.TotalDevices]);
        Assert.Equal(25, Plans.FreeLimits[Workloads.NonItAssets]);
        Assert.Equal(5, Plans.FreeLimits[Workloads.CloudInstances]);
        Assert.Equal(0, Plans.FreeLimits[Workloads.Technicians]);
        Assert.Equal([250, 500, 1000, 2000, 4000], Plans.NonItPacks);
        Assert.Equal([1, 3, 5], Plans.SubscriptionYears);
    }

    [Fact]
    public void Running_trial_has_the_trial_limits_and_exact_caps()
    {
        var s = Eval(RunningTrial(), null);
        Assert.Equal(LicenseMode.Trial, s.Mode);
        Assert.Equal(30, s.Limits[Workloads.TotalDevices]);
        Assert.True(s.ExactLimits);
    }

    [Fact]
    public void Ended_trial_without_license_falls_back_to_the_free_tier()
    {
        var s = Eval(EndedTrial(), null, new Dictionary<string, int> { [Workloads.TotalDevices] = 20 });
        Assert.Equal(LicenseMode.Free, s.Mode);
        Assert.True(s.FeaturesEnabled);
        Assert.Equal(Editions.Free, s.Edition);
        Assert.Equal(25, s.Limits[Workloads.TotalDevices]);
        Assert.True(s.ModuleEnabled(Modules.Inventory));
    }

    [Fact]
    public void Free_tier_over_its_limits_pauses_features()
    {
        var s = Eval(EndedTrial(), null, new Dictionary<string, int> { [Workloads.TotalDevices] = 26 });
        Assert.Equal(LicenseMode.FreeOverLimit, s.Mode);
        Assert.False(s.FeaturesEnabled);
    }

    [Fact]
    public void Subscription_applies_then_grace_then_free_tier()
    {
        Assert.Equal(LicenseMode.Licensed, Eval(EndedTrial(), Lic()).Mode);
        Assert.Equal(LicenseMode.LicensedGrace, Eval(EndedTrial(), Lic(expires: Now.AddDays(-5))).Mode);
        Assert.Equal(LicenseMode.Free, Eval(EndedTrial(), Lic(expires: Now.AddDays(-60))).Mode);
    }

    [Fact]
    public void Perpetual_needs_support_covering_the_running_release()
    {
        var ok = Eval(EndedTrial(), Lic(LicenseFormats.Perpetual, support: Now.AddDays(-1)), release: Now.AddDays(-30));
        Assert.Equal(LicenseMode.Licensed, ok.Mode);   // support ended, but this version was released before
        var newer = Eval(EndedTrial(), Lic(LicenseFormats.Perpetual, support: Now.AddDays(-30)), release: Now.AddDays(-1));
        Assert.Equal(LicenseMode.SupportExpired, newer.Mode);
        Assert.False(newer.FeaturesEnabled);
    }

    [Fact]
    public void Online_lease_warns_then_expires_after_its_grace()
    {
        var fresh = Eval(EndedTrial(), Lic(activation: LicenseFormats.Online, lease: Now.AddDays(20)));
        Assert.Equal(LicenseMode.Licensed, fresh.Mode);
        Assert.Null(fresh.Warning);
        var late = Eval(EndedTrial(), Lic(activation: LicenseFormats.Online, lease: Now.AddDays(-5)));
        Assert.Equal(LicenseMode.Licensed, late.Mode);
        Assert.False(string.IsNullOrEmpty(late.Warning));
        Assert.Equal(LicenseMode.LeaseExpired, Eval(EndedTrial(), Lic(activation: LicenseFormats.Online, lease: Now.AddDays(-31))).Mode);
    }

    [Fact]
    public void License_keys_normalize_and_hash_stably()
    {
        var k = LicenseKeys.New();
        Assert.Matches("^ZQL-[0-9A-Z]{5}-[0-9A-Z]{5}-[0-9A-Z]{5}-[0-9A-Z]{5}$", k);
        Assert.Equal(k, LicenseKeys.Normalize(k.ToLowerInvariant().Replace("-", " ")));
        Assert.Equal(LicenseKeys.Hash(k), LicenseKeys.Hash(LicenseKeys.Normalize(k)!));
        Assert.Null(LicenseKeys.Normalize("ZQL-1234"));
        Assert.Equal("ZQL-00000-11111-11111-11111", LicenseKeys.Normalize("zql-OOOOO-IIIII-LLLLL-11111"));
    }

    [Fact]
    public void Signed_schema3_license_round_trips_through_the_verifier()
    {
        var (priv, pub) = LicenseSigning.NewKeyPair();
        using var key = LicenseSigning.LoadPrivateKey(priv);
        var verifier = new LicenseVerifier(new InMemoryPublicKeyStore([new LicensePublicKey("k1", pub, null, false)]));
        var doc = LicenseSigning.Sign(Lic(LicenseFormats.Perpetual, activation: LicenseFormats.Online, lease: Now.AddDays(30)), key, "k1");
        var r = verifier.Verify(doc, Install, Now);
        Assert.True(r.IsValid, r.Message);
        Assert.Equal(LicenseFormats.Perpetual, r.Payload!.LicenseType);
        Assert.Equal(500, r.Payload.Entitlements[Workloads.TotalDevices]);

        // Perpetual licenses must state their support end.
        var bad = LicenseSigning.Sign(Lic(LicenseFormats.Perpetual) with { SupportUntil = null }, key, "k1");
        Assert.Equal(LicenseRejection.InvalidContent, verifier.Verify(bad, Install, Now).Rejection);
    }
}
