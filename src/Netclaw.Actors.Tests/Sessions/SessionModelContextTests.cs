// -----------------------------------------------------------------------
// <copyright file="SessionModelContextTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Tests.Tools;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using ProtocolChatRole = Netclaw.Actors.Protocol.ChatRole;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class SessionModelContextTests
{

    [Fact]
    public void Prompt_refresh_preserves_equal_bytes_and_retains_the_last_prompt_when_source_disappears()
    {
        // Arrange
        var prompt = new MutablePromptProvider { Content = "base\nproject A" };
        var owner = CreateOwner(prompt);
        var original = SessionState.Empty.AddUserMessage("question");

        // Act
        var inserted = owner.RefreshPrompt(original, TrustAudience.Personal);
        var unchanged = owner.RefreshPrompt(inserted, TrustAudience.Personal);
        prompt.Content = "base\nproject B";
        var replaced = owner.RefreshPrompt(unchanged, TrustAudience.Personal);
        prompt.Content = string.Empty;
        var retained = owner.RefreshPrompt(replaced, TrustAudience.Personal);

        // Assert
        Assert.Equal(ProtocolChatRole.System, inserted.History[0].Role);
        Assert.Equal("base\nproject A", inserted.History[0].Content);
        Assert.Equal("question", inserted.History[1].Content);
        Assert.Same(inserted, unchanged);
        Assert.Equal("base\nproject B", replaced.History[0].Content);
        Assert.Equal("question", replaced.History[1].Content);
        Assert.Same(replaced, retained);
    }

    [Fact]
    public async Task A_turn_gets_one_volatile_nudge_before_its_user_message_and_a_tool_loop_gets_none()
    {
        // Arrange
        var owner = CreateOwner(new StaticSystemPromptProvider("stable prompt"));
        owner.SetOverlay("context note");
        var firstState = owner.RefreshPrompt(
            SessionState.Empty.AddUserMessage("first question"), TrustAudience.Personal);

        // Act
        var firstInspection = Assert.IsType<ContextCall.Inspect>(Start(owner, 1, firstState));
        var firstReply = Assert.IsType<WorkingContextSnapshotReady>(await firstInspection.Work);
        var first = Assert.IsType<PreparedModelRequest>(owner.CompleteInspection(
            firstReply, firstState, childContinuation: null, TrustAudience.Personal, currentToolTrust: null));
        var loop = Assert.IsType<ContextCall.Ready>(Start(owner, 2, first.State)).Request;

        owner.ResetForNewTurn();
        var nextState = first.State.AddUserMessage("second question");
        var nextInspection = Assert.IsType<ContextCall.Inspect>(Start(owner, 3, nextState));
        var nextReply = Assert.IsType<WorkingContextSnapshotReady>(await nextInspection.Work);
        var next = Assert.IsType<PreparedModelRequest>(owner.CompleteInspection(
            nextReply, nextState, childContinuation: null, TrustAudience.Personal, currentToolTrust: null));

        // Assert
        Assert.Equal(1, NudgeCount(first.State));
        Assert.StartsWith(SessionState.SystemNudgePrefix, first.State.History[^2].Content);
        Assert.Equal("first question", first.State.History[^1].Content);
        Assert.Same(first.State, loop.State);
        Assert.Equal(1, NudgeCount(loop.State));
        Assert.Equal(first.Messages[0].Text, loop.Messages[0].Text);
        Assert.Equal(2, NudgeCount(next.State));
        Assert.StartsWith(SessionState.SystemNudgePrefix, next.State.History[^2].Content);
        Assert.Equal("second question", next.State.History[^1].Content);
        Assert.Equal("stable prompt", next.Messages[0].Text);
        Assert.Equal(SessionId.Value, first.Options.SessionId);
        Assert.Equal(SessionId.Value, loop.Options.SessionId);
        Assert.Equal(SessionId.Value, next.Options.SessionId);
    }

    [Fact]
    public async Task Recall_runs_once_per_turn_and_returns_an_audit_separate_from_the_wire_request()
    {
        // Arrange
        var recall = new RecordingRecallCoordinator();
        var owner = CreateOwner(new StaticSystemPromptProvider("stable prompt"), memory: CreateMemory(recall));
        var source = Source(TrustAudience.Personal);
        var firstState = SessionState.Empty.AddUserMessage("first question");

        // Act
        var firstCall = Assert.IsType<ContextCall.Inspect>(Start(owner, 1, firstState, source));
        var firstReply = Assert.IsType<WorkingContextSnapshotReady>(await firstCall.Work);
        var first = Assert.IsType<PreparedModelRequest>(owner.CompleteInspection(
            firstReply, firstState, childContinuation: null, TrustAudience.Personal, currentToolTrust: null));
        var loop = Assert.IsType<ContextCall.Ready>(Start(owner, 2, first.State, source));

        owner.ResetForNewTurn();
        var secondState = first.State.AddUserMessage("second question");
        var secondCall = Assert.IsType<ContextCall.Inspect>(Start(owner, 3, secondState, source));
        var secondReply = Assert.IsType<WorkingContextSnapshotReady>(await secondCall.Work);
        var second = Assert.IsType<PreparedModelRequest>(owner.CompleteInspection(
            secondReply, secondState, childContinuation: null, TrustAudience.Personal, currentToolTrust: null));

        owner.ResetAfterCompaction();
        var thirdState = second.State.AddUserMessage("third question");
        var thirdCall = Assert.IsType<ContextCall.Inspect>(Start(owner, 4, thirdState, source));
        var thirdReply = Assert.IsType<WorkingContextSnapshotReady>(await thirdCall.Work);
        var third = Assert.IsType<PreparedModelRequest>(owner.CompleteInspection(
            thirdReply, thirdState, childContinuation: null, TrustAudience.Personal, currentToolTrust: null));

        // Assert
        Assert.Equal(3, recall.Requests.Count);
        Assert.Equal("first question", recall.Requests[0].Query);
        Assert.Equal("second question", recall.Requests[1].Query);
        Assert.Equal("third question", recall.Requests[2].Query);
        Assert.Equal("memory one", Assert.Single(firstCall.Audit.Result.Items).Content);
        Assert.Equal("memory two", Assert.Single(secondCall.Audit.Result.Items).Content);
        Assert.Equal("memory three", Assert.Single(thirdCall.Audit.Result.Items).Content);
        Assert.Contains("memory one", string.Join("\n", first.Messages.Select(message => message.Text)));
        Assert.Contains("memory two", string.Join("\n", second.Messages.Select(message => message.Text)));
        Assert.Contains("memory three", string.Join("\n", third.Messages.Select(message => message.Text)));
        Assert.Same(first.State, loop.Request.State);
    }

    [Fact]
    public async Task A_public_request_omits_private_jobs_paths_and_recall_from_the_complete_wire_request()
    {
        // Arrange
        var recall = new RecordingRecallCoordinator();
        var owner = CreateOwner(new StaticSystemPromptProvider("stable prompt"), memory: CreateMemory(recall));
        var state = SessionState.Empty
            .TrackBackgroundJob("bg-job:secret01", new ActiveJobInfo
            {
                JobId = new BackgroundJobId("secret01"),
                Command = "deploy --token secret",
                Rationale = "private deploy",
                StartedAtMs = 1000,
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.Personal,
                OutputLogPath = "/private/jobs/secret01.log"
            }) with
            { WorkingContext = WorkingContext.Empty.WithProjectDirectory("/private/project").AddRecentFile("Secrets.cs") };
        state = state.AddUserMessage("public question");

        // Act
        var call = Assert.IsType<ContextCall.Inspect>(Start(owner, 1, state, Source(TrustAudience.Public)));
        var reply = Assert.IsType<WorkingContextSnapshotReady>(await call.Work);
        var prepared = Assert.IsType<PreparedModelRequest>(owner.CompleteInspection(
            reply, state, childContinuation: null, TrustAudience.Public, currentToolTrust: null));
        var wire = string.Join("\n", prepared.Messages.Select(message => message.Text));

        // Assert
        Assert.Empty(call.Audit.Result.Items);
        Assert.Empty(recall.Requests);
        Assert.Contains("public question", wire);
        Assert.DoesNotContain("deploy --token secret", wire);
        Assert.DoesNotContain("secret01.log", wire);
        Assert.DoesNotContain("/private/project", wire);
        Assert.DoesNotContain("Secrets.cs", wire);
        Assert.DoesNotContain("[memory-recall]", wire);
        Assert.Equal(SessionId.Value, prepared.Options.SessionId);
    }

    [Fact]
    public async Task Missing_trust_context_refuses_a_candidate_tool_under_the_real_access_policy()
    {
        // Arrange
        var tool = AIFunctionFactory.Create(() => "private result", "file_read");
        var registry = new ToolRegistry();
        registry.RegisterCore(tool, "file");
        var cache = new DiscoveredToolCache();
        cache.SeedBaseTools([tool]);
        var services = new SessionToolServices(
            new UnexpectedToolExecutor(),
            registry,
            TestToolAccessPolicy.Create(new ToolConfig()),
            TrustDeriver: null,
            SkillRegistry: null);
        var owner = CreateOwner(new StaticSystemPromptProvider("stable prompt"),
            discoveredTools: cache, tools: services);
        var state = SessionState.Empty.AddUserMessage("read a file");

        // Act
        var call = Assert.IsType<ContextCall.Inspect>(Start(owner, 1, state));
        var reply = Assert.IsType<WorkingContextSnapshotReady>(await call.Work);
        var prepared = Assert.IsType<PreparedModelRequest>(owner.CompleteInspection(
            reply, state, childContinuation: null, TrustAudience.Personal, currentToolTrust: null));

        // Assert
        Assert.Single(cache.AvailableTools);
        Assert.Null(prepared.Options.Tools);
        Assert.Equal(SessionId.Value, prepared.Options.SessionId);
    }

    [Fact]
    public async Task A_failed_inspection_uses_a_visible_unavailable_context_for_the_current_call()
    {
        // Arrange
        var owner = CreateOwner(new StaticSystemPromptProvider("stable prompt"),
            snapshots: new FailingSnapshotProvider());
        var state = SessionState.Empty.AddUserMessage("question");

        // Act
        var call = Assert.IsType<ContextCall.Inspect>(Start(owner, 1, state));
        var reply = Assert.IsType<WorkingContextSnapshotFailed>(await call.Work);
        var prepared = Assert.IsType<PreparedModelRequest>(owner.CompleteInspection(
            reply, state, childContinuation: null, TrustAudience.Personal, currentToolTrust: null));
        var wire = string.Join("\n", prepared.Messages.Select(message => message.Text));

        // Assert
        Assert.IsType<IOException>(reply.Cause);
        Assert.Equal(1, NudgeCount(prepared.State));
        Assert.Contains("working context inspection failed", wire);
        Assert.Equal(SessionId.Value, prepared.Options.SessionId);
    }

    private static SessionMemoryServices CreateMemory(IMemoryRecallCoordinator recall)
        => new(
            recall,
            NullMemoryCheckpointSink.Instance,
            MemoryStore: null,
            MemoryConfig: new MemoryConfig { Enabled = true });

    private static MessageSource Source(TrustAudience audience)
        => new()
        {
            ChannelType = ChannelType.Slack,
            SenderId = new SenderId("user-1"),
            Audience = audience,
            Boundary = audience == TrustAudience.Public ? TrustBoundary.Public : TrustBoundary.Personal,
            Principal = PrincipalClassification.TrustedInternal,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        };

    private sealed class UnexpectedToolExecutor : IToolExecutor
    {
        public Task<string> ExecuteAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The context owner must not execute a tool.");
    }

    private sealed class MutablePromptProvider : ISystemPromptProvider
    {
        public string Content { get; set; } = string.Empty;

        public string GetSystemPrompt(TrustAudience audience, string? projectDirectory = null) => Content;

        public string? GetProjectInstructions(TrustAudience audience, string? projectDirectory) => null;

        public string? GetOperatingRules(TrustAudience audience) => null;
    }

    private sealed class FailingSnapshotProvider : IWorkingContextSnapshotProvider
    {
        public Task<WorkingContextSnapshot> CreateAsync(
            WorkingContext context,
            TrustAudience audience,
            CancellationToken cancellationToken)
            => Task.FromException<WorkingContextSnapshot>(new IOException("inspection unavailable"));
    }

    private sealed class RecordingRecallCoordinator : IMemoryRecallCoordinator
    {
        private static readonly string[] Contents = ["memory one", "memory two", "memory three"];

        public List<AutomaticRecallRequest> Requests { get; } = [];

        public Task<AutomaticRecallResult> RecallAsync(
            AutomaticRecallRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var index = Requests.Count - 1;
            var item = new AutomaticRecallItem(
                $"mem/{index + 1}", "test memory", Contents[index], "personal", 0.9);
            return Task.FromResult(new AutomaticRecallResult([item]));
        }
    }
}
