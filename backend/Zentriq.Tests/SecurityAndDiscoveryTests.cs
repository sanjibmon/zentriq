using Zentriq.Application.Discovery;
using Zentriq.Application.Security;

namespace Zentriq.Tests;

public sealed class RedactorTests
{
    [Theory]
    [InlineData("Host=db;Username=zentriq_app;Password=S3cret!value;Database=zentriq", "S3cret!value")]
    [InlineData("{\"password\":\"hunter2hunter2\"}", "hunter2hunter2")]
    [InlineData("--superpassword=Abc123xyz", "Abc123xyz")]
    [InlineData("ALTER ROLE x PASSWORD 'SCRAM-SHA-256$4096:abc$def:ghi'", "4096:abc$def:ghi")]
    public void Secrets_are_removed(string input, string secret)
    {
        var r = Redactor.Redact(input);
        Assert.DoesNotContain(secret, r);
        Assert.Contains(Redactor.Mask, r);
    }

    [Fact]
    public void Private_keys_are_removed() =>
        Assert.DoesNotContain("MIGHAgEA", Redactor.Redact("-----BEGIN PRIVATE KEY-----\nMIGHAgEA\n-----END PRIVATE KEY-----"));
}

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("short1A!", false)]
    [InlineData("alllowercaseletters", false)]
    [InlineData("Correct-Horse-42", true)]
    [InlineData("Admin-Password-1", true)]
    public void Policy(string pw, bool ok) => Assert.Equal(ok, PasswordPolicy.Validate(pw) is null);

    [Fact]
    public void Password_must_not_contain_account_name() =>
        Assert.NotNull(PasswordPolicy.Validate("Jsmith-Password-1", "jsmith@example.com"));
}

public sealed class TargetParserTests
{
    private static readonly ScanSafeguards Open = new(["10.0.0.0/8", "192.168.0.0/16"], 65536, 16);

    [Fact]
    public void Cidr_with_exclusion_counts_addresses()
    {
        var p = TargetParser.Parse("10.1.2.0/24", "10.1.2.10", Open);
        Assert.True(p.IsValid, string.Join("; ", p.Errors));
        Assert.Equal(256, p.Items.Sum(i => i.Count));
        Assert.DoesNotContain("10.1.2.10", TargetParser.Expand(p, 1000));
    }

    [Fact]
    public void Range_single_ip_and_hostname_are_accepted()
    {
        var p = TargetParser.Parse("192.168.1.10-192.168.1.20\n192.168.5.5\nsrv01.corp.example.com", null, Open);
        Assert.True(p.IsValid, string.Join("; ", p.Errors));
        Assert.Equal(3, p.Items.Count);
        Assert.Contains(p.Items, i => i.IsHost);
    }

    [Fact]
    public void Out_of_scope_and_too_broad_targets_are_rejected()
    {
        Assert.False(TargetParser.Parse("8.8.8.8", null, Open).IsValid);
        Assert.False(TargetParser.Parse("10.0.0.0/8", null, Open).IsValid);
    }

    [Fact]
    public void Garbage_is_rejected() => Assert.False(TargetParser.Parse("10.1.2.300", null, Open).IsValid);
}
