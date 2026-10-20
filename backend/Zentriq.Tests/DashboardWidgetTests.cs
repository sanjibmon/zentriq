using System.Text.RegularExpressions;

namespace Zentriq.Tests;

/// <summary>The console's dashboard widgets must all be accepted by the API, otherwise saving a layout fails.</summary>
public sealed class DashboardWidgetTests
{
    private static string? RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var f = Path.Combine(dir.FullName, relative);
            if (File.Exists(f)) return f;
        }
        return null;
    }

    [Fact]
    public void Every_console_widget_type_is_accepted_by_the_api()
    {
        var tsx = RepoFile(Path.Combine("frontend", "src", "features", "dashboard", "DashboardPage.tsx"));
        var cs = RepoFile(Path.Combine("backend", "Zentriq.Api", "Endpoints", "DashboardEndpoints.cs"));
        Assert.NotNull(tsx);
        Assert.NotNull(cs);
        var console = Regex.Matches(File.ReadAllText(tsx!), @"\bid: '([a-z]+\.[A-Za-z]+)'").Select(m => m.Groups[1].Value).Distinct().ToList();
        var src = File.ReadAllText(cs!);
        var start = src.IndexOf("WidgetTypes =", StringComparison.Ordinal);
        var block = src[start..src.IndexOf("];", start, StringComparison.Ordinal)];
        var accepted = Regex.Matches(block, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.True(console.Count > 20, "widget list not found in DashboardPage.tsx");
        Assert.Empty(console.Where(id => !accepted.Contains(id)));
    }
}
