using System.Net;
using Zentriq.Infrastructure.Remote;
using Zentriq.Inventory;

namespace Zentriq.Tests;

/// <summary>Short enrollment codes, the server's enrollment proof, one-hour uninstall codes and the internet address checks.</summary>
public sealed class AgentCodesTests
{
    [Fact]
    public void Enrollment_codes_are_20_characters_and_forgiving_when_typed()
    {
        for (var i = 0; i < 200; i++)
        {
            var c = AgentCodes.NewEnrollmentCode();
            Assert.Matches("^[0-9A-HJKMNP-TV-Z]{20}$", c);
            Assert.Equal(c, AgentCodes.NormalizeEnrollment(AgentCodes.Group(c)));
            Assert.Equal(c, AgentCodes.NormalizeEnrollment(" " + AgentCodes.Group(c).ToLowerInvariant() + " "));
        }
        Assert.Equal("AB12C-DE34F-GH56J-KM78N", AgentCodes.Group("AB12CDE34FGH56JKM78N"));
        Assert.Equal("0123456789ABCDEFGHJK", AgentCodes.NormalizeEnrollment("O123456789abcdefghjk"));   // O → 0
        Assert.Equal("1123456789ABCDEFGHJK", AgentCodes.NormalizeEnrollment("I123456789ABCDEFGHJK"));   // I → 1
        Assert.Null(AgentCodes.NormalizeEnrollment("ABC"));
        Assert.Null(AgentCodes.NormalizeEnrollment("AB12C-DE34F-GH56J-KM78U"));                      // U is not used
        Assert.Null(AgentCodes.NormalizeEnrollment("ZQ1.eyJ2IjoxfQ"));
        Assert.Equal("AB12CD", AgentCodes.EnrollmentId("AB12CDE34FGH56JKM78N"));
    }

    [Fact]
    public void Enrollment_proof_binds_the_code_to_the_server_key()
    {
        var hash = AgentCodes.Sha256("AB12CDE34FGH56JKM78N");
        var proof = AgentCodes.EnrollProof(hash, "nonce-1234567890abcdef", ["pinA", "pinB"]);
        Assert.Equal(proof, AgentCodes.EnrollProof(hash, "nonce-1234567890abcdef", ["pinB", "pinA"]));          // order does not matter
        Assert.NotEqual(proof, AgentCodes.EnrollProof(hash, "nonce-1234567890abcdef", ["pinA", "pinX"]));        // another key
        Assert.NotEqual(proof, AgentCodes.EnrollProof(hash, "nonce-other-123456789", ["pinA", "pinB"]));         // replay
        Assert.NotEqual(proof, AgentCodes.EnrollProof(AgentCodes.Sha256("AB12CDE34FGH56JKM78P"), "nonce-1234567890abcdef", ["pinA", "pinB"]));
    }

    [Fact]
    public void Uninstall_codes_work_for_one_hour_on_one_device()
    {
        var key = AgentCodes.NewUninstallKey();
        var other = AgentCodes.NewUninstallKey();
        var now = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
        var code = AgentCodes.UninstallCode(key, now.Add(AgentCodes.UninstallValidity));
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{16}$", code);
        Assert.True(AgentCodes.VerifyUninstallCode(key, AgentCodes.Group(code, 4), now, out _));
        Assert.True(AgentCodes.VerifyUninstallCode(key, code.ToLowerInvariant(), now.AddMinutes(59), out _));
        Assert.False(AgentCodes.VerifyUninstallCode(key, code, now.AddMinutes(61), out var expired));
        Assert.Contains("expired", expired);
        Assert.False(AgentCodes.VerifyUninstallCode(other, code, now, out var wrong));                         // another device
        Assert.Contains("not valid for this device", wrong);
        var tampered = code[..6] + (code[6] == 'A' ? 'B' : 'A') + code[7..];
        Assert.False(AgentCodes.VerifyUninstallCode(key, tampered, now, out _));
        // A code for later than one hour ahead is refused (only the server can make codes, and only for one hour).
        var tooLong = AgentCodes.UninstallCode(key, now.AddHours(5));
        Assert.False(AgentCodes.VerifyUninstallCode(key, tooLong, now, out var notYet));
        Assert.Contains("not valid yet", notYet);
        Assert.False(AgentCodes.VerifyUninstallCode(key, "", now, out _));
        Assert.False(AgentCodes.VerifyUninstallCode(key, "ABCD-EFGH-JKMN-PQRS", now, out _));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("49.36.112.18", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.4", false)]
    [InlineData("100.64.1.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("203.0.113.10", false)]
    [InlineData("2001:4860:4860::8888", true)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    public void Public_addresses_are_recognised(string ip, bool isPublic) => Assert.Equal(isPublic, InternetCheck.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public void Internet_address_input_is_validated()
    {
        Assert.Null(InternetCheck.Validate("Support.Example.com", 8446, out var h));
        Assert.Equal("support.example.com", h);
        Assert.Null(InternetCheck.Validate("https://support.example.com:443/", 443, out h));
        Assert.Equal("support.example.com", h);
        Assert.Null(InternetCheck.Validate("49.36.112.18", 8446, out h));
        Assert.Contains("HTTPS only", InternetCheck.Validate("http://support.example.com", 8446, out _));
        Assert.Contains("HTTPS only", InternetCheck.Validate("support.example.com", 80, out _));
        Assert.Contains("private address", InternetCheck.Validate("192.168.1.4", 8446, out _));
        Assert.Contains("full DNS name", InternetCheck.Validate("zentriq-srv", 8446, out _));
        Assert.NotNull(InternetCheck.Validate("", 8446, out _));
    }

    [Fact]
    public async Task Private_names_are_reported_as_not_reachable_from_the_internet()
    {
        var r = await InternetCheck.RunAsync("localhost", 8446, 8446, default);
        Assert.False(r.Ok);
        Assert.False(r.Usable);
        Assert.False(r.PublicAddress);
        Assert.Contains("not reachable from the internet", r.Message);
        Assert.False((r with { ConfirmedFromOutside = true }).Usable);   // a private address cannot be confirmed
    }
}
