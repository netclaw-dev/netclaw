// -----------------------------------------------------------------------
// <copyright file="MemoryCheckpointAudienceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

/// <summary>
/// A queued memory checkpoint is a durable record. Before this change the
/// curation worker stored a checkpoint without an audience as a Public memory.
/// These tests pin the loud behavior: the worker drops the checkpoint and
/// writes an Error log that names the reason.
/// </summary>
public sealed class MemoryCheckpointAudienceTests : IAsyncDisposable
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), $"netclaw-memory-checkpoint-audience-tests-{Guid.NewGuid():N}");

    private readonly SQLiteMemoryStore _store;
    private readonly RecordingLogger _logger = new();
    private readonly MemoryCurationEngine _engine;

    public MemoryCheckpointAudienceTests()
    {
        Directory.CreateDirectory(_baseDir);
        _store = new SQLiteMemoryStore(Path.Combine(_baseDir, "netclaw.db"), TimeProvider.System);
        _engine = new MemoryCurationEngine(
            _store,
            new MemoryRulesFirstExtractor(new MemoryPolicyEvaluator()),
            new MemoryConfig(),
            _logger);
    }

    public async ValueTask DisposeAsync() => await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(_baseDir);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("everyone")]
    public async Task Checkpoint_with_missing_or_unknown_audience_is_dropped_with_an_error(string? audience)
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);

        var operations = await _engine.CurateAsync(
            Checkpoint(ExplicitRequest(audience)),
            TestContext.Current.CancellationToken);

        Assert.Empty(operations);
        var error = Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains(nameof(MemoryExtractionDropReason.AudienceUnresolved), error.Message);
        Assert.Contains(SecurityPolicyDefaults.AudienceUnresolvedReason, error.Message);
    }

    [Theory]
    [InlineData("public", TrustAudience.Public)]
    [InlineData("team", TrustAudience.Team)]
    [InlineData("personal", TrustAudience.Personal)]
    public async Task Checkpoint_with_explicit_audience_is_curated_under_that_audience(
        string wire,
        TrustAudience expected)
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);

        var operations = await _engine.CurateAsync(
            Checkpoint(ExplicitRequest(wire)),
            TestContext.Current.CancellationToken);

        var operation = Assert.Single(operations);
        Assert.Equal(expected, operation.Audience);
        Assert.DoesNotContain(_logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public void Allowed_audience_set_refuses_an_undefined_audience_value()
    {
        // The old switch arm returned the Public set for any undefined value.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryPolicyEvaluator.AllowedAudienceWireValues((TrustAudience)7));
        Assert.Equal(
            ["public", "team"],
            MemoryPolicyEvaluator.AllowedAudienceWireValues(TrustAudience.Team));
    }

    private static MemoryCheckpointPayload ExplicitRequest(string? audience) => new(
        SessionId: "signalr/checkpoint-audience",
        TriggerType: CheckpointTriggerType.ExplicitMemoryRequest.ToWireValue(),
        Source: "store_memory",
        Content: "The deploy runbook lives in the ops repository under runbooks/deploy.md.",
        UserContent: "Remember where the deploy runbook lives.",
        AssistantContent: null,
        IsExplicitRequest: true,
        HasVerifiedToolFinding: false,
        IsCompactionBoundary: false,
        HasAcceptedSubAgentFinding: false,
        Sensitivity: MemorySensitivity.Normal.ToWireValue(),
        RecallMode: MemoryRecallMode.Auto.ToWireValue(),
        Confidence: 0.95,
        Boundary: TrustBoundary.TrustedInstanceValue,
        Audience: audience);

    private static SQLiteMemoryCheckpoint Checkpoint(MemoryCheckpointPayload payload) => new(
        CheckpointId: $"cp-{Guid.NewGuid():N}",
        SessionId: payload.SessionId,
        TurnId: null,
        TriggerType: payload.TriggerType,
        Priority: 100,
        Status: "pending",
        PayloadJson: JsonSerializer.Serialize(payload),
        RetryCount: 0,
        CreatedAtMs: 0,
        UpdatedAtMs: 0);

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class RecordingLogger : ILogger<MemoryCurationEngine>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                    return [.. _entries];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }
}
