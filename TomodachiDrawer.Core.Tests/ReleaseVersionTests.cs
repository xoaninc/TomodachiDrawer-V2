using Xunit;

namespace TomodachiDrawer.Core.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.5.0", "0.6.0-rc.1", -1)]
    [InlineData("0.6.0", "0.6.0-rc.1", 1)]
    [InlineData("0.6.0-rc.2", "0.6.0-rc.1", 1)]
    [InlineData("0.6.0-rc.10", "0.6.0-rc.2", 1)]
    [InlineData("v0.6.0-rc.1+abcdef", "0.6.0-rc.1", 0)]
    [InlineData("0.6.1-rc.1", "0.6.0", 1)]
    [InlineData("0.6.0", "0.6.0+build123", 0)]
    public void Stable_and_candidate_updates_are_ordered(
        string latest,
        string current,
        int expected
    )
    {
        Assert.True(ReleaseVersion.TryCompare(latest, current, out int comparison));
        Assert.Equal(expected, Math.Sign(comparison));
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("0.6.0-rc.0")]
    [InlineData("0.6.0-preview")]
    [InlineData("999999999999.6.0")]
    public void Unknown_versions_do_not_trigger_an_update(string value)
    {
        Assert.False(ReleaseVersion.TryCompare(value, "0.5.0", out _));
        Assert.False(ReleaseVersion.TryCompare("0.5.0", value, out _));
    }
}
