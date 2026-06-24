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
            var declared = tool.GetType().GetCustomAttribute<NetclawToolAttribute>()?.Liveness;
            if (declared == ToolLivenessMode.SelfMonitoring
                && tool.LivenessMode != ToolLivenessMode.SelfMonitoring)
            {
                (mismatches ??= []).Add(
                    $"{tool.Name} ({tool.GetType().Name}): declared SelfMonitoring, resolved {tool.LivenessMode}");
            }
        }

        if (mismatches is not null)
        {
            throw new InvalidOperationException(
                "Tool liveness misconfiguration — the following tool(s) declare SelfMonitoring but resolve to a "
                + "different mode, which would place them on a wall-clock watchdog and can kill a healthy "
                + "self-monitoring run: " + string.Join("; ", mismatches)
                + ". This usually means stale generated code; rebuild the tool's project.");
        }
    }
}
