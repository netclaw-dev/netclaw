using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class SessionMemoryObserverActorTests : TestKit
{
    private readonly FakeChatClient _fakeChatClient = new();

    public SessionMemoryObserverActorTests(ITestOutputHelper output) : base(output: output)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore();
    }

    /// <summary>
    /// Creates observer as a top-level actor (Context.Parent = user guardian).
    /// Use for tests that send explicit DistillMemories (reply goes to Sender).
    /// </summary>
    private IActorRef CreateObserver(
        string sessionSuffix,
        FakeChatClient? client = null,
        TimeSpan? idleTimeout = null,
        TimeSpan? sidecarTimeout = null) =>
        Sys.ActorOf(
            SessionMemoryObserverActor.CreateProps(
                new SessionId($"test-channel/{sessionSuffix}"),
                client ?? _fakeChatClient,
                idleTimeout ?? TimeSpan.FromSeconds(1),
                sidecarTimeout ?? TimeSpan.FromSeconds(5)));

    /// <summary>
    /// Creates observer as a child of a forwarding parent that routes all
    /// messages from the observer to the returned probe. This is needed for
    /// tests that verify idle-triggered distillation (which sends to Context.Parent).
    /// </summary>
    private (IActorRef Observer, IActorRef ParentActor, Akka.TestKit.TestProbe ParentProbe) CreateObserverWithParentProbe(
        string sessionSuffix,
        FakeChatClient? client = null,
        TimeSpan? idleTimeout = null,
        TimeSpan? sidecarTimeout = null)
    {
        var probe = CreateTestProbe($"parent-{sessionSuffix}");
        var props = SessionMemoryObserverActor.CreateProps(
            new SessionId($"test-channel/{sessionSuffix}"),
            client ?? _fakeChatClient,
            idleTimeout ?? TimeSpan.FromSeconds(1),
            sidecarTimeout ?? TimeSpan.FromSeconds(5));

        var parent = Sys.ActorOf(Props.Create(() => new ForwardingParent(props, probe.Ref)),
            $"parent-wrapper-{sessionSuffix}");

        // Ask the parent for the child ref
        var observer = parent.Ask<IActorRef>(GetChild.Instance, TimeSpan.FromSeconds(3)).Result;
        return (observer, parent, probe);
    }

    [Fact]
    public async Task ReceiveTimeout_with_no_new_content_does_not_reply_to_parent()
    {
        var (observer, _, parentProbe) = CreateObserverWithParentProbe("observer-timeout");

        observer.Tell(ReceiveTimeout.Instance, parentProbe.Ref);

        await parentProbe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Explicit_distill_request_with_no_new_content_still_replies_empty()
    {
        var observer = CreateObserver("observer-explicit");
        var parentProbe = CreateTestProbe("observer-explicit-parent");

        observer.Tell(new DistillMemories(), parentProbe.Ref);

        var reply = await parentProbe.ExpectMsgAsync<SessionDistillationCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(reply.Proposals);
        Assert.Null(reply.FailureReason);
    }

    [Fact]
    public async Task SessionPhaseChanged_Passivating_disables_idle_distillation()
    {
        // Use a very short idle timeout so the test doesn't wait long
        var (observer, _, parentProbe) = CreateObserverWithParentProbe("passivate-idle",
            idleTimeout: TimeSpan.FromMilliseconds(200));

        // Feed content so idle distillation would fire
        observer.Tell(new SendUserMessage
        {
            SessionId = new SessionId("test-channel/passivate-idle"),
            Content = "remember this fact"
        });

        // Enter passivating — this should disable the idle timer
        observer.Tell(new SessionPhaseChanged(SessionPhase.Passivating));

        // Wait longer than idle timeout — no distillation should fire
        await parentProbe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SessionPhaseChanged_Ready_after_Passivating_re_enables_idle_distillation()
    {
        var (observer, _, parentProbe) = CreateObserverWithParentProbe("abort-idle",
            idleTimeout: TimeSpan.FromMilliseconds(300));

        // Feed content
        observer.Tell(new SendUserMessage
        {
            SessionId = new SessionId("test-channel/abort-idle"),
            Content = "some important context"
        });

        // Enter passivating, then abort
        observer.Tell(new SessionPhaseChanged(SessionPhase.Passivating));
        observer.Tell(new SessionPhaseChanged(SessionPhase.Ready));

        // Idle timer should be re-enabled — wait for distillation to fire
        // The FakeChatClient returns non-JSON text, so proposals will be empty,
        // but the reply itself proves the idle distillation ran.
        var reply = await parentProbe.ExpectMsgAsync<SessionDistillationCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(reply);
    }

    [Fact]
    public async Task DistillMemories_with_content_triggers_distillation_and_replies()
    {
        var client = new FakeChatClient();
        // Return valid distillation JSON
        client.PlannedResponses.Enqueue([new TextContent("""
            {
                "proposals": [
                    {
                        "operation": "upsert_document",
                        "memoryClass": "durable_fact",
                        "subjectKind": "user",
                        "subjectValue": "self",
                        "anchor": { "canonicalName": "test-preference", "anchorType": "preference" },
                        "title": "Test Preference",
                        "content": "User prefers dark mode",
                        "aliases": ["dark mode"],
                        "facets": ["ui_preference"],
                        "recallMode": "auto",
                        "sensitivity": "normal",
                        "confidence": 0.9
                    }
                ]
            }
            """)]);

        var observer = CreateObserver("distill-content", client: client);
        var parentProbe = CreateTestProbe("parent-distill-content");

        // Feed content
        observer.Tell(new SendUserMessage
        {
            SessionId = new SessionId("test-channel/distill-content"),
            Content = "I prefer dark mode for all interfaces"
        });

        // Request distillation
        observer.Tell(new DistillMemories(), parentProbe.Ref);

        var reply = await parentProbe.ExpectMsgAsync<SessionDistillationCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(reply.Proposals);
        Assert.Equal("test-preference", reply.Proposals[0].Anchor?.CanonicalName);
        Assert.Null(reply.FailureReason);
    }

    [Fact]
    public async Task DistillMemories_while_distilling_with_different_sender_gets_empty_reply_after_completion()
    {
        // Gate the response so we can control when distillation completes
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeChatClient { NextResponseGate = gate };

        var (observer, _, parentProbe) = CreateObserverWithParentProbe("mid-distill", client: client,
            idleTimeout: TimeSpan.FromSeconds(30)); // long idle to prevent auto-fire

        var passivationProbe = CreateTestProbe("passivation-caller");

        // Feed content
        observer.Tell(new SendUserMessage
        {
            SessionId = new SessionId("test-channel/mid-distill"),
            Content = "important data for distillation"
        });

        // Trigger idle distillation (in-flight, blocked by gate)
        observer.Tell(ReceiveTimeout.Instance);

        // Wait until the distillation task has started (reached the gated LLM call)
        await AwaitAssertAsync(() => Assert.True(client.CallCount >= 1,
            $"Expected distillation to start, but CallCount={client.CallCount}"), cancellationToken: TestContext.Current.CancellationToken);

        // Now send passivation DistillMemories while idle distillation is in-flight
        observer.Tell(new DistillMemories(), passivationProbe.Ref);

        // Release the gate — idle distillation completes
        gate.SetResult();

        // The idle distillation result goes to Context.Parent (parentProbe)
        var idleReply = await parentProbe.ExpectMsgAsync<SessionDistillationCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(idleReply);

        // A distinct passivation caller gets the follow-up Empty reply
        var passivationReply = await passivationProbe.ExpectMsgAsync<SessionDistillationCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(passivationReply);
        Assert.Empty(passivationReply.Proposals);
    }

    [Fact]
    public async Task Passivation_abort_clears_pending_passivation_reply()
    {
        // Gate the response to keep distillation in-flight
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeChatClient { NextResponseGate = gate };

        var (observer, _, parentProbe) = CreateObserverWithParentProbe("abort-stash", client: client,
            idleTimeout: TimeSpan.FromSeconds(30));

        var passivationProbe = CreateTestProbe("passivation-abort-caller");
        var anotherProbe = CreateTestProbe("post-abort");

        // Feed content and trigger idle distillation (blocked by gate)
        observer.Tell(new SendUserMessage
        {
            SessionId = new SessionId("test-channel/abort-stash"),
            Content = "content for distillation"
        });
        observer.Tell(ReceiveTimeout.Instance);

        // Wait until the distillation task has started (reached the gated LLM call)
        await AwaitAssertAsync(() => Assert.True(client.CallCount >= 1,
            $"Expected distillation to start, but CallCount={client.CallCount}"), cancellationToken: TestContext.Current.CancellationToken);

        // Enter passivation and send DistillMemories (stashes reply)
        observer.Tell(new SessionPhaseChanged(SessionPhase.Passivating));
        observer.Tell(new DistillMemories(), passivationProbe.Ref);

        // Abort passivation before distillation finishes
        observer.Tell(new SessionPhaseChanged(SessionPhase.Ready));

        // Release the gate — distillation completes
        gate.SetResult();

        // The idle distillation result goes to parentProbe (Context.Parent)
        await parentProbe.ExpectMsgAsync<SessionDistillationCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // The passivation probe should NOT receive a reply (stash was cleared on abort)
        await passivationProbe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        // Verify observer is still functional after abort
        observer.Tell(new SendUserMessage
        {
            SessionId = new SessionId("test-channel/abort-stash"),
            Content = "new content after abort"
        });
        observer.Tell(new DistillMemories(), anotherProbe.Ref);
        var reply = await anotherProbe.ExpectMsgAsync<SessionDistillationCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(reply);
    }

    [Fact]
    public async Task Distillation_completion_keeps_dirty_bit_when_new_content_arrives_mid_run()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeChatClient { NextResponseGate = gate };
        var (observer, _, parentProbe) = CreateObserverWithParentProbe("dirty-mid-run", client: client,
            idleTimeout: TimeSpan.FromSeconds(30));

        observer.Tell(new SendUserMessage
        {
            SessionId = new SessionId("test-channel/dirty-mid-run"),
            Content = "first content"
        });
        observer.Tell(ReceiveTimeout.Instance);

        await AwaitAssertAsync(() => Assert.True(client.CallCount >= 1,
            $"Expected distillation to start, but CallCount={client.CallCount}"), cancellationToken: TestContext.Current.CancellationToken);

        observer.Tell(new SendUserMessage
        {
            SessionId = new SessionId("test-channel/dirty-mid-run"),
            Content = "second content after snapshot"
        });

        gate.SetResult();

        await parentProbe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        observer.Tell(ReceiveTimeout.Instance);
        await AwaitAssertAsync(() => Assert.True(client.CallCount >= 2,
            $"Expected follow-up distillation to start, but CallCount={client.CallCount}"), cancellationToken: TestContext.Current.CancellationToken);
        await parentProbe.ExpectMsgAsync<SessionDistillationCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await parentProbe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Same_parent_passivation_request_while_idle_distillation_runs_does_not_emit_duplicate_completion()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeChatClient { NextResponseGate = gate };
        var (observer, parentActor, parentProbe) = CreateObserverWithParentProbe("same-parent-passivation", client: client,
            idleTimeout: TimeSpan.FromSeconds(30));

        observer.Tell(new SendUserMessage
        {
            SessionId = new SessionId("test-channel/same-parent-passivation"),
            Content = "content for passivation"
        });
        observer.Tell(ReceiveTimeout.Instance);

        await AwaitAssertAsync(() => Assert.True(client.CallCount >= 1,
            $"Expected distillation to start, but CallCount={client.CallCount}"), cancellationToken: TestContext.Current.CancellationToken);

        observer.Tell(new DistillMemories(), parentActor);
        gate.SetResult();

        var reply = await parentProbe.ExpectMsgAsync<SessionDistillationCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(reply);
        await parentProbe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DistillMemories_records_accepted_proposals_for_recovery_after_ack()
    {
        var firstClient = new FakeChatClient();
        firstClient.PlannedResponses.Enqueue([new TextContent("""
            {
                "proposals": [
                    {
                        "operation": "upsert_document",
                        "memoryClass": "durable_fact",
                        "subjectKind": "user",
                        "subjectValue": "self",
                        "anchor": { "canonicalName": "persisted-anchor", "anchorType": "preference" },
                        "title": "Persisted Anchor",
                        "content": "Should be skipped next time",
                        "aliases": ["persisted"],
                        "facets": ["test"],
                        "recallMode": "auto",
                        "sensitivity": "normal",
                        "confidence": 0.9
                    }
                ]
            }
            """)]);

        var observer = CreateObserver("persist-before-reply", client: firstClient);
        var replyProbe = CreateTestProbe("persist-before-reply-probe");

        observer.Tell(new SendUserMessage
        {
            SessionId = new SessionId("test-channel/persist-before-reply"),
            Content = "remember this"
        });

        observer.Tell(new DistillMemories(), replyProbe.Ref);
        var reply = await replyProbe.ExpectMsgAsync<SessionDistillationCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        var gate = new MemoryProposalGate();
        var gateResult = gate.Evaluate(
            reply.Proposals,
            "normal",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var persistProbe = CreateTestProbe("persist-before-reply-ack");
        observer.Tell(new RecordAcceptedDistillationProposals(gateResult.AcceptedProposals), persistProbe.Ref);
        await persistProbe.ExpectMsgAsync<AcceptedDistillationProposalsRecorded>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Watch(observer);
        Sys.Stop(observer);
        await ExpectTerminatedAsync(observer, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        var secondClient = new FakeChatClient();
        var recoveredObserver = CreateObserver("persist-before-reply", client: secondClient);
        var replayProbe = CreateTestProbe("persist-recovery-probe");

        recoveredObserver.Tell(new SendUserMessage
        {
            SessionId = new SessionId("test-channel/persist-before-reply"),
            Content = "check the skip list"
        });

        recoveredObserver.Tell(new DistillMemories(), replayProbe.Ref);
        await replayProbe.ExpectMsgAsync<SessionDistillationCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        var skipPrompt = secondClient.ReceivedMessages[0]
            .LastOrDefault(msg => msg.Role == Microsoft.Extensions.AI.ChatRole.User)?.Text;

        Assert.NotNull(skipPrompt);
        Assert.Contains("persisted-anchor", skipPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DistillMemories_only_persists_accepted_proposals_for_future_dedup()
    {
        var firstClient = new FakeChatClient();
        firstClient.PlannedResponses.Enqueue([new TextContent("""
            {
                "proposals": [
                    {
                        "operation": "upsert_document",
                        "memoryClass": "durable_fact",
                        "subjectKind": "user",
                        "subjectValue": "self",
                        "anchor": { "canonicalName": "accepted-anchor", "anchorType": "preference" },
                        "title": "Accepted Anchor",
                        "content": "Valid retained fact.",
                        "aliases": ["accepted"],
                        "facets": ["travel_profile"],
                        "recallMode": "auto",
                        "sensitivity": "normal",
                        "confidence": 0.95
                    },
                    {
                        "operation": "upsert_document",
                        "memoryClass": "durable_fact",
                        "subjectKind": "user",
                        "subjectValue": "self",
                        "anchor": { "canonicalName": "rejected-anchor", "anchorType": "preference" },
                        "title": "Rejected Anchor",
                        "content": "Missing retrieval metadata should be dropped.",
                        "aliases": [],
                        "facets": [],
                        "recallMode": "auto",
                        "sensitivity": "normal",
                        "confidence": 0.90
                    }
                ]
            }
            """)]);

        var observer = CreateObserver("accepted-only", client: firstClient);
        var replyProbe = CreateTestProbe("accepted-only-probe");

        observer.Tell(new SendUserMessage
        {
            SessionId = new SessionId("test-channel/accepted-only"),
            Content = "remember this"
        });

        observer.Tell(new DistillMemories(), replyProbe.Ref);
        var reply = await replyProbe.ExpectMsgAsync<SessionDistillationCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, reply.Proposals.Count);

        var gate = new MemoryProposalGate();
        var gateResult = gate.Evaluate(
            reply.Proposals,
            "normal",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var persistProbe = CreateTestProbe("accepted-only-persist-probe");
        observer.Tell(new RecordAcceptedDistillationProposals(gateResult.AcceptedProposals), persistProbe.Ref);
        await persistProbe.ExpectMsgAsync<AcceptedDistillationProposalsRecorded>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Watch(observer);
        Sys.Stop(observer);
        await ExpectTerminatedAsync(observer, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        var secondClient = new FakeChatClient();
        var recoveredObserver = CreateObserver("accepted-only", client: secondClient);
        var replayProbe = CreateTestProbe("accepted-only-replay-probe");

        recoveredObserver.Tell(new SendUserMessage
        {
            SessionId = new SessionId("test-channel/accepted-only"),
            Content = "what do you already know"
        });

        recoveredObserver.Tell(new DistillMemories(), replayProbe.Ref);
        await replayProbe.ExpectMsgAsync<SessionDistillationCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        var replayPrompt = secondClient.ReceivedMessages[0]
            .LastOrDefault(msg => msg.Role == Microsoft.Extensions.AI.ChatRole.User)?.Text;

        Assert.NotNull(replayPrompt);
        Assert.Contains("accepted-anchor", replayPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rejected-anchor", replayPrompt, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void BuildDistillationUserPrompt_includes_existing_proposals_for_legacy_anchor_context()
    {
        var prompt = SessionMemoryObserverActor.BuildDistillationUserPrompt(
            "test-channel/legacy-recovery",
            1,
            "check legacy skip context",
            [new ProposedMemoryContext("legacy-anchor", "legacy-anchor", string.Empty)]);

        Assert.Contains("legacy-anchor", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("existingProposals", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DistillationSystemPrompt_contains_explicit_operation_mapping_and_examples()
    {
        var prompt = SessionMemoryObserverActor.BuildDistillationSystemPrompt();

        // Must contain explicit operation-class mapping rules
        Assert.Contains("evidence -> operation MUST be \"append_record\"", prompt, StringComparison.Ordinal);
        Assert.Contains("durable_fact -> operation MUST be \"upsert_document\"", prompt, StringComparison.Ordinal);

        // Must contain both example operations
        Assert.Contains("\"operation\": \"append_record\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"operation\": \"upsert_document\"", prompt, StringComparison.Ordinal);

        // Must contain both example memory classes
        Assert.Contains("\"memoryClass\": \"evidence\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"memoryClass\": \"durable_fact\"", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Simple parent actor that creates the observer as its child and forwards
    /// all received messages to a probe. This lets tests verify messages sent
    /// to <c>Context.Parent</c> by the observer (idle-triggered distillation).
    /// </summary>
    private sealed class ForwardingParent : UntypedActor
    {
        private readonly IActorRef _child;
        private readonly IActorRef _probe;

        public ForwardingParent(Props childProps, IActorRef probe)
        {
            _probe = probe;
            _child = Context.ActorOf(childProps, "observer");
        }

        protected override void OnReceive(object message)
        {
            if (message is GetChild)
            {
                Sender.Tell(_child);
                return;
            }

            // Forward everything from child to probe
            _probe.Forward(message);
        }
    }

    private sealed class GetChild
    {
        public static readonly GetChild Instance = new();
    }
}
