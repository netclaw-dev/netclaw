// -----------------------------------------------------------------------
// <copyright file="ToolLivenessValidatorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ToolLivenessValidatorTests
{
    [Fact]
    public void Passes_when_self_monitoring_declaration_matches_resolved_mode()
    {
        // A consistent self-monitoring tool plus a tool with no liveness attribute
        // (skipped) must not trip the guard.
        ToolLivenessValidator.AssertSelfMonitoringConsistency(
            [new ConsistentSelfMonitoringTool(), new UnattributedTool()]);
    }

    [Fact]
    public void Throws_when_a_self_monitoring_declared_tool_resolves_to_opaque()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ToolLivenessValidator.AssertSelfMonitoringConsistency([new DriftedTool()]));

        // Names the offending tool and the modes so the failure is actionable.
        Assert.Contains("DriftedTool", ex.Message);
        Assert.Contains("Opaque", ex.Message);
    }

    [Fact]
    public void Throws_when_a_tool_resolves_self_monitoring_without_declaring_it()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ToolLivenessValidator.AssertSelfMonitoringConsistency([new UndeclaredSelfMonitoringTool()]));

        // A tool self-monitoring at runtime but never declared would be drained with no
        // watchdog — the guard must catch this reverse direction too.
        Assert.Contains("UndeclaredSelfMonitoringTool", ex.Message);
        Assert.Contains("SelfMonitoring", ex.Message);
    }

    // Declares SelfMonitoring and resolves SelfMonitoring — consistent.
    [NetclawTool("good_sm", "consistent", Liveness = ToolLivenessMode.SelfMonitoring)]
    private sealed class ConsistentSelfMonitoringTool : StubTool
    {
        public override ToolLivenessMode LivenessMode => ToolLivenessMode.SelfMonitoring;
    }

    // Declares SelfMonitoring but resolves Opaque (the interface default) — the drift
    // the guard exists to catch (e.g. stale generated code).
    [NetclawTool("drifted_sm", "drift", Liveness = ToolLivenessMode.SelfMonitoring)]
    private sealed class DriftedTool : StubTool;

    // Resolves SelfMonitoring but does NOT declare it via the attribute — the reverse
    // drift: would be drained with no watchdog.
    private sealed class UndeclaredSelfMonitoringTool : StubTool
    {
        public override ToolLivenessMode LivenessMode => ToolLivenessMode.SelfMonitoring;
    }

    // No [NetclawTool] attribute and resolves Opaque — consistent; the validator skips it.
    private sealed class UnattributedTool : StubTool;

    private abstract class StubTool : INetclawTool
    {
        public virtual ToolLivenessMode LivenessMode => ToolLivenessMode.Opaque;
        public string Name => GetType().Name;
        public LlmFacingToolName LlmFacingName => LlmFacingToolName.FromCanonical(Name);
        public string Description => "stub";
        public string GrantCategory => "builtin";
        public System.Text.Json.JsonElement ParameterSchema => default;
        public AITool ToAITool() => AIFunctionFactory.Create(() => "", name: Name, description: Description);

        public Task<string> ExecuteAsync(
            IDictionary<string, object?>? arguments,
            ToolInvocationContext context,
            CancellationToken ct = default)
            => Task.FromResult(string.Empty);
    }
}
