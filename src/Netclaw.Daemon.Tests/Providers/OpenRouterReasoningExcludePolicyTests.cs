// -----------------------------------------------------------------------
// <copyright file="OpenRouterReasoningExcludePolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Providers.OpenRouter;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class OpenRouterReasoningExcludePolicyTests
{
    [Fact]
    public void InjectsReasoningExclude_IntoRequestBody()
    {
        var policy = new OpenRouterReasoningExcludePolicy();
        var body = new JsonObject
        {
            ["model"] = "anthropic/claude-sonnet-4-20250514",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" })
        };

        var result = PipelinePolicyTestHarness.RunSync(policy, body);

        Assert.NotNull(result);
        Assert.True(result!["reasoning"]?["exclude"]?.GetValue<bool>());
    }

    [Fact]
    public void PreservesExistingFields()
    {
        var policy = new OpenRouterReasoningExcludePolicy();
        var body = new JsonObject
        {
            ["model"] = "deepseek/deepseek-r1",
            ["temperature"] = 0.7,
            ["stream"] = true,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "think hard" })
        };

        var result = PipelinePolicyTestHarness.RunSync(policy, body);

        Assert.NotNull(result);
        Assert.Equal("deepseek/deepseek-r1", result!["model"]?.GetValue<string>());
        Assert.Equal(0.7, result["temperature"]?.GetValue<double>());
        Assert.True(result["stream"]?.GetValue<bool>());
        Assert.Single(result["messages"]!.AsArray());
    }

    [Fact]
    public void OverwritesExistingReasoningField()
    {
        var policy = new OpenRouterReasoningExcludePolicy();
        var body = new JsonObject
        {
            ["model"] = "test",
            ["reasoning"] = new JsonObject { ["effort"] = "high" }
        };

        var result = PipelinePolicyTestHarness.RunSync(policy, body);

        Assert.NotNull(result);
        // The policy overwrites any existing reasoning config
        Assert.True(result!["reasoning"]?["exclude"]?.GetValue<bool>());
    }

    [Fact]
    public void NoOps_WhenContentIsNull()
    {
        var policy = new OpenRouterReasoningExcludePolicy();
        var capture = new PipelinePolicyTestHarness.CapturePolicy();
        var message = PipelinePolicyTestHarness.CreateMessage(null);

        policy.Process(message, [policy, capture], 0);

        Assert.True(capture.WasCalled);
        Assert.Null(message.Request.Content);
    }
}
