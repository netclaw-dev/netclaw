// -----------------------------------------------------------------------
// <copyright file="ModelCapabilityResolutionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class ModelCapabilityResolutionTests
{
    [Fact]
    public void ResolveModelCapabilities_UsesConfiguredContextWindowAsClamp()
    {
        var models = new ModelSelection
        {
            Main = new ModelReference
            {
                ContextWindow = 32768,
                InputModalities = ModelModality.Text,
                OutputModalities = ModelModality.Text
            }
        };
        var detected = new ResolvedModelCapabilities("model", ModelModality.Text, ModelModality.Text, 65536);

        var result = ModelCapabilityResolution.ResolveModelCapabilities(models, detected);

        Assert.Equal(32768, result.ContextWindowTokens);
        Assert.Equal(ModelModality.Text, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Fact]
    public void ResolveModelCapabilities_HonorsConfiguredContextAndWarnsWhenItExceedsDetectedWindow()
    {
        var models = new ModelSelection
        {
            Main = new ModelReference
            {
                ContextWindow = 131072,
                InputModalities = ModelModality.Text,
                OutputModalities = ModelModality.Text
            }
        };
        var detected = new ResolvedModelCapabilities("model", ModelModality.Text, ModelModality.Text, 65536);
        var logger = new CapturingLogger();

        var result = ModelCapabilityResolution.ResolveModelCapabilities(
            models, detected, logger: logger);

        // Provider-reported windows are unreliable, so the operator's value wins
        // and we warn instead of refusing to boot the daemon.
        Assert.Equal(131072, result.ContextWindowTokens);
        var (level, message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("131072", message);
        Assert.Contains("65536", message);
    }

    [Fact]
    public void ResolveModelCapabilities_DoesNotWarnWhenConfiguredContextWithinDetectedWindow()
    {
        var models = new ModelSelection
        {
            Main = new ModelReference
            {
                ContextWindow = 32768,
                InputModalities = ModelModality.Text,
                OutputModalities = ModelModality.Text
            }
        };
        var detected = new ResolvedModelCapabilities("model", ModelModality.Text, ModelModality.Text, 65536);
        var logger = new CapturingLogger();

        var result = ModelCapabilityResolution.ResolveModelCapabilities(
            models, detected, logger: logger);

        Assert.Equal(32768, result.ContextWindowTokens);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void ResolveModelCapabilities_UsesDetectedContextWhenNoClampConfigured()
    {
        var models = new ModelSelection
        {
            Main = new ModelReference()
        };
        var detected = new ResolvedModelCapabilities("model", ModelModality.Text | ModelModality.Image, ModelModality.Text, 65536);

        var result = ModelCapabilityResolution.ResolveModelCapabilities(models, detected);

        Assert.Equal(65536, result.ContextWindowTokens);
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Fact]
    public void ResolveModelCapabilities_IgnoresDetectedZeroContextWhenConfiguredContextSet()
    {
        var models = new ModelSelection
        {
            Main = new ModelReference
            {
                ContextWindow = 120_000
            }
        };
        var detected = new ResolvedModelCapabilities("model", ModelModality.Text, ModelModality.Text, 0);

        var result = ModelCapabilityResolution.ResolveModelCapabilities(models, detected);

        Assert.Equal(120_000, result.ContextWindowTokens);
    }

    [Fact]
    public void ResolveModelCapabilities_UsesDefaultContextWhenDetectedContextIsZero()
    {
        var models = new ModelSelection
        {
            Main = new ModelReference()
        };
        var detected = new ResolvedModelCapabilities("model", ModelModality.Text, ModelModality.Text, 0);

        var result = ModelCapabilityResolution.ResolveModelCapabilities(models, detected);

        Assert.Equal(32_768, result.ContextWindowTokens);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }
}
