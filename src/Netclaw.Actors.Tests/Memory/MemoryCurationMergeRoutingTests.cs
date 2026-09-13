// -----------------------------------------------------------------------
// <copyright file="MemoryCurationMergeRoutingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Xunit;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netclaw.Actors.Tests.Memory;

/// <summary>
/// End-to-end coverage for memory-core-redesign Slice 3 task 3.4's guard-validated write
/// routing, exercised through the REAL <see cref="MemoryCurationEvaluator"/> +
/// <see cref="SQLiteMemoryStore"/> (not just the pure decision layer): a guard-failing or
/// body-absent LLM UPDATE/CONSOLIDATE decision must land as a structural append with
/// AppendDocument semantics, never a raw overwrite of the target's body, and the target's
/// original content must survive intact as a prefix. The deterministic tier's exact-anchor
/// UPDATE keeps its pre-Slice-3 raw-overwrite behavior — a regression guard proves that.
/// </summary>
public sealed class MemoryCurationMergeRoutingTests : IAsyncDisposable
{
    private static readonly SessionId TestSessionId = new("test-channel/merge-routing");

    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "netclaw-curation-merge-routing-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly SQLiteMemoryStore _store;

    public MemoryCurationMergeRoutingTests()
    {
        Directory.CreateDirectory(_baseDir);
        _dbPath = Path.Combine(_baseDir, "netclaw.db");
        _store = new SQLiteMemoryStore(_dbPath, TimeProvider.System);
    }

    public async ValueTask DisposeAsync() => await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(_baseDir);

    // ── Guard failure -> append fallback (overwrite-unreachable) ──────

    [Fact]
    public async Task LlmUpdate_withLossyMergedBody_appendsInsteadOfOverwriting()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        const string originalBody =
            "Netclaw GitHub repository: https://github.com/netclaw-dev/netclaw. The repository is private.";
        await SeedDocumentAsync("netclaw-github-repository", "doc-repo", originalBody, freshnessAtMs: 1000, ct);

        var operation = MakeOperation(
            "netclaw-github-repo",
            "Netclaw GitHub repository at https://github.com/netclaw-dev/netclaw, private repo",
            freshnessAtMs: 2000);

        // Lossy: drops the URL entirely — MergeGuard must reject this.
        var evaluator = new MemoryCurationEvaluator(
            _store, (ILoggingAdapter)NoLogger.Instance, new MemoryCurationConfig(),
            new ScriptedCurationChatClient("UPDATE doc-repo\n---\nRepository details updated."));

        var evaluation = await evaluator.EvaluateAsync(operation, TestSessionId, ct);
        Assert.Equal(CurationDecisionKind.Update, evaluation.Decision.Kind);
        Assert.True(evaluation.Decision.FromLlmTier);
        Assert.NotNull(evaluation.Decision.MergedBody);

        var writeOp = await evaluator.ApplyDecisionAsync(operation, evaluation.Decision, evaluation.Candidates, ct);
        Assert.NotNull(writeOp);
        await _store.ApplyInlineCurationBatchAsync([writeOp!], ct);

        var stored = await GetDocumentAsync("doc-repo", ct);
        Assert.NotNull(stored);

