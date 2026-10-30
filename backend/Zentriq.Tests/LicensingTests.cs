using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zentriq.Application.Licensing;
using Zentriq.Domain;
using Zentriq.Domain.Licensing;

namespace Zentriq.Tests;

public sealed class LicenseVerifierTests
{
    private static readonly Guid Install = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly LicenseVerifier _verifier;

    public LicenseVerifierTests()
    {
        _verifier = new LicenseVerifier(new InMemoryPublicKeyStore([new LicensePublicKey("test-key", _key.ExportSubjectPublicKeyInfoPem(), null, false)]));
    }

    private static LicensePayload Payload(Guid? install = null, DateTimeOffset? expires = null, Dictionary<string, int>? ent = null) =>
        new(1, "ZQ-TEST-1", "Contoso", install ?? Install, Now.AddDays(-400), Now.AddDays(-400), expires ?? Now.AddDays(365), 14,
            ["*"], ent ?? new Dictionary<string, int> { ["endpoint"] = 100, ["server"] = 10 });

    private string Sign(LicensePayload p, string keyId = "test-key", ECDsa? key = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(p);
        var sig = (key ?? _key).SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.Serialize(new SignedDocument(LicenseFormats.License, keyId, LicenseFormats.Algorithm, Convert.ToBase64String(bytes), Convert.ToBase64String(sig)));
    }

    [Fact]
    public void Valid_license_is_accepted()
    {
        var r = _verifier.Verify(Sign(Payload()), Install, Now);
        Assert.True(r.IsValid, r.Message);
        Assert.Equal(100, r.Payload!.Entitlements["endpoint"]);
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var doc = JsonSerializer.Deserialize<SignedDocument>(Sign(Payload()))!;
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(doc.Payload)).Replace("\"endpoint\":100", "\"endpoint\":100000");
        var tampered = JsonSerializer.Serialize(doc with { Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) });
        Assert.Equal(LicenseRejection.BadSignature, _verifier.Verify(tampered, Install, Now).Rejection);
    }

    [Fact]
    public void License_signed_by_another_key_is_rejected()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(LicenseRejection.BadSignature, _verifier.Verify(Sign(Payload(), key: other), Install, Now).Rejection);
    }

    [Fact]
    public void Unknown_key_id_is_rejected() =>
        Assert.Equal(LicenseRejection.UnknownKey, _verifier.Verify(Sign(Payload(), keyId: "nope"), Install, Now).Rejection);

    [Fact]
    public void License_for_another_installation_is_rejected() =>
        Assert.Equal(LicenseRejection.WrongInstallation, _verifier.Verify(Sign(Payload(install: Guid.NewGuid())), Install, Now).Rejection);

    [Fact]
    public void Expired_license_past_grace_is_rejected() =>
        Assert.Equal(LicenseRejection.Expired, _verifier.Verify(Sign(Payload(expires: Now.AddDays(-15))), Install, Now).Rejection);

    [Fact]
    public void Unknown_workload_is_rejected_after_signature_check() =>
        Assert.Equal(LicenseRejection.InvalidContent,
            _verifier.Verify(Sign(Payload(ent: new Dictionary<string, int> { ["printer"] = 5 })), Install, Now).Rejection);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"format\":\"zentriq-license\"}")]
    public void Malformed_documents_are_rejected(string doc) =>
        Assert.False(_verifier.Verify(doc, Install, Now).IsValid);

    [Fact]
    public void Embedded_production_keys_are_p256_public_keys()
    {
        var store = Zentriq.Infrastructure.Licensing.EmbeddedPublicKeys.Load();
        Assert.NotEmpty(store.All);
        foreach (var k in store.All)
        {
            Assert.DoesNotContain("PRIVATE", k.PublicKeyPem);
            using var e = InMemoryPublicKeyStore.Import(k);
            Assert.Equal(256, e.KeySize);
        }
    }
}

public sealed class TrialAndEvaluatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Trial_integrity_detects_changed_expiry()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var id = Guid.NewGuid();
        var mac = TrialIntegrity.Compute(secret, id, Start, Start.AddDays(14));
        Assert.True(TrialIntegrity.Verify(secret, id, Start, Start.AddDays(14), mac));
        Assert.False(TrialIntegrity.Verify(secret, id, Start, Start.AddDays(30), mac));
        Assert.False(TrialIntegrity.Verify(RandomNumberGenerator.GetBytes(32), id, Start, Start.AddDays(14), mac));
    }

    private static LicenseStatus Eval(DateTimeOffset now, DateTimeOffset? lastSeen = null, bool initialized = true, bool integrity = true) =>
        LicenseEvaluator.Evaluate(new LicenseInputs(initialized,
            initialized ? new TrialSnapshot(Start, Start.AddDays(14), TrialPolicy.Default, integrity) : null,
            null, now, lastSeen ?? now, TimeSpan.FromHours(24)));

    [Fact]
    public void Uninitialized_installation_has_no_trial() => Assert.Equal(LicenseMode.Uninitialized, Eval(Start, initialized: false).Mode);

    [Fact]
    public void Trial_is_active_for_14_days_then_expires()
    {
        Assert.Equal(LicenseMode.Trial, Eval(Start.AddDays(13.9)).Mode);
        // Afterwards the free tier applies (Standard modules), as long as the installation stays within its limits.
        var after = Eval(Start.AddDays(14).AddMinutes(1));
        Assert.Equal(LicenseMode.Free, after.Mode);
        Assert.True(after.ModuleEnabled(Modules.Inventory));
        Assert.False(after.ModuleEnabled(Modules.Remote));
        Assert.Equal(25, after.Limits[Workloads.TotalDevices]);
    }

    [Fact]
    public void Clock_rollback_cannot_extend_the_trial()
    {
        // Clock moved back beyond tolerance: features blocked.
        Assert.Equal(LicenseMode.ClockRollback, Eval(Start.AddDays(5), lastSeen: Start.AddDays(20)).Mode);
        // Within tolerance, expiry is computed against the high-water mark.
        Assert.Equal(LicenseMode.Free, Eval(Start.AddDays(14).AddHours(-1), lastSeen: Start.AddDays(14).AddHours(2)).Mode);
    }

    [Fact]
    public void Tampered_trial_record_gives_only_the_free_tier()
    {
        var s = Eval(Start.AddDays(1), integrity: false);
        Assert.Equal(LicenseMode.Free, s.Mode);
        Assert.False(s.ModuleEnabled(Modules.Vulnerability));
    }
}
