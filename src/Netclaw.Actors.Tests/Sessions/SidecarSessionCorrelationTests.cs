// -----------------------------------------------------------------------
// <copyright file="SidecarSessionCorrelationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Event;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Regression guard (replaces the deleted SidecarDiagnosticsContextTests, netclaw-dev/netclaw#920)
/// for the session-owned sidecar LLM paths — title generation, compaction observation, memory
/// extraction/distillation, and memory curation. After the SessionDiagnosticsContext AsyncLocal
/// was removed, each must carry its owning session id explicitly via
/// <see cref="SessionScopedChatOptions"/> on the chat-call's <see cref="ChatOptions"/>, so the
/// file-logger routes the call's chat-client diagnostics into that session's session.log (and
/// Seq/OTLP correlate them). Test contract: a fake IChatClient captures the ChatOptions it is
/// invoked with; the assertion is that it is a SessionScopedChatOptions naming the session.
/// Without the carrier these calls silently regress to daemon.log, uncorrelated.
/// </summary>
public sealed class SidecarSessionCorrelationTests : TestKit
{
    protected override void ConfigureAkka(Akka.Hosting.AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task TitleGenerator_carries_session_scoped_options()
    {
        var sessionId = new SessionId("ch/title-thread");
        var captor = new FakeChatClient();

        await SessionTitleGenerator.GenerateAsync(
            captor, sessionId, history: [], self: CreateTestProbe().Ref,
            log: NoLogger.Instance, timeout: TimeSpan.FromSeconds(5));

        AssertScopedTo(sessionId, captor);
    }

    [Fact]
    public async Task CompactionObserver_carries_session_scoped_options()
    {
        var sessionId = new SessionId("ch/compaction-thread");
        var captor = new FakeChatClient();
        var history = new List<SerializableChatMessage>
        {
            new() { Role = Netclaw.Actors.Protocol.ChatRole.User, Content = "hello" },
            new() { Role = Netclaw.Actors.Protocol.ChatRole.Assistant, Content = "hi" }
        };

        await SessionCompactionPipeline.GenerateObservationsAsync(
            client: captor, sessionId: sessionId, history: history, systemOffset: 0,
            keepStartIndex: 1, sidecarTimeout: TimeSpan.FromSeconds(5), log: NoLogger.Instance,
            cancellationToken: TestContext.Current.CancellationToken);

        AssertScopedTo(sessionId, captor);
    }

    [Fact]
    public async Task MemoryExtraction_carries_session_scoped_options()
    {
        var sessionId = new SessionId("ch/memory-extraction-thread");
        var captor = new FakeChatClient();

        await LlmSessionActor.InvokeMemoryExtractionCoreAsync(
            captor, sessionId, history: [], self: CreateTestProbe().Ref, timeout: TimeSpan.FromSeconds(5));

        AssertScopedTo(sessionId, captor);
    }

    [Fact]
    public async Task MemoryDistillation_carries_session_scoped_options()
    {
        var sessionId = new SessionId("ch/distillation-thread");
        var captor = new FakeChatClient();

        await SessionMemoryObserverActor.RunDistillationAsync(
            client: captor, sessionId: sessionId, turnCount: 5,
            transcript: "user: hello\nassistant: hi", existingProposals: [],
            timeout: TimeSpan.FromSeconds(5), self: CreateTestProbe().Ref, runId: 1,
            contentVersion: 1, log: NoLogger.Instance);

        AssertScopedTo(sessionId, captor);
    }

    [Fact]
    public async Task MemoryCuration_carries_session_scoped_options()
    {
        var sessionId = new SessionId("ch/curation-thread");
        var captor = new FakeChatClient();
        var operation = new SQLiteMemoryCurationOperation(
            Kind: "document",
            MemoryClass: "durable_fact",
            MemoryId: null,
            AnchorCanonicalName: "test-anchor",
            AnchorType: "preference",
            Title: "Test",
            Content: "Test content for session correlation verification.",
            AliasesJson: "[]",
            FacetsJson: "[]",
            SlotsJson: null,
            Relations: null,
            UpdateSemantics: "merge-document",
            Boundary: TrustBoundary.TrustedInstanceValue,
            Audience: TrustAudience.Public,
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds(),
            ExpiresAtMs: null);

        await MemoryCurationEvaluator.TryLlmEvaluationAsync(
            captor, sessionId, operation, candidates: [], log: new AkkaCurationLog(NoLogger.Instance));

        AssertScopedTo(sessionId, captor);
    }

    private static void AssertScopedTo(SessionId sessionId, FakeChatClient captor)
    {
        // FakeChatClient here is the Sessions-namespace fake (it is purpose-built for
        // these pipeline paths); it records the options of every call, so the last entry
        // is the most recent invocation the decorators scoped.
        var scoped = Assert.IsType<SessionScopedChatOptions>(captor.ReceivedOptions[^1]);
        Assert.Equal(sessionId.Value, scoped.SessionId);
    }
}
