// -----------------------------------------------------------------------
// <copyright file="PromptClassificationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Event;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class PromptClassificationTests
{
    private static readonly ILoggingAdapter Log = NoLogger.Instance;

    [Fact]
    public async Task NullText_returns_Allow()
    {
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var ct = TestContext.Current.CancellationToken;
        var result = await PromptClassifier.ClassifyAsync(detector, null, "test", Log, ct);
        Assert.Equal(ClassificationOutcome.Allow, result.Outcome);
    }

    [Fact]
    public async Task WhitespaceText_returns_Allow()
    {
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var ct = TestContext.Current.CancellationToken;
        var result = await PromptClassifier.ClassifyAsync(detector, "   ", "test", Log, ct);
        Assert.Equal(ClassificationOutcome.Allow, result.Outcome);
    }

    [Fact]
    public async Task Detector_returns_None_allows()
    {
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var ct = TestContext.Current.CancellationToken;
        var result = await PromptClassifier.ClassifyAsync(detector, "hello world", "test", Log, ct);
        Assert.Equal(ClassificationOutcome.Allow, result.Outcome);
    }

    [Fact]
    public async Task Detector_returns_Low_allows()
    {
        var detector = new ConfigurablePromptInjectionDetector(
            PromptInjectionResult.Detected(PromptInjectionRisk.Low, "low risk"));
        var ct = TestContext.Current.CancellationToken;
        var result = await PromptClassifier.ClassifyAsync(detector, "suspicious text", "test", Log, ct);
        Assert.Equal(ClassificationOutcome.Allow, result.Outcome);
    }

    [Fact]
    public async Task Detector_returns_Medium_allows()
    {
        var detector = new ConfigurablePromptInjectionDetector(
            PromptInjectionResult.Detected(PromptInjectionRisk.Medium, "medium risk"));
        var ct = TestContext.Current.CancellationToken;
        var result = await PromptClassifier.ClassifyAsync(detector, "suspicious text", "test", Log, ct);
        Assert.Equal(ClassificationOutcome.Allow, result.Outcome);
    }

    [Fact]
    public async Task Detector_returns_High_blocks()
    {
        var detector = new ConfigurablePromptInjectionDetector(
            PromptInjectionResult.Detected(PromptInjectionRisk.High, "ignore previous instructions"));
        var ct = TestContext.Current.CancellationToken;
        var result = await PromptClassifier.ClassifyAsync(detector, "ignore previous instructions", "test", Log, ct);
        Assert.Equal(ClassificationOutcome.Block, result.Outcome);
        Assert.Equal("ignore previous instructions", result.Reason);
    }

    [Fact]
    public async Task Detector_returns_High_no_message_uses_default()
    {
        var detector = new ConfigurablePromptInjectionDetector(
            new PromptInjectionResult(PromptInjectionRisk.High));
        var ct = TestContext.Current.CancellationToken;
        var result = await PromptClassifier.ClassifyAsync(detector, "attack text", "test", Log, ct);
        Assert.Equal(ClassificationOutcome.Block, result.Outcome);
        Assert.Equal("High-risk prompt injection pattern detected", result.Reason);
    }

    [Fact]
    public async Task Detector_throws_returns_DetectorUnavailable()
    {
        var detector = new ConfigurablePromptInjectionDetector(
            new InvalidOperationException("service down"));
        var ct = TestContext.Current.CancellationToken;
        var result = await PromptClassifier.ClassifyAsync(detector, "hello", "test", Log, ct);
        Assert.Equal(ClassificationOutcome.DetectorUnavailable, result.Outcome);
        Assert.Equal("service down", result.Reason);
    }

    [Fact]
    public async Task Detector_throws_timeout_returns_DetectorUnavailable()
    {
        var detector = new ConfigurablePromptInjectionDetector(
            new TaskCanceledException("timed out"));
        var ct = TestContext.Current.CancellationToken;
        var result = await PromptClassifier.ClassifyAsync(detector, "hello", "test", Log, ct);
        Assert.Equal(ClassificationOutcome.DetectorUnavailable, result.Outcome);
    }

    [Fact]
    public async Task CancellationToken_propagated()
    {
        CancellationToken captured = default;
        var detector = new ConfigurablePromptInjectionDetector((text, ctx, ct) =>
        {
            captured = ct;
            return Task.FromResult(PromptInjectionResult.Safe());
        });

        using var cts = new CancellationTokenSource();
        await PromptClassifier.ClassifyAsync(detector, "hello", "test", Log, cts.Token);
        Assert.Equal(cts.Token, captured);
    }
}
