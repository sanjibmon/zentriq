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
    public void Standard_edition_covers_overview_inventory_infrastructure_and_insight()
    {
        var s = Licensed(Editions.Standard);
        Assert.True(s.ModuleEnabled(Modules.Lifecycle));
        Assert.True(s.ModuleEnabled(Modules.Cloud));
        Assert.True(s.SectionEnabled(ProductSections.Infrastructure));
        Assert.True(s.ModuleEnabled(Modules.Inventory));
        Assert.True(s.ModuleEnabled(Modules.Dashboards));
        Assert.True(s.ModuleEnabled(Modules.Reports));
        Assert.False(s.ModuleEnabled(Modules.Remote));
        Assert.False(s.ModuleEnabled(Modules.Vulnerability));
        Assert.True(s.SectionEnabled(ProductSections.Insight));
        Assert.False(s.SectionEnabled(ProductSections.Secure));
        Assert.False(s.SectionEnabled(ProductSections.Remote));
    }

    [Fact]
    public void Advanced_edition_covers_every_section_and_extra_modules_add_up()
    {
        var a = Licensed(Editions.Advanced);
        Assert.All(ProductSections.All, x => Assert.True(a.SectionEnabled(x), x));
        var plus = Licensed(Editions.Standard, Modules.Remote);
        Assert.True(plus.SectionEnabled(ProductSections.Remote));
        Assert.False(plus.SectionEnabled(ProductSections.Secure));
    }

    [Theory]
    [InlineData(null, 6)]
    [InlineData("", 6)]
    [InlineData("overview,discover,insight", 4)]     // standard sections (with Infrastructure) are always installed
    [InlineData("remote", 5)]
    [InlineData("[\"remote\",\"secure\"]", 6)]
    [InlineData("advanced", 6)]
    [InlineData("bogus", 4)]
    public void Installed_sections_are_parsed(string? stored, int count) => Assert.Equal(count, ProductSections.Parse(stored).Count);

    [Fact]
    public void Trial_is_limited_to_the_installed_sections()
    {
        var trial = new LicenseStatus(LicenseMode.Trial, "", null, 14, ["*"], new Dictionary<string, int>(), null, null, "trial");
        var standardInstall = trial.RestrictTo(ProductSections.Standard);
        Assert.True(standardInstall.ModuleEnabled(Modules.Inventory));
        Assert.False(standardInstall.ModuleEnabled(Modules.Remote));
        Assert.False(standardInstall.SectionEnabled(ProductSections.Remote));
        Assert.Equal(new[] { "*" }, trial.RestrictTo(ProductSections.All).Modules);

        // Advanced license on a Standard installation: still only what is installed.
        var adv = Licensed(Editions.Advanced).RestrictTo(ProductSections.Parse("remote"));
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
