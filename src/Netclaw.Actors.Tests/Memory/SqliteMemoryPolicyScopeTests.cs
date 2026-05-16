// -----------------------------------------------------------------------
// <copyright file="SqliteMemoryPolicyScopeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class SqliteMemoryPolicyScopeTests : IAsyncDisposable
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "netclaw-sqlite-memory-policy-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly SQLiteMemoryStore _store;

    public SqliteMemoryPolicyScopeTests()
    {
        Directory.CreateDirectory(_baseDir);
        _dbPath = Path.Combine(_baseDir, "netclaw.db");
        _store = new SQLiteMemoryStore(_dbPath, TimeProvider.System);
    }

    [Fact]
    public async Task GetMemories_respects_explicit_context_policy_scope()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();

        await _store.ApplyCurationBatchAsync(
            "cp-policy-scope",
            [
                new SQLiteMemoryCurationOperation(
                    Kind: "document",
                    MemoryClass: "durable_fact",
                    MemoryId: "doc-public",
                    AnchorCanonicalName: "netclaw-public",
                    AnchorType: "project",
                    Title: "Public note",
                    Content: "This is public.",
                    AliasesJson: "[\"public\"]",
                    FacetsJson: "[\"project_fact\"]",
                    SlotsJson: null,
                    Relations: null,
                    UpdateSemantics: "merge-document",
                    Boundary: TrustBoundary.TrustedInstanceValue,
                    Audience: TrustAudience.Public,
                    Sensitivity: "normal",
                    RecallMode: "auto",
                    Confidence: 0.9,
                    FreshnessAtMs: now,
                    ExpiresAtMs: null),
                new SQLiteMemoryCurationOperation(
                    Kind: "document",
                    MemoryClass: "durable_fact",
                    MemoryId: "doc-team",
                    AnchorCanonicalName: "netclaw-team",
                    AnchorType: "project",
                    Title: "Team note",
                    Content: "This is team-only.",
                    AliasesJson: "[\"team\"]",
                    FacetsJson: "[\"project_fact\"]",
                    SlotsJson: null,
                    Relations: null,
                    UpdateSemantics: "merge-document",
                    Boundary: TrustBoundary.TrustedInstanceValue,
                    Audience: TrustAudience.Team,
                    Sensitivity: "normal",
                    RecallMode: "auto",
                    Confidence: 0.9,
                    FreshnessAtMs: now,
                    ExpiresAtMs: null)
            ],
            CancellationToken.None);

        var tool = new SqliteGetMemoriesTool(_store, logger: NullLogger<SqliteGetMemoriesTool>.Instance);
        var context = new ToolExecutionContext("signalr/thread-1", null)
        {
            Audience = TrustAudience.Public,
            Boundary = TrustBoundary.TrustedInstance
        };

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Ids"] = "doc:doc-public,doc:doc-team" },
            context,
            CancellationToken.None);

        Assert.Contains("Public note", result);
        Assert.DoesNotContain("Team note", result);
    }

    [Fact]
    public async Task StoreMemory_uses_explicit_context_policy_scope()
    {
        var sink = new CapturingCheckpointSink();
        var tool = new SqliteStoreMemoryTool(sink, NullLogger<SqliteStoreMemoryTool>.Instance);
        var context = new ToolExecutionContext("slack/thread-1", null)
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal
        };

        await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Title"] = "Travel profile",
                ["Content"] = "Remember that I prefer United."
            },
            context,
            CancellationToken.None);

        var request = Assert.Single(sink.Requests);
        var payload = Assert.IsType<MemoryCheckpointPayload>(request.Payload);
        Assert.Equal(TrustAudience.Personal.ToWireValue(), payload.Audience);
        Assert.Equal(TrustBoundary.PersonalValue, payload.Boundary);
    }

    public async ValueTask DisposeAsync()
    {
        await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(_baseDir);
    }

    private sealed class CapturingCheckpointSink : IMemoryCheckpointSink
    {
        public List<MemoryCheckpointRequest> Requests { get; } = [];

        public Task<MemoryCheckpointEnqueueResult> EnqueueAsync(MemoryCheckpointRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new MemoryCheckpointEnqueueResult("cp-test", 0));
        }
    }
}
