// -----------------------------------------------------------------------
// <copyright file="LegacyCoordinationReplayTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Hosting;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Protocol;

public sealed class LegacyCoordinationReplayTests : TestKit
{
    private const string SourceRevision = "9f79c0c4b637f005ffba5572d0bf50aa550464f2";
    private const string Owner = "signalr/child-ledger-owner";

    public LegacyCoordinationReplayTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
        => builder.WithNetclawSerialization();

    [Fact]
    public void Fixed_ordinary_adoption_keeps_the_adopted_context_after_snapshot_restore()
    {
        var serializer = Codec();
        var input = Assert.IsType<InputAdmitted>(Deserialize(serializer, "input-admitted-a-no-source-id"));
        var state = SessionState.Empty.Apply(input);

        state = Replay(serializer, state, "valid-ordinary-input-adoption");

        var inputId = new InputId("legacy-input-a");
        Assert.True(state.TryGetLegacyInputContext(inputId, out var context));
        Assert.Equal(new SessionId(Owner), context.SessionId);
        var restored = SessionState.FromSnapshot(RoundTrip(serializer, state.ToSnapshot()));
        Assert.True(restored.TryGetLegacyInputContext(inputId, out var restoredContext));
        Assert.Equal(context, restoredContext);
    }

    [Fact]
    public void Fixed_invalid_adoption_stays_quarantined_after_snapshot_restore()
    {
        var serializer = Codec();
        var inputA = Assert.IsType<InputAdmitted>(Deserialize(serializer, "input-admitted-a-no-source-id"));
        var inputB = Assert.IsType<InputAdmitted>(Deserialize(serializer, "input-admitted-b-no-source-id"));
        var state = SessionState.Empty.Apply(inputA).Apply(inputB);

        state = Replay(serializer, state, "invalid-2453-adoption-of-b-while-a-pending");
        var quarantinedId = new InputId("legacy-input-b");
        Assert.True(state.IsLegacyInputQuarantined(quarantinedId));
        Assert.DoesNotContain(state.History, message => message.Content == "Neutral task B.");

        var restored = SessionState.FromSnapshot(RoundTrip(serializer, state.ToSnapshot()));
        Assert.True(restored.IsLegacyInputQuarantined(quarantinedId));
        Assert.DoesNotContain(restored.History, message => message.Content == "Neutral task B.");
    }

    [Fact]
    public void Fixed_child_lifecycle_replays_the_deployed_source_operation_pair()
    {
        var serializer = Codec();
        var originalCall = new SerializableChatMessage
        {
            Role = ChatRole.Assistant,
            ToolCalls =
            [
                new SerializableToolCall
                {
                    CallId = new ToolCallId("start-call"),
                    Name = new ToolName("spawn_agent"),
                    ArgumentsJson = "{}"
                }
            ]
        };
        var state = SessionState.Empty with { History = [originalCall] };
        foreach (var name in new[]
        {
            "child-run-accepted", "child-started", "child-checkpoint", "child-terminal",
            "child-result-prepared", "child-delivery-admitted", "valid-child-delivery-adoption"
        })
        {
            state = Replay(serializer, state, name);
        }

        var runId = new SubAgentRunId("accepted-run");
        var reported = Assert.IsType<ChildRunLifecycle.Reported>(state.ChildRuns[runId].Lifecycle);
        Assert.Equal(new ChildRunReport.LegacyDelivery(new ToolCallId("legacy-delivery-call")), reported.Report);
        var call = Assert.Single(state.History.SelectMany(message => message.ToolCalls), item => item.CallId.Value == "legacy-delivery-call");
        Assert.Equal("spawn_agent", call.Name.Value);
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("accepted-run", arguments.RootElement.GetProperty("run_id").GetString());
        Assert.Equal("spawn_agent", arguments.RootElement.GetProperty("source_operation").GetString());
        var result = Assert.Single(state.History, message => message.ToolCallId?.Value == "legacy-delivery-call");
        Assert.Equal("spawn_agent", result.Name);
        Assert.Equal(ChatRole.Tool, result.Role);
    }

