using Zentriq.Application.Licensing;
using Zentriq.Domain;
using Zentriq.Domain.Licensing;

namespace Zentriq.Tests;

/// <summary>Standard / Advanced editions and installed product sections.</summary>
public sealed class EditionTests
{
    private static LicenseStatus Licensed(string? edition, params string[] modules) =>
        new(LicenseMode.Licensed, "", null, null, Editions.Expand(edition, modules), new Dictionary<string, int>(), "L1", "C", edition);

    [Fact]
    public void Standard_edition_covers_overview_inventory_security_pack_and_insight()
    {
        var s = Licensed(Editions.Standard);
        Assert.True(s.ModuleEnabled(Modules.Inventory));
        Assert.True(s.ModuleEnabled(Modules.Dashboards));
        Assert.True(s.ModuleEnabled(Modules.Reports));
        Assert.True(s.ModuleEnabled(Modules.SecurityPack));
        Assert.True(s.SectionEnabled(ProductSections.SecurityPack));
        Assert.True(s.SectionEnabled(ProductSections.Insight));
        Assert.False(s.ModuleEnabled(Modules.Cloud));
        Assert.False(s.ModuleEnabled(Modules.Lifecycle));
        Assert.False(s.SectionEnabled(ProductSections.Infrastructure));
        Assert.False(s.ModuleEnabled(Modules.Remote));
        Assert.False(s.ModuleEnabled(Modules.Vulnerability));
        Assert.False(s.SectionEnabled(ProductSections.Secure));
        Assert.False(s.SectionEnabled(ProductSections.Remote));
    }

    [Fact]
    public void Advanced_edition_adds_infrastructure_and_secure_and_remote_support_is_an_add_on()
    {
        var a = Licensed(Editions.Advanced);
        Assert.All(ProductSections.All.Where(x => x != ProductSections.Remote), x => Assert.True(a.SectionEnabled(x), x));
        Assert.False(a.SectionEnabled(ProductSections.Remote));
        Assert.True(Licensed(Editions.Advanced, Modules.Remote).SectionEnabled(ProductSections.Remote));
        var plus = Licensed(Editions.Standard, Modules.Remote);
        Assert.True(plus.SectionEnabled(ProductSections.Remote));
        Assert.False(plus.SectionEnabled(ProductSections.Secure));
        Assert.False(plus.SectionEnabled(ProductSections.Infrastructure));
        Assert.Equal("addon", ProductSections.OfferOf(ProductSections.Remote));
        Assert.Equal(Editions.Standard, ProductSections.OfferOf(ProductSections.SecurityPack));
        Assert.Equal(Editions.Advanced, ProductSections.OfferOf(ProductSections.Infrastructure));
    }

    [Fact]
    public void Licenses_issued_before_build_142_keep_what_their_edition_covered()
    {
        var before = Editions.Build142.AddDays(-30);
        Assert.Contains(Modules.Cloud, Editions.Expand(Editions.Standard, [], before));
        Assert.Contains(Modules.Lifecycle, Editions.Expand(Editions.Standard, [], before));
        Assert.Contains(Modules.Remote, Editions.Expand(Editions.Advanced, [], before));
        Assert.DoesNotContain(Modules.Cloud, Editions.Expand(Editions.Standard, [], Editions.Build142.AddDays(1)));
        Assert.DoesNotContain(Modules.Remote, Editions.Expand(Editions.Advanced, [], Editions.Build142.AddDays(1)));
    }

    [Fact]
    public void Free_tier_keeps_asset_management_without_security_or_remote()
    {
        var f = Zentriq.Domain.Licensing.Plans.FreeModules;
        Assert.Contains(Modules.Inventory, f);
        Assert.Contains(Modules.Cloud, f);
        Assert.DoesNotContain(Modules.SecurityPack, f);
        Assert.DoesNotContain(Modules.Remote, f);
        Assert.DoesNotContain(Modules.Vulnerability, f);
    }

    [Theory]
    [InlineData(null, 7)]
    [InlineData("", 7)]
    [InlineData("overview,discover,insight", 4)]     // standard sections (with Security Pack) are always installed
    [InlineData("overview,discover,infrastructure,insight", 5)]   // a Standard installation before build 142 keeps Infrastructure
    [InlineData("remote", 5)]
    [InlineData("[\"remote\",\"secure\"]", 6)]
    [InlineData("advanced", 7)]
    [InlineData("bogus", 4)]
    public void Installed_sections_are_parsed(string? stored, int count) => Assert.Equal(count, ProductSections.Parse(stored).Count);

    [Fact]
    public void Trial_is_limited_to_the_installed_sections()
    {
        var trial = new LicenseStatus(LicenseMode.Trial, "", null, 14, ["*"], new Dictionary<string, int>(), null, null, "trial");
        var standardInstall = trial.RestrictTo(ProductSections.Standard);
        Assert.True(standardInstall.ModuleEnabled(Modules.Inventory));
        Assert.True(standardInstall.ModuleEnabled(Modules.SecurityPack));
        Assert.False(standardInstall.ModuleEnabled(Modules.Remote));
        Assert.False(standardInstall.ModuleEnabled(Modules.Cloud));
        Assert.False(standardInstall.SectionEnabled(ProductSections.Remote));
        Assert.Equal(new[] { "*" }, trial.RestrictTo(ProductSections.All).Modules);

        // Advanced license with the add-on on a Standard installation plus Remote: still only what is installed.
        var adv = Licensed(Editions.Advanced, Modules.Remote).RestrictTo(ProductSections.Parse("remote"));
        Assert.True(adv.ModuleEnabled(Modules.Remote));
        Assert.False(adv.ModuleEnabled(Modules.Vulnerability));
    }

    [Fact]
    public void Expired_license_enables_nothing_whatever_is_installed()
    {
        var expired = new LicenseStatus(LicenseMode.LicenseExpired, "", null, 0, [], new Dictionary<string, int>(), "L", "C", Editions.Advanced).RestrictTo(ProductSections.All);
        Assert.All(ProductSections.All, x => Assert.False(expired.SectionEnabled(x)));
    }

    [Fact]
    public void Every_module_belongs_to_one_section() =>
        Assert.All(Modules.All, m => Assert.NotNull(ProductSections.SectionOf(m)));
}