        // Overwrite-unreachable: the original body is NOT replaced by the lossy merge — the
        // rejected merged body ("Repository details updated.") is discarded entirely, and the
        // append fallback appends the ORIGINAL PROPOSAL content instead (never the untrusted
        // merged text), on top of the target's untouched original body.
        Assert.StartsWith(originalBody, stored!.Value.Body, StringComparison.Ordinal);
        Assert.Contains(operation.Content, stored.Value.Body);
        Assert.DoesNotContain("Repository details updated.", stored.Value.Body);
        Assert.Contains("---", stored.Value.Body);
        Assert.Equal("append-document", stored.Value.UpdateSemantics);
    }

    [Fact]
    public async Task LlmUpdate_withNoMergedBody_appendsInsteadOfOverwriting()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        const string originalBody =
            "Netclaw GitHub repository: https://github.com/netclaw-dev/netclaw. The repository is private.";
        await SeedDocumentAsync("netclaw-github-repository", "doc-repo", originalBody, freshnessAtMs: 1000, ct);

        var operation = MakeOperation(
            "netclaw-github-repo",
            "Netclaw GitHub repository at https://github.com/netclaw-dev/netclaw, private repo",
            freshnessAtMs: 2000);

        // Keyword-only LLM response — no "---" body at all.
        var evaluator = new MemoryCurationEvaluator(
            _store, (ILoggingAdapter)NoLogger.Instance, new MemoryCurationConfig(),
            new ScriptedCurationChatClient("UPDATE doc-repo"));

        var evaluation = await evaluator.EvaluateAsync(operation, TestSessionId, ct);
        Assert.Equal(CurationDecisionKind.Update, evaluation.Decision.Kind);
        Assert.True(evaluation.Decision.FromLlmTier);
        Assert.Null(evaluation.Decision.MergedBody);

        var writeOp = await evaluator.ApplyDecisionAsync(operation, evaluation.Decision, evaluation.Candidates, ct);
        await _store.ApplyInlineCurationBatchAsync([writeOp!], ct);

        var stored = await GetDocumentAsync("doc-repo", ct);
        Assert.NotNull(stored);
        Assert.StartsWith(originalBody, stored!.Value.Body, StringComparison.Ordinal);
        Assert.Contains(operation.Content, stored.Value.Body);
        Assert.Equal("append-document", stored.Value.UpdateSemantics);
    }

    // ── Guard pass -> merged body written ─────────────────────────────

    [Fact]
    public async Task LlmUpdate_withFaithfulMergedBody_writesMergedBody()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        const string originalBody =
            "Netclaw GitHub repository: https://github.com/netclaw-dev/netclaw. The repository is private.";
        await SeedDocumentAsync("netclaw-github-repository", "doc-repo", originalBody, freshnessAtMs: 1000, ct);

        var operation = MakeOperation(
            "netclaw-github-repo",
            "Netclaw GitHub repository at https://github.com/netclaw-dev/netclaw, private repo",
            freshnessAtMs: 2000);

        const string mergedBody =
            "Netclaw GitHub repository: https://github.com/netclaw-dev/netclaw. The repository remains private.";
        var evaluator = new MemoryCurationEvaluator(
            _store, (ILoggingAdapter)NoLogger.Instance, new MemoryCurationConfig(),
            new ScriptedCurationChatClient($"UPDATE doc-repo\n---\n{mergedBody}"));

        var evaluation = await evaluator.EvaluateAsync(operation, TestSessionId, ct);
        var writeOp = await evaluator.ApplyDecisionAsync(operation, evaluation.Decision, evaluation.Candidates, ct);
        await _store.ApplyInlineCurationBatchAsync([writeOp!], ct);

        var stored = await GetDocumentAsync("doc-repo", ct);
        Assert.NotNull(stored);
        Assert.Equal(mergedBody, stored!.Value.Body);
        Assert.Equal("merge-document", stored.Value.UpdateSemantics);
    }

    // ── Deterministic tier regression: exact-anchor Update keeps raw overwrite ─

    [Fact]
    public async Task DeterministicUpdate_exactAnchorSuperset_stillOverwritesRawly()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);
        await SeedDocumentAsync("latest-version", "doc-version", "Latest version is 1.5.62.", freshnessAtMs: 1000, ct);

        var operation = MakeOperation(
            "latest-version",
            "Latest version is 1.5.62. Released with the new serializer.",
            freshnessAtMs: 2000);

        // No LLM client — this exercises the deterministic exact-anchor path only.
        var evaluator = new MemoryCurationEvaluator(_store, (ILoggingAdapter)NoLogger.Instance, new MemoryCurationConfig());

        var evaluation = await evaluator.EvaluateAsync(operation, TestSessionId, ct);
        Assert.Equal(CurationDecisionKind.Update, evaluation.Decision.Kind);
        Assert.False(evaluation.Decision.FromLlmTier);

        var writeOp = await evaluator.ApplyDecisionAsync(operation, evaluation.Decision, evaluation.Candidates, ct);
        await _store.ApplyInlineCurationBatchAsync([writeOp!], ct);

        var stored = await GetDocumentAsync("doc-version", ct);
        Assert.NotNull(stored);
        // Raw overwrite: the stored body IS the proposal's content verbatim, not appended.
        Assert.Equal(operation.Content, stored!.Value.Body);
        Assert.Equal("merge-document", stored.Value.UpdateSemantics);
    }

    // ── Consolidate: deterministic tier (no LLM, no merged body) also appends ─

    [Fact]
    public async Task DeterministicConsolidate_appendsRatherThanOverwriting()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.InitializeAsync(ct);

        const string originalBody = "Akka.NET latest release version is 1.5.62";
        await SeedDocumentAsync("akka-net-latest-release", "doc-akka", originalBody, freshnessAtMs: 1000, ct);

        var operation = MakeOperation(
            "akka-net-release", "Akka.NET latest release version is 1.5.62", freshnessAtMs: 2000);

        // No LLM client — fuzzy match >80% overlap resolves to Consolidate deterministically.
        var evaluator = new MemoryCurationEvaluator(_store, (ILoggingAdapter)NoLogger.Instance, new MemoryCurationConfig());

        var evaluation = await evaluator.EvaluateAsync(operation, TestSessionId, ct);
        Assert.Equal(CurationDecisionKind.Consolidate, evaluation.Decision.Kind);
        Assert.False(evaluation.Decision.FromLlmTier);
        Assert.Null(evaluation.Decision.MergedBody);

        var writeOp = await evaluator.ApplyDecisionAsync(operation, evaluation.Decision, evaluation.Candidates, ct);
        Assert.NotNull(writeOp);
        Assert.Equal("doc-akka", writeOp!.MemoryId);
        await _store.ApplyInlineCurationBatchAsync([writeOp], ct);

        var stored = await GetDocumentAsync("doc-akka", ct);
        Assert.NotNull(stored);
        Assert.StartsWith(originalBody, stored!.Value.Body, StringComparison.Ordinal);
        Assert.Equal("append-document", stored.Value.UpdateSemantics);
    }

    // ── helpers ──────────────────────────────────────────────────────

    private async Task SeedDocumentAsync(
        string anchorName, string docId, string content, long freshnessAtMs, CancellationToken ct)
    {
        var anchor = _store.CreateDefaultAnchor(anchorName);
        await _store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: docId,
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: $"Existing {anchorName}",
            MarkdownBody: content,
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: freshnessAtMs,
            ExpiresAtMs: null,
            CreatedAtMs: freshnessAtMs,
            UpdatedAtMs: freshnessAtMs), ct);
    }

    private async Task<(string Body, string UpdateSemantics)?> GetDocumentAsync(string documentId, CancellationToken ct)
    {
        var handles = await _store.ResolveMemoryHandlesAsync(
            [documentId], TrustBoundary.TrustedInstanceValue, TrustAudience.Public, ct);
        var resolved = handles.FirstOrDefault(h => h.Resolved);
        if (resolved is null)
            return null;

        var hydrated = await _store.GetMemoriesByResolvedHandlesAsync(
            [resolved], TrustBoundary.TrustedInstanceValue, TrustAudience.Public, ct);
        var item = hydrated.FirstOrDefault();
        return item is null ? null : (item.Content, item.UpdateSemantics);
    }

    private static SQLiteMemoryCurationOperation MakeOperation(
        string anchor,
        string content,
        string kind = "document",
        string updateSemantics = "merge-document",
        long freshnessAtMs = 2000) =>
        new(
            Kind: kind,
            MemoryClass: "durable_fact",
            MemoryId: null,
            AnchorCanonicalName: anchor,
            AnchorType: "concept",
            Title: $"Title for {anchor}",
            Content: content,
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            Relations: null,
            UpdateSemantics: updateSemantics,
            Boundary: TrustBoundary.TrustedInstanceValue,
            Audience: TrustAudience.Public,
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: freshnessAtMs,
            ExpiresAtMs: null);

    /// <summary>
    /// Minimal scripted <see cref="IChatClient"/>: streams <paramref name="responseText"/> as a
    /// single update. Mirrors <c>MemoryCurationEvaluatorParityTests.ScriptedCurationChatClient</c>
    /// (kept as a separate private copy rather than shared test infra — small and self-contained).
    /// </summary>
    private sealed class ScriptedCurationChatClient(string responseText) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new AiChatMessage(AiChatRole.Assistant, responseText)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => StreamAsync(cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ChatResponseUpdate(AiChatRole.Assistant, responseText);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
