// -----------------------------------------------------------------------
// <copyright file="SubAgentSpawnObservabilityTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// Regression coverage for sub-agent spawn observability. A sub-agent's own actor
/// logs go through Akka's async logger bridge, where the diagnostics AsyncLocal is
/// gone, so they never reach the per-session <c>session.log</c>. The spawn lifecycle
/// is instead recorded by parent-side breadcrumbs that must run while the parent
/// session scope is active — otherwise a refused or failed spawn is invisible in the
/// session transcript. These tests assert the scope is active at log time, which is
/// exactly the condition <c>RollingFileLoggerProvider</c> uses to route a line to
/// <c>session.log</c>.
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
    public async Task Spawner_missing_session_context_records_breadcrumbs_under_session_scope()
    {
        var logger = new CapturingLogger<SubAgentSpawner>();
        // Only the parent-side breadcrumb path runs before the early return, so the
        // unused collaborators are never dereferenced.
        var spawner = new SubAgentSpawner(
            chatClientProvider: null!,
            new ToolRegistry(),
            toolAccessPolicy: null!,
            approvalService: null,
            promptProvider: null!,
            logger);

        // A context with a session id but no SpawnChildActor factory — the
        // "subagent tried to spawn but never launched" failure shape.
        var context = new ToolExecutionContext(SessionId, null) { Audience = TrustAudience.Personal };

        var result = await spawner.SpawnAsync(
            Profile("summarizer"), "do the work", null, context, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        // The spawn attempt and its failure are both visible in the session transcript.
        Assert.Contains(logger.Entries, e => e.SessionScope == SessionId && e.Message.Contains("spawn requested"));
        Assert.Contains(logger.Entries, e => e.SessionScope == SessionId && e.Message.Contains("no session context available"));
    }

    [Fact]
    public async Task Tool_refusal_records_real_reason_under_session_scope()
    {
        var logger = new CapturingLogger<SpawnAgentTool>();
        var registry = new SubAgentDefinitionRegistry();
        var tool = new SpawnAgentTool(registry, spawner: null!, _paths, logger: logger);

        // Public audience is refused with a deliberately opaque model-facing string;
        // the operator-facing breadcrumb must still record the real reason.
        var context = new ToolExecutionContext(SessionId, null) { Audience = TrustAudience.Public };

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["agent"] = "summarizer", ["task"] = "do the work" },
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal("Error: This tool is not available.", result);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning
                 && e.SessionScope == SessionId
                 && e.Message.Contains("refused")
                 && e.Message.Contains("Public"));
    }

    private static SubAgentProfile Profile(string name) => new()
    {
        Name = name,
        Description = "test agent",
        SystemPrompt = "You are a test agent.",
        ToolNames = ["file_read"],
        Visibility = SubAgentVisibility.UserFacing
    };

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message, string? SessionScope)> Entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), SessionDiagnosticsContext.SessionId));
    }
}