    [Fact]
    public void Fixed_legacy_batch_settles_child_after_consumed_ids_leave_the_final_turn()
    {
        var serializer = Codec();
        var originalCall = new SerializableChatMessage
        {
            Role = ChatRole.Assistant,
            ToolCalls =
            [
                new SerializableToolCall
                {
                    CallId = new ToolCallId("start-call"),
                    Name = new ToolName("spawn_agent"),
                    ArgumentsJson = "{}"
                }
            ]
        };
        var state = SessionState.Empty with { History = [originalCall] };
        var workflow = new SessionToolWorkflow();
        foreach (var name in new[]
        {
            "child-run-accepted", "child-started", "child-checkpoint", "child-terminal",
            "child-result-prepared", "child-delivery-admitted", "valid-child-delivery-adoption",
            "child-downstream-tool-batch", "child-downstream-tool-result", "child-downstream-final-turn"
        })
        {
            state = Replay(serializer, state, name, workflow);
        }

        var runId = new SubAgentRunId("accepted-run");
        Assert.IsType<ChildRunLifecycle.Reported>(state.ChildRuns[runId].Lifecycle);
        var downstreamResult = Assert.Single(state.History,
            message => message.ToolCallId?.Value == "downstream-call");
        Assert.Equal("file_read", downstreamResult.Name);
        Assert.Equal(ChatRole.Tool, downstreamResult.Role);
        Assert.Equal("Neutral downstream result.", downstreamResult.Content);
        Assert.DoesNotContain(state.LegacyCoordinationEvidence,
            evidence => evidence.Disposition == LegacyCoordinationDisposition.AppliedChildContinuation);
    }

    [Fact]
    public void Fixed_ambiguous_snapshot_keeps_child_delivery_unresolved_without_a_new_pair_id()
    {
        var serializer = Codec();
        var snapshot = Assert.IsType<SessionSnapshot>(Deserialize(serializer, "ambiguous-child-delivery-snapshot"));

        var restored = SessionState.FromSnapshot(snapshot);

        var runId = new SubAgentRunId("accepted-run");
        Assert.True(restored.TryGetLegacyChildDisposition(runId, out var evidence));
        Assert.Equal(LegacyCoordinationDisposition.UnresolvedLegacyChild, evidence.Disposition);
        Assert.Null(evidence.DeliveryCallId);
        var quarantinedId = new InputId("child-result-accepted-run");
        Assert.True(restored.IsLegacyInputQuarantined(quarantinedId));
        Assert.Empty(restored.GetExecutableChildRuns());
        Assert.DoesNotContain(restored.History.SelectMany(message => message.ToolCalls),
            call => call.Name.Value == "child_run_result");
    }

    [Fact]
    public void Fixed_child_snapshot_preserves_the_exact_deployed_source_operation_pair()
    {
        var serializer = Codec();
        var snapshot = Assert.IsType<SessionSnapshot>(Deserialize(serializer, "legacy-child-pair-snapshot"));

        var restored = SessionState.FromSnapshot(snapshot);

        var run = Assert.Single(restored.ChildRuns.Values);
        Assert.Equal(new ChildRunReport.LegacyDelivery(new ToolCallId("legacy-delivery-call")),
            Assert.IsType<ChildRunLifecycle.Reported>(run.Lifecycle).Report);
        var call = Assert.Single(restored.History.SelectMany(message => message.ToolCalls), item => item.CallId.Value == "legacy-delivery-call");
        Assert.Equal("spawn_agent", call.Name.Value);
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("accepted-run", arguments.RootElement.GetProperty("run_id").GetString());
        Assert.Equal("spawn_agent", arguments.RootElement.GetProperty("source_operation").GetString());
        Assert.Equal("spawn_agent", Assert.Single(restored.History, message => message.ToolCallId == call.CallId).Name);

        Assert.Equal(1, restored.History.SelectMany(message => message.ToolCalls).Count(item => item.CallId == call.CallId));
        Assert.Equal(1, restored.History.Count(message => message.ToolCallId == call.CallId));
    }

