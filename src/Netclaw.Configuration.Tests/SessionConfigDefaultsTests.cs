// -----------------------------------------------------------------------
// <copyright file="SessionConfigDefaultsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Bear-trap tests for <see cref="SessionConfig"/>, <see cref="SessionTuning"/>,
/// and <see cref="ModelCapabilities"/> defaults.
/// If you change a default, you must update these assertions —
/// forcing a deliberate decision rather than an accidental drift.
/// </summary>
public sealed class SessionConfigDefaultsTests
{
    [Fact]
    public void Deterministic_retrieval_enabled_by_default()
    {
        var tuning = new SessionTuning();
        Assert.True(tuning.DeterministicRetrievalEnabled);
    }

    [Fact]
    public void Compaction_threshold_is_75_percent()
    {
        var tuning = new SessionTuning();
        Assert.Equal(0.75, tuning.CompactionThreshold);
    }

    [Fact]
    public void Context_window_defaults_to_32k()
    {
        var capabilities = new ModelCapabilities();
        Assert.Equal(32_768, capabilities.ContextWindowTokens);
    }

    [Fact]
    public void Idle_timeout_defaults_to_30_minutes()
    {
        var config = new SessionConfig();
        Assert.Equal(TimeSpan.FromMinutes(30), config.IdleTimeout);
    }

    [Fact]
    public void Max_tool_iterations_per_turn_defaults_to_60()
    {
        var config = new SessionConfig();
        Assert.Equal(60, config.MaxToolIterationsPerTurn);
    }

    [Fact]
    public void Timeout_resume_retry_budget_defaults_to_2()
    {
        var tuning = new SessionTuning();
        Assert.Equal(2, tuning.TimeoutResumeRetryBudget);
    }

    [Fact]
    public void BindFromConfiguration_supports_legacy_root_level_tuning_keys()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Session:CompactionThreshold"] = "0.5",
                ["Session:SnapshotInterval"] = "7",
                ["Session:KeepRecentMessages"] = "4",
                ["Session:TitleGenerationInterval"] = "2"
            })
            .Build();

        var bound = SessionConfig.BindFromConfiguration(config.GetSection("Session"));

        Assert.Equal(0.5, bound.Tuning.CompactionThreshold);
        Assert.Equal(7, bound.Tuning.SnapshotInterval);
        Assert.Equal(4, bound.Tuning.KeepRecentMessages);
        Assert.Equal(2, bound.Tuning.TitleGenerationInterval);
    }

    [Fact]
    public void BindFromConfiguration_prefers_nested_tuning_values_over_legacy_root_keys()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Session:CompactionThreshold"] = "0.5",
                ["Session:Tuning:CompactionThreshold"] = "0.8"
            })
            .Build();

        var bound = SessionConfig.BindFromConfiguration(config.GetSection("Session"));

        Assert.Equal(0.8, bound.Tuning.CompactionThreshold);
    }

    /// <summary>
    /// Cross-Boundary Contract Rule round-trip: the config-file shape a user would
    /// write (validated against netclaw-config.v1.schema.json — see
    /// ConfigSchemaDoctorCheckTests.ReturnsPass_WhenSessionTuningTimeoutResumeRetryBudgetSet)
    /// must bind to the exact runtime value LlmSessionActor.TryResumeAfterTimeout
    /// reads via SessionConfig.Tuning.TimeoutResumeRetryBudget.
    /// </summary>
    [Fact]
    public void BindFromConfiguration_binds_TimeoutResumeRetryBudget()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Session:Tuning:TimeoutResumeRetryBudget"] = "5"
            })
            .Build();

        var bound = SessionConfig.BindFromConfiguration(config.GetSection("Session"));

        Assert.Equal(5, bound.Tuning.TimeoutResumeRetryBudget);
    }
}
