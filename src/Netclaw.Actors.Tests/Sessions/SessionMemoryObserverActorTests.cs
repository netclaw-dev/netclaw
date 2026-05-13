// -----------------------------------------------------------------------
// <copyright file="SessionMemoryObserverActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tests.Hosting;
using VerifyXunit;
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
            .WithInMemorySnapshotStore()
            .WithNetclawSerialization()
            .WithSerializationVerification();
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
            (SessionId)"test-channel/legacy-recovery",
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

    [Fact]
    public void DistillationSystemPrompt_forbids_preamble_postscript_and_markdown_fences()
    {
        var prompt = SessionMemoryObserverActor.BuildDistillationSystemPrompt();

        Assert.Contains("Do NOT include any text before or after the JSON", prompt, StringComparison.Ordinal);
        Assert.Contains("Do NOT wrap the JSON in markdown code fences", prompt, StringComparison.Ordinal);
        Assert.Contains("must start with `{` and end with `}`", prompt, StringComparison.Ordinal);
    }

    // ── Prompt snapshot tests (Fix 2.5) ────────────────────────────────
    //
    // These tests lock down the distillation prompts via Verify snapshot
    // testing. They catch ANY byte-level change to the prompt (added or
    // removed examples, reworded instructions, casing changes, whitespace
    // edits) and force human review on every prompt edit by producing a
    // readable diff between the current prompt and the approved baseline.
    //
    // When the prompt intentionally changes:
    //   1. Run this test — it will fail with a `*.received.txt` file
    //      written next to the test source containing the new prompt.
    //   2. Compare it to the existing `*.verified.txt` baseline (the diff
    //      tool will pop up automatically in IDE; CLI users can `diff`
    //      the two files).
    //   3. If the change is intentional, replace the verified file with
    //      the received file (`mv ...received.txt ...verified.txt`).
    //   4. Commit the prompt change and the updated `verified.txt`
    //      together.
    //
    // What this does NOT catch: semantic changes that keep the same
    // byte-shape (none in practice), and prompt drift in the model itself
    // (that's what the eval suite is for). What it DOES catch: the casual
    // edit that drops an example, swaps a field name, or tightens a rule
    // in a way that breaks the contract Qwen has been honoring in
    // production.
    //
    // Synthetic content note: the user-prompt snapshot is built from
    // entirely synthetic inputs. No real session transcripts are baked
    // into the verified file (per the discretion rule).

    [Fact]
    public Task DistillationSystemPrompt_matches_approved_snapshot()
    {
        var prompt = SessionMemoryObserverActor.BuildDistillationSystemPrompt();
        return Verifier.Verify(prompt, extension: "txt");
    }

    [Fact]
    public Task DistillationUserPromptTemplate_matches_approved_snapshot()
    {
        var prompt = SessionMemoryObserverActor.BuildDistillationUserPrompt(
            sessionId: (SessionId)"synthetic/snapshot",
            turnCount: 3,
            transcript: "user: synthetic transcript line one\nassistant: synthetic response line one",
            existingProposals: [new ProposedMemoryContext("synthetic-anchor", "Synthetic Anchor", "snippet")]);

        // The user prompt is a single-line serialized JSON. Pretty-print it
        // before snapshotting so any future diff is readable per-field rather
        // than a wall-of-text on one line.
        using var doc = System.Text.Json.JsonDocument.Parse(prompt);
        var indented = System.Text.Json.JsonSerializer.Serialize(
            doc.RootElement,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        return Verifier.Verify(indented, extension: "json");
    }

    // ── Parser tests (Fix 1c) ──────────────────────────────────────────
    //
    // The pre-fix parser used `text[IndexOf('{')..LastIndexOf('}')]` which
    // captured everything between the first `{` (often inside preamble or
    // an echoed transcript snippet) and the last `}` of the actual JSON
    // object, producing malformed JSON and silently returning an empty
    // proposal list. These tests exercise the failure shapes Qwen3.5-27B
    // produces in production: preamble braces, markdown fences, refusal
    // text, multiple JSON objects, trailing chatter.

    private const string CleanProposalJson = """
        {
          "proposals": [
            {
              "operation": "upsert_document",
              "memoryClass": "durable_fact",
              "subjectKind": "user",
              "subjectValue": "self",
              "anchor": { "canonicalName": "test-anchor", "anchorType": "preference" },
              "title": "Test Anchor",
              "content": "Test content for the anchor.",
              "aliases": ["alias-one"],
              "facets": ["test_facet"],
              "recallMode": "auto",
              "sensitivity": "normal",
              "confidence": 0.9
            }
          ]
        }
        """;

    [Fact]
    public void ParseProposals_reports_empty_input_for_null_or_whitespace()
    {
        var empty = SessionMemoryObserverActor.ParseProposals(string.Empty);
        Assert.Empty(empty.Proposals);
        Assert.Equal(SessionMemoryObserverActor.ParseProposalsOutcome.EmptyInput, empty.Outcome);

        var whitespace = SessionMemoryObserverActor.ParseProposals("   \n\t  ");
        Assert.Empty(whitespace.Proposals);
        Assert.Equal(SessionMemoryObserverActor.ParseProposalsOutcome.EmptyInput, whitespace.Outcome);
    }

    [Fact]
    public void ParseProposals_succeeds_on_clean_json()
    {
        var result = SessionMemoryObserverActor.ParseProposals(CleanProposalJson);

        Assert.Equal(SessionMemoryObserverActor.ParseProposalsOutcome.Success, result.Outcome);
        var proposal = Assert.Single(result.Proposals);
        Assert.Equal("test-anchor", proposal.Anchor!.CanonicalName);
    }

    [Fact]
    public void ParseProposals_succeeds_on_empty_proposals_array()
    {
        var result = SessionMemoryObserverActor.ParseProposals(@"{ ""proposals"": [] }");

        Assert.Equal(SessionMemoryObserverActor.ParseProposalsOutcome.Success, result.Outcome);
        Assert.Empty(result.Proposals);
    }

    [Fact]
    public void ParseProposals_recovers_from_preamble_text_with_braces()
    {
        // The smoking-gun failure mode: Qwen emits chain-of-thought before
        // the JSON, often containing brace-delimited enumerations like
        // "{tools, decisions, projects}". The old parser took the substring
        // from the first `{` in the preamble to the last `}` of the real
        // JSON object, producing malformed JSON.
        var text = "Looking at the conversation I see {tools, decisions, projects} mentioned. Here's my analysis:\n\n" + CleanProposalJson;

        var result = SessionMemoryObserverActor.ParseProposals(text);

        Assert.Equal(SessionMemoryObserverActor.ParseProposalsOutcome.Success, result.Outcome);
        var proposal = Assert.Single(result.Proposals);
        Assert.Equal("test-anchor", proposal.Anchor!.CanonicalName);
    }

    [Fact]
    public void ParseProposals_recovers_from_markdown_code_fence_wrapper()
    {
        var text = "```json\n" + CleanProposalJson + "\n```";

        var result = SessionMemoryObserverActor.ParseProposals(text);

        Assert.Equal(SessionMemoryObserverActor.ParseProposalsOutcome.Success, result.Outcome);
        var proposal = Assert.Single(result.Proposals);
        Assert.Equal("test-anchor", proposal.Anchor!.CanonicalName);
    }

    [Fact]
    public void ParseProposals_recovers_from_unlabeled_code_fence_wrapper()
    {
        var text = "```\n" + CleanProposalJson + "\n```";

        var result = SessionMemoryObserverActor.ParseProposals(text);

        Assert.Equal(SessionMemoryObserverActor.ParseProposalsOutcome.Success, result.Outcome);
        var proposal = Assert.Single(result.Proposals);
        Assert.Equal("test-anchor", proposal.Anchor!.CanonicalName);
    }

    [Fact]
    public void ParseProposals_recovers_from_trailing_chatter()
    {
        var text = CleanProposalJson + "\n\nLet me know if you need anything else!";

        var result = SessionMemoryObserverActor.ParseProposals(text);

        Assert.Equal(SessionMemoryObserverActor.ParseProposalsOutcome.Success, result.Outcome);
        var proposal = Assert.Single(result.Proposals);
        Assert.Equal("test-anchor", proposal.Anchor!.CanonicalName);
    }

    [Fact]
    public void ParseProposals_recovers_from_multiple_json_objects()
    {
        // First object is unrelated stray JSON, second is the real proposals.
        // The walker should try each candidate in order until one parses to a
        // DistillationResponse with a non-null Proposals list.
        var text = "{ \"unrelatedField\": \"some value\" }\n\n" + CleanProposalJson;

        var result = SessionMemoryObserverActor.ParseProposals(text);

        Assert.Equal(SessionMemoryObserverActor.ParseProposalsOutcome.Success, result.Outcome);
        var proposal = Assert.Single(result.Proposals);
        Assert.Equal("test-anchor", proposal.Anchor!.CanonicalName);
    }

    [Fact]
    public void ParseProposals_reports_no_json_found_on_refusal_text()
    {
        var result = SessionMemoryObserverActor.ParseProposals(
            "There is nothing memory-worthy in this conversation.");

        Assert.Empty(result.Proposals);
        Assert.Equal(SessionMemoryObserverActor.ParseProposalsOutcome.NoJsonFound, result.Outcome);
        Assert.Equal(0, result.CandidateCount);
        Assert.NotNull(result.Preview);
    }

    [Fact]
    public void ParseProposals_reports_parse_failed_when_candidates_exist_but_none_match()
    {
        // Walker finds two stray JSON objects, neither of which deserialize
        // into a DistillationResponse with a non-null Proposals array.
        // Should be ParseFailed (NOT NoJsonFound), because the distinction
        // matters for diagnosing prompt vs format issues.
        var result = SessionMemoryObserverActor.ParseProposals(
            "{ \"unrelated\": \"data\" } and { \"more\": \"unrelated\" }");

        Assert.Empty(result.Proposals);
        Assert.Equal(SessionMemoryObserverActor.ParseProposalsOutcome.ParseFailed, result.Outcome);
        Assert.Equal(2, result.CandidateCount);
        Assert.NotNull(result.Preview);
    }

    [Fact]
    public void ParseProposals_reports_parse_failed_on_truncated_json()
    {
        var truncated = "{ \"proposals\": [ { \"title\": \"foo\", \"content\": \"bar\"";

        var result = SessionMemoryObserverActor.ParseProposals(truncated);

        Assert.Empty(result.Proposals);
        // Truncated JSON has an opening `{` but no matching close, so the
        // walker reports zero candidates → NoJsonFound. Locks the contract.
        Assert.Equal(SessionMemoryObserverActor.ParseProposalsOutcome.NoJsonFound, result.Outcome);
    }

    // ── ExtractJsonObjectCandidates / StripMarkdownFences direct unit tests ──

    [Fact]
    public void StripMarkdownFences_strips_json_fenced_block()
    {
        var input = "```json\n{\"key\":\"value\"}\n```";
        Assert.Equal("{\"key\":\"value\"}", SessionMemoryObserverActor.StripMarkdownFences(input));
    }

    [Fact]
    public void StripMarkdownFences_returns_input_unchanged_when_no_fence()
    {
        var input = "{\"key\":\"value\"}";
        Assert.Equal(input, SessionMemoryObserverActor.StripMarkdownFences(input));
    }

    [Fact]
    public void ExtractJsonObjectCandidates_finds_each_top_level_object()
    {
        var text = "preamble {\"a\":1} more {\"b\":2,\"nested\":{\"c\":3}} trailing";

        var candidates = SessionMemoryObserverActor.ExtractJsonObjectCandidates(text);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("{\"a\":1}", candidates[0]);
        Assert.Equal("{\"b\":2,\"nested\":{\"c\":3}}", candidates[1]);
    }

    [Fact]
    public void ExtractJsonObjectCandidates_ignores_braces_inside_strings()
    {
        // Brace inside a JSON string literal must not affect depth counting.
        var text = "{\"text\":\"contains } brace\"}";

        var candidates = SessionMemoryObserverActor.ExtractJsonObjectCandidates(text);

        var single = Assert.Single(candidates);
        Assert.Equal(text, single);
    }

    [Fact]
    public void ExtractJsonObjectCandidates_handles_escaped_quotes_inside_strings()
    {
        var text = "{\"text\":\"escaped \\\" quote and } brace\"}";

        var candidates = SessionMemoryObserverActor.ExtractJsonObjectCandidates(text);

        var single = Assert.Single(candidates);
        Assert.Equal(text, single);
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

    private sealed class GetChild : INoSerializationVerificationNeeded
    {
        public static readonly GetChild Instance = new();
    }
}
