using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

public sealed class SubAgentConfigTests
{
    [Fact]
    public void Default_values_match_existing_hardcoded_timeouts()
    {
        var config = new SubAgentConfig();

        Assert.Equal(60, config.DefaultTimeoutSeconds);
        Assert.Equal(180, config.StoreMemoryTimeoutSeconds);
        Assert.Equal(30, config.SearchMemoriesTimeoutSeconds);
    }

    [Fact]
    public void Custom_values_override_defaults()
    {
        var config = new SubAgentConfig
        {
            DefaultTimeoutSeconds = 120,
            StoreMemoryTimeoutSeconds = 300,
            SearchMemoriesTimeoutSeconds = 45
        };

        Assert.Equal(120, config.DefaultTimeoutSeconds);
        Assert.Equal(300, config.StoreMemoryTimeoutSeconds);
        Assert.Equal(45, config.SearchMemoriesTimeoutSeconds);
    }
}