    [Fact]
    public void Ordinary_legacy_snapshot_preserves_history_context_and_jobs_once()
    {
        var serializer = Codec();
        var snapshot = Assert.IsType<SessionSnapshot>(Deserialize(serializer, "session-snapshot"));

        var restored = SessionState.FromSnapshot(snapshot);

        Assert.Equal("Neutral task A.", Assert.Single(restored.History).Content);
        Assert.Equal("/repo/ordinary", restored.WorkingContext.ProjectDirectory);
        var job = Assert.Single(restored.ActiveBackgroundJobs.Values);
        Assert.Equal("legacy-job-1", job.JobId.Value);
        Assert.Equal("dotnet test", job.Command);
        Assert.DoesNotContain(restored.LegacyCoordinationEvidence, item => item.Payload.Length > 0);

        var replayed = SessionState.FromSnapshot(RoundTrip(serializer, restored.ToSnapshot()));
        Assert.Equal(restored.History, replayed.History);
        Assert.Equal(restored.WorkingContext, replayed.WorkingContext);
        Assert.Equal(restored.ActiveBackgroundJobs, replayed.ActiveBackgroundJobs);
        Assert.DoesNotContain(replayed.LegacyCoordinationEvidence, item => item.Payload.Length > 0);
    }

    [Fact]
    public void Prepared_undelivered_legacy_child_remains_ready_after_snapshot_restore()
    {
        var serializer = Codec();
        var snapshot = Assert.IsType<SessionSnapshot>(Deserialize(serializer, "prepared-undelivered-child-snapshot"));

        var restored = SessionState.FromSnapshot(snapshot);

        var run = Assert.Single(restored.ChildRuns.Values);
        Assert.IsType<ChildRunLifecycle.Terminal>(run.Lifecycle);
        Assert.False(restored.TryGetLegacyChildDisposition(run.RunId, out _));
        Assert.DoesNotContain(restored.History.SelectMany(message => message.ToolCalls),
            call => call.Name.Value == "child_run_result");

        var replayed = SessionState.FromSnapshot(RoundTrip(serializer, restored.ToSnapshot()));
        Assert.IsType<ChildRunLifecycle.Terminal>(Assert.Single(replayed.ChildRuns.Values).Lifecycle);
        Assert.False(replayed.TryGetLegacyChildDisposition(run.RunId, out _));
    }

    [Fact]
    public void No_writer_can_persist_a_child_continuation_and_child_approval_fields_survive_wire_round_trip()
    {
        var serializer = Codec();
        var continuation = new ChildContinuation(new SubAgentRunId("wire-run"), new ToolCallId("wire-call"));
        // The continuation is read-only legacy data. A writer that sets it fails loudly at persistence.
        Assert.Throws<InvalidDataException>(() => serializer.ToBinary(new ToolBatchStarted
        {
            SessionId = new SessionId(Owner),
            ChildContinuation = continuation,
            StartedAtMs = 10
        }));
        Assert.Throws<InvalidDataException>(() => serializer.ToBinary(new TurnRecorded
        {
            SessionId = new SessionId(Owner),
            ChildContinuation = continuation,
            RecordedAtMs = 11
        }));
        Assert.Throws<InvalidDataException>(() => serializer.ToBinary(new InputClosed
        {
            SessionId = new SessionId(Owner),
            ChildContinuation = continuation,
            ClosedAtMs = 12
        }));
        Assert.Throws<InvalidDataException>(() => serializer.ToBinary(
            new ChildRunEvent.ResultPrepared(ChildCall(CreateRun("wire-run"), continuation.DeliveryCallId),
                new SerializableChatMessage { Role = ChatRole.Tool, ToolCallId = continuation.DeliveryCallId }, null)
            {
                SessionId = new SessionId(Owner),
                RunId = continuation.RunId,
                RecordedAtMs = 13
            }));
        var requested = RoundTrip(serializer, new ToolApprovalRequested
        {
            SessionId = new SessionId(Owner),
            CallId = "approval-call",
            OriginalChildCallId = new ToolCallId("child-tool-call"),
            SourceChildRunId = continuation.RunId,
            ToolName = "file_write",
            TurnContext = new TurnContextRecord { SessionId = new SessionId(Owner), TurnId = "turn-1" },
            RequestedAtMs = 13
        });
        var resolved = RoundTrip(serializer, new ToolApprovalResolved
        {
            SessionId = new SessionId(Owner),
            CallId = "approval-call",
            SourceChildRunId = continuation.RunId,
            AuthorizationAttemptId = "attempt-1",
            Decision = "approve_once",
            ResolvedAtMs = 14
        });

        Assert.Equal(continuation.RunId, requested.SourceChildRunId);
        Assert.Equal(new ToolCallId("child-tool-call"), requested.OriginalChildCallId);
        Assert.Equal("turn-1", requested.TurnContext?.TurnId);
        Assert.Equal(continuation.RunId, resolved.SourceChildRunId);
        Assert.Equal("attempt-1", resolved.AuthorizationAttemptId);
    }

