using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Bear-trap tests for <see cref="SessionConfig"/> defaults.
/// If you change a default, you must update these assertions —
/// forcing a deliberate decision rather than an accidental drift.
/// </summary>
public sealed class SessionConfigDefaultsTests
{
    [Fact]
    public void Memory_sidecars_enabled_by_default()
    {
        var config = new SessionConfig();
        Assert.True(config.MemorySidecarsEnabled);
    }

    [Fact]
    public void Deterministic_retrieval_enabled_by_default()
    {
        var config = new SessionConfig();
        Assert.True(config.DeterministicRetrievalEnabled);
    }

    [Fact]
    public void Compaction_threshold_is_75_percent()
    {
        var config = new SessionConfig();
        Assert.Equal(0.75, config.CompactionThreshold);
    }

    [Fact]
    public void Context_window_defaults_to_32k()
    {
        var config = new SessionConfig();
        Assert.Equal(32_768, config.ContextWindowTokens);
    }

    [Fact]
    public void Idle_timeout_defaults_to_30_minutes()
    {
        var config = new SessionConfig();
        Assert.Equal(TimeSpan.FromMinutes(30), config.IdleTimeout);
    }
}
