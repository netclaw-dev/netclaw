// -----------------------------------------------------------------------
// <copyright file="SubAgentSpawnObservabilityTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// Regression coverage for sub-agent spawn observability. Each lifecycle/rejection breadcrumb is
/// an ordinary structured log call wrapped in a <c>SessionId</c> scope; the file-logger
/// partitions scoped lines into the spawning session's <c>session.log</c> (routing itself is
/// covered by <c>RollingFileLoggerPartitionTests</c>). These tests assert the producer side: a
/// refused or failed spawn still logs its real reason under the session's id, so it is not lost.
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
    public async Task Spawner_missing_session_context_logs_lifecycle_under_session_scope()
    {
        var logger = new RecordingLogger<SubAgentSpawner>();
        // Only the parent-side breadcrumb path runs before the early return, so the
        // unused collaborators are never dereferenced.
        var spawner = new SubAgentSpawner(
            chatClientProvider: null!,
            new ToolRegistry(),
            toolAccessPolicy: null!,
            approvalService: null,
            promptProvider: null!,
            workingContextSnapshots: new WorkingContextSnapshotProvider(NullLogger<WorkingContextSnapshotProvider>.Instance),
            logger);

        // A context with a session id but no SpawnChildActor factory — the
        // "subagent tried to spawn but never launched" failure shape.
        var context = new ToolExecutionContext(SessionId, null) { Audience = TrustAudience.Personal };

        var result = await spawner.SpawnAsync(
            Profile("summarizer"), "do the work", null, context, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        // The spawn attempt and its failure are both logged under the session's id, so the
        // file-logger routes them to that session's session.log.
        var requested = Assert.Single(logger.Entries, e => e.Message.Contains("spawn requested", StringComparison.Ordinal));
        Assert.Equal(SessionId, requested.SessionId);
        var noContext = Assert.Single(logger.Entries, e => e.Message.Contains("no session context available", StringComparison.Ordinal));
        Assert.Equal(SessionId, noContext.SessionId);
    }

    [Fact]
    public async Task Tool_refusal_logs_real_reason_under_session_scope()
    {
        var registry = new SubAgentDefinitionRegistry();
        var logger = new RecordingLogger<SpawnAgentTool>();
        var tool = new SpawnAgentTool(registry, spawner: null!, _paths, logger: logger);

        // Public audience is refused with a deliberately opaque model-facing string;
        // the operator-facing breadcrumb must still record the real reason.
        var context = new ToolExecutionContext(SessionId, null) { Audience = TrustAudience.Public };

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["agent"] = "summarizer", ["task"] = "do the work" },
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal("Error: This tool is not available.", result);
        var refused = Assert.Single(
            logger.Entries,
            e => e.Message.Contains("refused", StringComparison.Ordinal) && e.Message.Contains("Public", StringComparison.Ordinal));
        Assert.Equal(SessionId, refused.SessionId);
    }

    private static SubAgentProfile Profile(string name) => new()
    {
        Name = name,
        Description = "test agent",
        SystemPrompt = "You are a test agent.",
        ToolNames = ["file_read"],
        Visibility = SubAgentVisibility.UserFacing
    };

    // Records each log line with the SessionId on the scope active at emit time, so a test can
    // assert a specific breadcrumb was logged under the right session.
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<object?> _scopes = [];
        public List<(string Message, string? SessionId)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            _scopes.Add(state);
            return new Pop(_scopes);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((formatter(state, exception), ActiveSessionId()));

        private string? ActiveSessionId()
        {
            for (var i = _scopes.Count - 1; i >= 0; i--)
                if (_scopes[i] is IEnumerable<KeyValuePair<string, object>> kvps)
                    foreach (var kv in kvps)
                        if (kv.Key == NetclawLogProperties.SessionId && kv.Value is string s)
                            return s;
            return null;
        }

        private sealed class Pop(List<object?> scopes) : IDisposable
        {
            public void Dispose()
            {
                if (scopes.Count > 0)
                    scopes.RemoveAt(scopes.Count - 1);
            }
        }
    }
}