    [Fact]
    public void Fixed_extended_event_records_keep_their_deployed_fields()
    {
        var serializer = Codec();
        var closed = Assert.IsType<InputClosed>(Deserialize(serializer, "input-closed"));
        var batch = Assert.IsType<ToolBatchStarted>(Deserialize(serializer, "tool-batch-started"));
        var result = Assert.IsType<ToolCallRecorded>(Deserialize(serializer, "tool-call-recorded"));
        var requested = Assert.IsType<ToolApprovalRequested>(Deserialize(serializer, "tool-approval-requested"));
        var resolved = Assert.IsType<ToolApprovalResolved>(Deserialize(serializer, "tool-approval-resolved"));

        Assert.Equal(new SessionId(Owner), closed.SessionId);
        Assert.Equal([new InputId("legacy-input-b")], closed.InputIds);
        Assert.Equal(123464, closed.ClosedAtMs);
        Assert.Equal(new SessionId(Owner), batch.SessionId);
        Assert.Equal("Neutral task.", batch.UserMessage.Content);
        Assert.Equal(ChatRole.Assistant, batch.AssistantMessage.Role);
        Assert.Equal([new InputId("legacy-input-b")], batch.ConsumedInputIds);
        Assert.Equal(123465, batch.StartedAtMs);
        Assert.Equal("neutral-read-call", result.ToolResult.ToolCallId?.Value);
        Assert.Equal("file_read", result.ToolResult.Name);
        Assert.Equal("Neutral result.", result.ToolResult.Content);
        Assert.Equal(123466, result.RecordedAtMs);
        Assert.Equal(new SessionId(Owner), requested.SessionId);
        Assert.Equal("neutral-approval-call", requested.CallId);
        Assert.Equal("neutral-attempt", requested.AuthorizationAttemptId);
        Assert.Equal("file_write", requested.ToolName);
        Assert.Equal(["file_write"], requested.Patterns);
        Assert.Equal(["file_write"], requested.CandidateVerbs);
        Assert.Equal(123467, requested.RequestedAtMs);
        Assert.Equal(new SessionId(Owner), resolved.SessionId);
        Assert.Equal("neutral-approval-call", resolved.CallId);
        Assert.Equal("neutral-attempt", resolved.AuthorizationAttemptId);
        Assert.Equal("approve_once", resolved.Decision);
        Assert.Equal(123468, resolved.ResolvedAtMs);
    }

