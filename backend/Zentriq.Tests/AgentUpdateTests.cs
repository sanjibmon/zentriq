using Zentriq.Api.Endpoints;

namespace Zentriq.Tests;

public sealed class AgentUpdateTests
{
    [Theory]
    [InlineData("1.1.1", "1.1.1.35", true)]       // agents of builds before the build number report three parts
    [InlineData("1.1.1.29", "1.1.1.35", true)]
    [InlineData("1.1.1.35", "1.1.1.35", false)]
    [InlineData("1.1.2.1", "1.1.1.35", false)]    // never offer an older package
    [InlineData("1.0.2", "1.1.1.35", true)]
    [InlineData("unknown", "1.1.1.35", false)]
    [InlineData("1.1.1.29", null, false)]
    public void Offers_update_only_to_a_newer_package(string? agent, string? package, bool expected) =>
        Assert.Equal(expected, AgentEndpoints.IsOlder(agent, package));
}
