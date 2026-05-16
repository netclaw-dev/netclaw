// -----------------------------------------------------------------------
// <copyright file="SidecarDiagnosticsContextTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka.Event;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Xunit;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Verifies that session-owned sidecar paths populate
/// <see cref="SessionDiagnosticsContext"/> around their <c>IChatClient</c>
/// calls so MEL provider diagnostics emitted during the call route into
/// the per-session log. One test per major sidecar covered by
/// netclaw-dev/netclaw#920. Test contract: a fake <c>IChatClient</c>
/// captures <c>SessionDiagnosticsContext.SessionId</c> at the moment the
/// chat client method is invoked. That is exactly the AsyncLocal value
/// any real provider plugin would see when emitting MEL log lines.
/// </summary>
public sealed class SidecarDiagnosticsContextTests : TestKit
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task TitleGenerator_populates_session_diagnostics_scope()
    {
        var sessionId = new SessionId("ch/title-thread");
        var captor = new SessionContextCapturingChatClient();
        var probe = CreateTestProbe();

        await SessionTitleGenerator.GenerateAsync(
            captor,
            sessionId,
            history: [],
            self: probe.Ref,
            log: NoLogger.Instance,
            timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(sessionId.Value, captor.CapturedSessionId);
        Assert.Null(SessionDiagnosticsContext.SessionId);
    }

    [Fact]
    public async Task CompactionPipeline_populates_session_diagnostics_scope()
    {
        var sessionId = new SessionId("ch/compaction-thread");
        var captor = new SessionContextCapturingChatClient();
        var history = new List<SerializableChatMessage>
        {
            new() { Role = Netclaw.Actors.Protocol.ChatRole.User, Content = "hello" },
            new() { Role = Netclaw.Actors.Protocol.ChatRole.Assistant, Content = "hi" }
        };

        var observation = await SessionCompactionPipeline.GenerateObservationsAsync(
            client: captor,
            sessionId: sessionId,
            history: history,
            systemOffset: 0,
            keepStartIndex: 1,
            sidecarTimeout: TimeSpan.FromSeconds(5),
            log: NoLogger.Instance,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(sessionId.Value, captor.CapturedSessionId);
        Assert.Null(SessionDiagnosticsContext.SessionId);
        Assert.NotNull(observation);
    }

    [Fact]
    public async Task MemoryExtraction_populates_session_diagnostics_scope()
    {
        var sessionId = new SessionId("ch/memory-extraction-thread");
        var captor = new SessionContextCapturingChatClient();
        var probe = CreateTestProbe();

        await LlmSessionActor.InvokeMemoryExtractionCoreAsync(
            captor,
            sessionId,
            history: [],
            self: probe.Ref,
            timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(sessionId.Value, captor.CapturedSessionId);
        Assert.Null(SessionDiagnosticsContext.SessionId);
    }

    [Fact]
    public async Task SubAgent_InvokeLlm_populates_session_diagnostics_scope()
    {
        var sessionId = new SessionId("ch/subagent-thread");
        var captor = new SessionContextCapturingChatClient();
        var probe = CreateTestProbe();

        await SubAgentActor.InvokeLlmAsync(
            captor,
            messages: [],
            options: null,
            sessionId: sessionId,
            self: probe.Ref,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(sessionId.Value, captor.CapturedSessionId);
        Assert.Null(SessionDiagnosticsContext.SessionId);
    }

    [Fact]
    public async Task SubAgent_InvokeLlm_with_null_sessionId_pushes_null_scope()
    {
        // Sub-agents that run outside any session legitimately have no
        // session id. The Push contract accepts null and the captor should
        // see null inside the call.
        var captor = new SessionContextCapturingChatClient();
        var probe = CreateTestProbe();

        await SubAgentActor.InvokeLlmAsync(
            captor,
            messages: [],
            options: null,
            sessionId: null,
            self: probe.Ref,
            ct: TestContext.Current.CancellationToken);

        Assert.Null(captor.CapturedSessionId);
        Assert.Null(SessionDiagnosticsContext.SessionId);
    }

    [Fact]
    public async Task MemoryObserver_RunDistillation_populates_session_diagnostics_scope()
    {
        var sessionId = new SessionId("ch/distillation-thread");
        var captor = new SessionContextCapturingChatClient();
        var probe = CreateTestProbe();

        await SessionMemoryObserverActor.RunDistillationAsync(
            client: captor,
            sessionId: sessionId,
            turnCount: 5,
            transcript: "user: hello\nassistant: hi",
            existingProposals: [],
            timeout: TimeSpan.FromSeconds(5),
            self: probe.Ref,
            runId: 1,
            contentVersion: 1,
            log: NoLogger.Instance);

        Assert.Equal(sessionId.Value, captor.CapturedSessionId);
        Assert.Null(SessionDiagnosticsContext.SessionId);
    }

    [Fact]
    public async Task MemoryCuration_TryLlmEvaluation_populates_session_diagnostics_scope()
    {
        var sessionId = new SessionId("ch/curation-thread");
        var captor = new SessionContextCapturingChatClient();
        var operation = new SQLiteMemoryCurationOperation(
            Kind: "document",
            MemoryClass: "durable_fact",
            MemoryId: null,
            AnchorCanonicalName: "test-anchor",
            AnchorType: "preference",
            Title: "Test",
            Content: "Test content for diagnostics scope verification.",
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

        await MemoryCurationActor.TryLlmEvaluationAsync(
            captor,
            sessionId,
            operation,
            candidates: [],
            log: NoLogger.Instance);

        Assert.Equal(sessionId.Value, captor.CapturedSessionId);
        Assert.Null(SessionDiagnosticsContext.SessionId);
    }

    /// <summary>
    /// IChatClient stub that captures <see cref="SessionDiagnosticsContext.SessionId"/>
    /// at the moment its methods are invoked. The captured value is the
    /// AsyncLocal seen by the chat client — the same value any MEL provider
    /// plugin emitting diagnostics inside the call would see.
    /// </summary>
    private sealed class SessionContextCapturingChatClient : IChatClient
    {
        public string? CapturedSessionId { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CapturedSessionId = SessionDiagnosticsContext.SessionId;
            var response = new ChatResponse(new AiChatMessage(
                AiChatRole.Assistant,
                (IList<AIContent>)[new TextContent("captured")]));
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => StreamAsync(cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            CapturedSessionId = SessionDiagnosticsContext.SessionId;
            yield return new ChatResponseUpdate(AiChatRole.Assistant, "captured");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