    [Fact]
    public void Serialized_child_lifecycle_retains_findings_context_and_failed_review_decision()
    {
        var serializer = Codec();
        var run = CreateRun("wire-review-run");
        var initial = new WorkingContext
        {
            ProjectDirectory = "/parent/project",
            RecentFiles = ["/parent/kept.txt"]
        };
        var state = SessionState.Empty with { WorkingContext = initial };
        state = state.Apply(RoundTrip(serializer, new ChildRunAccepted
        {
            SessionId = new SessionId(Owner),
            Run = run,
            RecordedAtMs = 20
        }));

        var delta = new WorkingContextDelta
        {
            ProjectDirectory = "/child/project",
            ReadFiles = ["/child/read.txt"],
            ConfirmedChangedFiles = ["/child/changed.txt"]
        };
        var terminal = new ChildRunTerminal(new SubAgentProtocol.SubAgentResult
        {
            AgentName = run.AgentName,
            RunId = run.RunId,
            ScopeId = run.ScopeId,
            Output = "The child completed with a finding.",
            Completion = new ChildRunCompletion.Completed(delta),
            Findings =
            [
                new SubAgentFinding
                {
                    Title = "Verified result",
                    Content = "One fact passed review.",
                    Evidence = ["/child/changed.txt"]
                }
            ],
            FindingsCount = 1
        }, Lost: false, EvidenceWarning: "Working-context review failed.");
        state = state.Apply(RoundTrip(serializer, new ChildRunEvent.TerminalRecorded(terminal)
        {
            SessionId = new SessionId(Owner),
            RunId = run.RunId,
            RecordedAtMs = 21
        }));
        var callId = new ToolCallId("wire-review-call");
        var call = ChildCall(run, callId);
        var toolResult = new SerializableChatMessage
        {
            Role = ChatRole.Tool,
            Name = "child_run_result",
            ToolCallId = callId,
            Content = "{\"warning\":\"Working-context review failed.\",\"findings\":[{\"title\":\"Verified result\"}]}"
        };
        // The deployed rows are read-only data. The reader applies them from memory because no writer can persist them.
        state = state.Apply(new ChildRunEvent.ResultPrepared(call, toolResult, ReviewedWorkingContext: null)
        {
            SessionId = new SessionId(Owner),
            RunId = run.RunId,
            RecordedAtMs = 22
        });
        state = new SessionToolWorkflow().ApplyBatchStarted(state, new ToolBatchStarted
        {
            SessionId = new SessionId(Owner),
            ChildContinuation = new ChildContinuation(run.RunId, callId),
            StartedAtMs = 23
        });
        state = state.Apply(new TurnRecorded
        {
            SessionId = new SessionId(Owner),
            ChildContinuation = new ChildContinuation(run.RunId, callId),
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Completed." },
            RecordedAtMs = 24
        });

        Assert.Equal(SubAgentRunOutcome.Completed, Assert.IsType<ChildRunLifecycle.Reported>(state.ChildRuns[run.RunId].Lifecycle).Outcome);
        Assert.Equal(initial.ProjectDirectory, state.WorkingContext.ProjectDirectory);
        Assert.Equal(initial.RecentFiles, state.WorkingContext.RecentFiles);
        Assert.Contains("Working-context review failed.", Assert.Single(state.History, message => message.ToolCallId == callId).Content);
        Assert.Contains("Verified result", Assert.Single(state.History, message => message.ToolCallId == callId).Content);

        var restored = SessionState.FromSnapshot(RoundTrip(serializer, state.ToSnapshot()));
        Assert.Equal(initial.ProjectDirectory, restored.WorkingContext.ProjectDirectory);
        Assert.Equal(initial.RecentFiles, restored.WorkingContext.RecentFiles);
        Assert.Equal(state.History.Count, restored.History.Count);
        for (var index = 0; index < state.History.Count; index++)
        {
            var expected = state.History[index];
            var actual = restored.History[index];
            Assert.Equal(expected.Role, actual.Role);
            Assert.Equal(expected.Content, actual.Content);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.ToolCallId, actual.ToolCallId);
            Assert.Equal(expected.ToolCalls.Select(static call => (call.CallId, call.Name, call.ArgumentsJson)),
                actual.ToolCalls.Select(static call => (call.CallId, call.Name, call.ArgumentsJson)));
        }
        Assert.Equal(1, restored.History.Count(message => message.ToolCallId == callId));
    }

    private NetclawProtobufSerializer Codec()
        => new((Akka.Actor.ExtendedActorSystem)Sys);

    private object Deserialize(NetclawProtobufSerializer serializer, string name)
    {
        var corpus = JsonSerializer.Deserialize<FixtureCorpus>(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CoordinationLegacy", "legacy-writer-records.json")))!;
        Assert.Equal(SourceRevision, corpus.SourceRevision);
        Assert.Equal(150, corpus.SerializerIdentifier);
        var record = Assert.Single(corpus.Records, item => item.Name == name);
        var bytes = Convert.FromBase64String(record.Base64);
        Assert.Equal(record.Sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        return serializer.FromBinary(bytes, record.Manifest);
    }

    private SessionState Replay(
        NetclawProtobufSerializer serializer,
        SessionState state,
        string name,
        SessionToolWorkflow? workflow = null)
    {
        workflow ??= new SessionToolWorkflow();
        var evt = Deserialize(serializer, name);
        if (evt is LegacyCoordinationEvent legacy)
            return LegacyCoordinationReplay.Apply(state, legacy);

        return evt switch
        {
            ToolBatchStarted batch => workflow.ApplyBatchStarted(
                state, LegacyCoordinationReplay.TranslateParentDecision(state, batch)),
            ToolCallRecorded result => workflow.ApplyRecordedResult(state, result.ToolResult),
            TurnRecorded turn => ApplyParentTurn(state, turn),
            InputAdmitted input => state.Apply(input),
            _ => throw new InvalidDataException($"Fixture '{name}' is not a replayable session event.")
        };
    }

    private static SessionState ApplyParentTurn(SessionState state, TurnRecorded recorded)
    {
        var translated = LegacyCoordinationReplay.TranslateParentDecision(state, recorded);
        var userAlreadyInHistory = translated.ChildContinuation is not null || translated.ConsumedInputIds.Count > 0;
        return state.Apply(translated, userAlreadyInHistory);
    }

    private T RoundTrip<T>(NetclawProtobufSerializer serializer, T value) where T : notnull
    {
        var bytes = serializer.ToBinary(value);
        return Assert.IsType<T>(serializer.FromBinary(bytes, serializer.Manifest(value)));
    }

    private static BackgroundChildRun CreateRun(string id)
    {
        var context = new TurnContextRecord
        {
            SessionId = new SessionId(Owner),
            TurnId = "turn-1",
            Boundary = TrustBoundary.Public,
            RequesterPrincipal = PrincipalClassification.UntrustedExternal
        };
        return new BackgroundChildRun
        {
            RunId = new SubAgentRunId(id),
            ScopeId = new SubAgentScopeId($"{Owner}/child/{id}"),
            StartKey = new ChildRunStartKey.Slash(new SessionId(Owner), "turn-1", new InputId($"input-{id}")),
            AgentName = new AgentName("worker"),
            SourceOperation = "slash",
            ArgumentsDigest = $"digest-{id}",
            OriginalContext = context,
            InitialWorkingSnapshot = new WorkingContextSnapshot
            {
                WorkingContext = WorkingContext.Empty,
                Git = new GitWorkingContextInspection.Skipped()
            },
            AcceptedAtMs = 20,
            Lifecycle = new ChildRunLifecycle.Accepted(null)
        };
    }

    private static SerializableChatMessage ChildCall(BackgroundChildRun run, ToolCallId callId)
        => new()
        {
            Role = ChatRole.Assistant,
            ToolCalls =
            [
                new SerializableToolCall
                {
                    CallId = callId,
                    Name = new ToolName("child_run_result"),
                    ArgumentsJson = JsonSerializer.Serialize(new { run_id = run.RunId.Value, source_operation = run.SourceOperation })
                }
            ]
        };

    private sealed record FixtureCorpus(
        [property: JsonPropertyName("sourceRevision")] string SourceRevision,
        [property: JsonPropertyName("serializerIdentifier")] int SerializerIdentifier,
        [property: JsonPropertyName("records")] FixtureRecord[] Records);

    private sealed record FixtureRecord(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("manifest")] string Manifest,
        [property: JsonPropertyName("base64")] string Base64,
        [property: JsonPropertyName("sha256")] string Sha256);
}
