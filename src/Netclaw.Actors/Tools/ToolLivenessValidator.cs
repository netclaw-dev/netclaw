// -----------------------------------------------------------------------
// <copyright file="ToolLivenessValidator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Startup guard against a silent liveness downgrade. A tool that declares
/// <see cref="ToolLivenessMode.SelfMonitoring"/> via its
/// <c>[NetclawTool(Liveness = …)]</c> attribute must also resolve to
/// SelfMonitoring at runtime (<see cref="INetclawTool.LivenessMode"/>). A silent
/// downgrade to <see cref="ToolLivenessMode.Opaque"/> would place the tool on a
/// wall-clock watchdog — for <c>spawn_agent</c> that means killing a healthy,
/// self-monitoring sub-agent mid-run. The source generator emits
/// <c>LivenessMode</c> directly from the attribute, so a mismatch means stale
/// generated code or a hand-rolled override; either way, fail loud at startup
/// rather than mis-supervising at runtime. (No silent fallbacks.)
/// </summary>
public static class ToolLivenessValidator
{
    public static void AssertSelfMonitoringConsistency(IEnumerable<INetclawTool> tools)
    {
        List<string>? mismatches = null;
        foreach (var tool in tools)
        {
            var declaredSelfMonitoring =
                tool.GetType().GetCustomAttribute<NetclawToolAttribute>()?.Liveness == ToolLivenessMode.SelfMonitoring;
            var resolvedSelfMonitoring = tool.LivenessMode == ToolLivenessMode.SelfMonitoring;

            // Both directions matter: a tool that declares SelfMonitoring but resolves
            // to a wall-clock mode would be killed mid-run, and a tool that resolves
            // SelfMonitoring without declaring it would be drained with NO watchdog at
            // all (the parent does not supervise self-monitoring tools). The declaration
            // and the runtime mode must agree exactly.
            if (declaredSelfMonitoring != resolvedSelfMonitoring)
            {
                (mismatches ??= []).Add(
                    $"{tool.Name} ({tool.GetType().Name}): declared "
                    + $"{(declaredSelfMonitoring ? "SelfMonitoring" : "non-SelfMonitoring")}, "
                    + $"resolved {tool.LivenessMode}");
            }
        }

        if (mismatches is not null)
        {
            throw new InvalidOperationException(
                "Tool liveness misconfiguration — the following tool(s) have a SelfMonitoring declaration that "
                + "disagrees with their resolved LivenessMode. A self-monitoring tool runs with NO parent watchdog, "
                + "so the declaration and the runtime mode must match exactly: " + string.Join("; ", mismatches)
                + ". This usually means stale generated code or a hand-rolled LivenessMode override.");
        }
    }
}
