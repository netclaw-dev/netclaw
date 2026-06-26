// -----------------------------------------------------------------------
// <copyright file="SubAgentSpawnObservabilityTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// Regression coverage for sub-agent spawn observability. The spawn lifecycle is
/// published to the parent's <c>session.log</c> explicitly via
/// <see cref="ToolExecutionContext.EmitSessionLogLine"/> (the session wires it to the
/// session-log dispatcher). These tests capture those publishes and assert each
/// lifecycle/rejection breadcrumb is emitted; otherwise a refused or failed spawn
/// would be invisible in the session transcript.
/// </summary>
public sealed class SubAgentSpawnObservabilityTests : IDisposable
{
    private const string SessionId = "C123/167.42";

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public SubAgentSpawnObservabilityTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Spawner_missing_session_context_publishes_lifecycle_to_session_log()
    {
        // Only the parent-side breadcrumb path runs before the early return, so the
        // unused collaborators are never dereferenced.
        var spawner = new SubAgentSpawner(
            chatClientProvider: null!,
            new ToolRegistry(),
            toolAccessPolicy: null!,
            approvalService: null,
            promptProvider: null!,
            NullLogger<SubAgentSpawner>.Instance);

        var sessionLog = new List<string>();
        // A context with a session id but no SpawnChildActor factory — the
        // "subagent tried to spawn but never launched" failure shape.
        var context = new ToolExecutionContext(SessionId, null)
        {
            Audience = TrustAudience.Personal,
            EmitSessionLogLine = sessionLog.Add
        };

        var result = await spawner.SpawnAsync(
            Profile("summarizer"), "do the work", null, context, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        // The spawn attempt and its failure are both published to the session transcript.
        Assert.Contains(sessionLog, line => line.Contains("spawn requested", StringComparison.Ordinal));
        Assert.Contains(sessionLog, line => line.Contains("no session context available", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tool_refusal_publishes_real_reason_to_session_log()
    {
        var registry = new SubAgentDefinitionRegistry();
        var tool = new SpawnAgentTool(registry, spawner: null!, _paths, logger: NullLogger<SpawnAgentTool>.Instance);

        var sessionLog = new List<string>();
        // Public audience is refused with a deliberately opaque model-facing string;
        // the operator-facing breadcrumb must still record the real reason.
        var context = new ToolExecutionContext(SessionId, null)
        {
            Audience = TrustAudience.Public,
            EmitSessionLogLine = sessionLog.Add
        };

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["agent"] = "summarizer", ["task"] = "do the work" },
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal("Error: This tool is not available.", result);
        Assert.Contains(
            sessionLog,
            line => line.Contains("refused", StringComparison.Ordinal) && line.Contains("Public", StringComparison.Ordinal));
    }

    private static SubAgentProfile Profile(string name) => new()
    {
        Name = name,
        Description = "test agent",
        SystemPrompt = "You are a test agent.",
        ToolNames = ["file_read"],
        Visibility = SubAgentVisibility.UserFacing
    };
}
