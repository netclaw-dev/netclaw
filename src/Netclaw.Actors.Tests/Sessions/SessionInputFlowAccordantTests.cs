// -----------------------------------------------------------------------
// <copyright file="SessionInputFlowAccordantTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using CsCheck;
using Microsoft.Accordant;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Configuration;
using Xunit;
using AccordantSpec = Microsoft.Accordant.Spec<Netclaw.Actors.Tests.Sessions.InputFlowModelState>;
using ChatRole = Netclaw.Actors.Protocol.ChatRole;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// What an observer needs to predict the input flow: which source keys the journal admitted, which labelled inputs
/// wait in the buffer, whether a turn is open, and who the open turn serves. The model holds labels and requester
/// names only. It does not mirror the flow's lists or any <c>TurnContext</c>.
/// </summary>
[State]
public partial class InputFlowModelState
{
    public bool TurnOpen { get; set; }
    public string TurnRequester { get; set; } = string.Empty;
    public List<string> Buffer { get; set; } = [];
    public List<string> SeenKeys { get; set; } = [];
    public Dictionary<string, string> RequesterByLabel { get; set; } = new();
}

/// <summary>
/// Executable spec for <see cref="SessionInputFlow"/> admission, selection, and buffered batching, with the
/// reminder redelivery rules. A hand-written adapter drives the real flow with the same calls as
/// <c>LlmSessionActor</c>. The Accordant model predicts each observation. CsCheck chooses the operation order.
/// <para>
/// Sources of intent, by invariant:
/// </para>
/// <list type="bullet">
/// <item>Order, liveness: PRD-001 FR-017 "Ordinary user inputs SHALL retain their existing ordered batch";
/// <c>background-subagent-runs</c> "Ordinary inputs keep their order".</item>
/// <item>One admission per source key: <c>netclaw-session</c> "Reminder redelivery best-effort dedup"
/// (a redelivery is acked without a second input).</item>
/// <item>Pending input closes when a turn consumes it: <c>session-resume</c> "A completed turn stays quiet"
/// (a restart must not resume a consumed input).</item>
/// <item>Authority per input and per turn: PRD-001 FR-017 "... retain their existing ordered batch and authority";
/// <c>session-resume</c> "SignalR text success confirms durable admission" (the daemon preserves the input's
/// original authority); <c>netclaw-session</c> "Tool execution encapsulation" (tool authority derives from the
/// admitted TurnContext). The operator decided: a turn has one authority, fixed when it starts, and inputs with
/// different requesters never share a turn. An input from another requester waits for its own turn.
/// The older <c>docs/research/session-contract-test-plan.md</c> text and one <c>LlmSessionIntegrationTests</c>
/// case accept a mixed-requester batch. Those two follow this decision.</item>
/// </list>
/// </summary>
public sealed class SessionInputFlowAccordantTests(ITestOutputHelper output)
{
    // CsCheck replays this first case, then explores new random sequences. Failures report their own replay seed.
    private const string InitialSampleSeed = "0N0XIzNsQ0O2";

    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Automation = "reminder-bot";

    private enum Rule { Base, SingleRequesterPerBatch, ModelCallUsesBatchAuthority, RunningTurnKeepsItsAuthority }

    private enum Kind { AdmitOperator, AdmitReminder, FinishTurn, ToolBoundary, FailTurn }

    private enum Explored
    {
        Activated, Buffered, DuplicateOperator, DuplicateReminder, ReminderBuffered, FollowUpBatch,
        MixedRequesterBuffer, ToolBoundaryDrain, FailTurnFollowUp, ReminderFiresOfOneReminder
    }

    private sealed record Command(Kind Kind, string Requester, string? SourceMessageId)
    {
        public override string ToString() => Kind switch
        {
            Kind.AdmitOperator => $"Operator({Requester},msg={SourceMessageId ?? "none"})",
            Kind.AdmitReminder => $"Reminder(fire={SourceMessageId})",
            _ => Kind.ToString()
        };
    }

    private sealed record AdmitRequest(string Label, string Requester, string? SourceKey, string? ReminderFireKey);
    private sealed record AdmitObservation(string Outcome, string? AdmittedRequester);
    private sealed record AdvanceObservation(
        string Action,
        string[] BatchLabels,
        string? ExecutingRequester,
        string[] PendingLabels);

    [Fact]
    public void Generated_inputs_keep_order_dedup_and_closure()
        => RunProperty(Rule.Base);

    [Fact]
    public void Generated_batches_never_mix_requesters()
        => RunProperty(Rule.SingleRequesterPerBatch);

    [Fact]
    public void Generated_follow_up_model_calls_run_under_the_authority_of_the_batch_they_serve()
        => RunProperty(Rule.ModelCallUsesBatchAuthority);

    [Fact]
    public void Generated_inputs_from_another_requester_never_join_a_running_turn()
        => RunProperty(Rule.RunningTurnKeepsItsAuthority);

    // S6 current behavior, stated as a fact and not as a defect. A key names one fire of a reminder
    // ("{reminderId}:{fireTimeMs}"). Two fires of one reminder are two inputs today. Coalescing them is a design choice for later.
    [Fact]
    public void A_redelivered_fire_is_a_duplicate_and_a_second_fire_of_the_same_reminder_is_admitted()
    {
        // Arrange
        var sessionId = new SessionId("input-flow/reminder-fires");
        var flow = new SessionInputFlow();
        var state = SessionState.Empty;

        // Act
        var firstFire = Admit(flow, ref state, sessionId, "daily-report:1000");
        var redelivery = Admit(flow, ref state, sessionId, "daily-report:1000");
        var secondFire = Admit(flow, ref state, sessionId, "daily-report:2000");

        // Assert
        Assert.Equal("Admitted", firstFire);
        Assert.Equal("Duplicate", redelivery);
        Assert.Equal("Admitted", secondFire);
        Assert.Equal(2, state.PendingInputs.Count);

        static string Admit(SessionInputFlow flow, ref SessionState state, SessionId sessionId, string fireKey)
        {
            var command = new SendUserMessage
            {
                SessionId = sessionId,
                Content = "Run the report",
                Source = ReminderSource(fireKey)
            };
            if (flow.IsReminderDuplicate(command.Source!.ReminderId, state))
                return "Duplicate";
            switch (flow.PrepareAdmission(command, state, sessionId, 1))
            {
                case AdmissionDecision.Duplicate:
                    return "Duplicate";
                case AdmissionDecision.Persist persist:
                    state = state.Apply(persist.Event);
                    flow.AdmissionCommitted(persist.Event, command, InputPlacement.BufferDuringModel);
                    return "Admitted";
                default:
                    throw new InvalidOperationException("Unexpected admission decision.");
            }
        }
    }

    private void RunProperty(Rule rule)
    {
        // Arrange
        var spec = CreateSpec(rule);
        var sequences = GenerateSequences();
        var explored = new HashSet<Explored>();
        var cases = 0;
        var operations = 0;

        // Act: CsCheck chooses operation order and inputs. Accordant checks each observation.
        sequences.Sample(sequence =>
        {
            explored.UnionWith(new FlowScenario(spec, sequence).Run());
            cases++;
            operations += sequence.Length;
        }, iter: 1_000, seed: InitialSampleSeed, threads: 1, print: DescribeSequence);

        // Assert: These counts check exploration. The model rules below check behavior.
        output.WriteLine($"rule={rule}; first-case-seed={InitialSampleSeed}; cases={cases}; operations={operations}; explored={string.Join(',', explored)}");
        Assert.True(cases >= 1_000, $"CsCheck ran only {cases} cases.");
        Assert.True(operations > cases, $"Only {operations} operations ran in {cases} cases.");
        foreach (var required in Enum.GetValues<Explored>())
            Assert.Contains(required, explored);
    }

    private static string DescribeSequence(Command[] sequence)
        => string.Join(" -> ", sequence.Select((command, index) => $"c{index}:{command}"));

    private static Gen<Command[]> GenerateSequences()
    {
        var admitOperator = Gen.Select(
            Gen.OneOfConst(Alice, Bob),
            Gen.OneOfConst<string?>(null, "m0", "m1"),
            (requester, messageId) => new Command(Kind.AdmitOperator, requester, messageId));
        var admitReminder = Gen.OneOfConst("daily:1", "daily:2", "weekly:1")
            .Select(fire => new Command(Kind.AdmitReminder, Automation, fire));
        var terminal = Gen.OneOfConst(Kind.FinishTurn, Kind.ToolBoundary, Kind.FailTurn)
            .Select(kind => new Command(kind, string.Empty, null));
        return Gen.Frequency(
            (5, admitOperator),
            (3, admitReminder),
            (4, terminal)).Array[1, 12];
    }

    private static AccordantSpec CreateSpec(Rule rule)
    {
        var spec = new AccordantSpec();
        spec.Operation<AdmitRequest, AdmitObservation>("Admit", Admit);
        spec.Operation<Unit, AdvanceObservation>("FinishTurn", (_, state) => Terminal(state, rule));
        spec.Operation<Unit, AdvanceObservation>("FailTurn", (_, state) => Terminal(state, rule));
        spec.Operation<Unit, AdvanceObservation>("ToolBoundary", (_, state) => ToolBoundary(state, rule));
        return spec;
    }

    private static ExpectedOutcomes Admit(AdmitRequest request, InputFlowModelState state)
    {
        // Rule: one admission per source key. A second delivery of a key is a duplicate and adds no input.
        if (request.SourceKey is { } key && state.SeenKeys.Contains(key))
        {
            return Expect.That<AdmitObservation>(actual => actual.Outcome == "Duplicate"
                    ? ValidationResult.Valid()
                    : ValidationResult.Invalid(
                        $"Source key '{key}' was admitted already, so a second delivery must be a duplicate. Actual outcome: {actual.Outcome}."))
                .SameState();
        }

        var expectedOutcome = state.TurnOpen ? "Buffered" : "Activated";
        return Expect.That<AdmitObservation>(actual =>
            {
                if (actual.Outcome != expectedOutcome)
                    return ValidationResult.Invalid(
                        $"A new input must be {expectedOutcome} while turn open={state.TurnOpen}. Actual outcome: {actual.Outcome}.");
                // Rule: admission preserves the original authority of the input.
                return actual.AdmittedRequester == request.Requester
                    ? ValidationResult.Valid()
                    : ValidationResult.Invalid(
                        $"The journal must record requester '{request.Requester}' for {request.Label}. Actual: '{actual.AdmittedRequester}'.");
            })
            .ThenState<InputFlowModelState>(nextState =>
            {
                if (request.SourceKey is { } sourceKey)
                    nextState.SeenKeys.Add(sourceKey);
                nextState.RequesterByLabel[request.Label] = request.Requester;
                if (state.TurnOpen)
                {
                    nextState.Buffer.Add(request.Label);
                    return;
                }

                nextState.TurnOpen = true;
                nextState.TurnRequester = request.Requester;
            });
    }

    // A turn ends (recorded or failed). The flow either goes Ready or drains the buffer into a follow-up batch.
    private static ExpectedOutcomes Terminal(InputFlowModelState state, Rule rule)
        => Expect.That<AdvanceObservation>(actual =>
                ValidateAdvance(state, rule, actual, midTurn: false))
            .ThenState<InputFlowModelState>((AdvanceObservation actual, InputFlowModelState nextState) =>
            {
                ApplyAdvance(state, actual, nextState);
                nextState.TurnOpen = actual.Action == "FollowUp";
            }, mock: () => new AdvanceObservation("Ready", [], null, []));

    // A tool batch completes inside an open turn. The turn continues and may drain buffered inputs into it.
    private static ExpectedOutcomes ToolBoundary(InputFlowModelState state, Rule rule)
        => Expect.That<AdvanceObservation>(actual =>
                ValidateAdvance(state, rule, actual, midTurn: true))
            .ThenState<InputFlowModelState>((AdvanceObservation actual, InputFlowModelState nextState) =>
                ApplyAdvance(state, actual, nextState),
                mock: () => new AdvanceObservation("Continue", [], null, []));

    private static void ApplyAdvance(InputFlowModelState state, AdvanceObservation actual, InputFlowModelState nextState)
    {
        nextState.Buffer = [.. state.Buffer.Skip(actual.BatchLabels.Length)];
        if (actual.BatchLabels.Length > 0)
            nextState.TurnRequester = state.RequesterByLabel[actual.BatchLabels[0]];
    }

    private static ValidationResult ValidateAdvance(
        InputFlowModelState state, Rule rule, AdvanceObservation actual, bool midTurn)
    {
        // Rule: order. A batch is a prefix of the buffer, in admission order.
        if (!state.Buffer.Take(actual.BatchLabels.Length).SequenceEqual(actual.BatchLabels))
            return ValidationResult.Invalid(
                $"The batch must be a prefix of the buffer in admission order. Buffer [{string.Join(',', state.Buffer)}], "
                + $"batch [{string.Join(',', actual.BatchLabels)}].");

        // Rule: no starvation. A finished turn goes Ready only when no input waits.
        if (!midTurn)
        {
            var expectedAction = state.Buffer.Count == 0 ? "Ready" : "FollowUp";
            if (actual.Action != expectedAction)
                return ValidationResult.Invalid(
                    $"With {state.Buffer.Count} buffered input(s) the flow must be {expectedAction}. Actual: {actual.Action}.");
            if (state.Buffer.Count > 0 && actual.BatchLabels.Length == 0)
                return ValidationResult.Invalid("A follow-up must carry at least one buffered input.");
        }

        // Rule: closure. Every consumed input leaves pending, and every unconsumed input stays pending in order.
        if (!actual.PendingLabels.SequenceEqual(state.Buffer))
            return ValidationResult.Invalid(
                $"Pending inputs must be exactly the unconsumed ones [{string.Join(',', state.Buffer)}]. "
                + $"Actual [{string.Join(',', actual.PendingLabels)}].");

        var batchRequesters = actual.BatchLabels.Select(label => state.RequesterByLabel[label]).Distinct().ToArray();
        if (rule == Rule.SingleRequesterPerBatch && batchRequesters.Length > 1)
            return ValidationResult.Invalid(
                "Inputs from different requesters must not share one batch. Batch "
                + $"[{string.Join(',', actual.BatchLabels.Select(label => $"{label}={state.RequesterByLabel[label]}"))}].");

        if (rule == Rule.ModelCallUsesBatchAuthority && batchRequesters.Length == 1)
        {
            // Only a batch with one requester can name one authority, so this rule isolates stale authority.
            if (actual.ExecutingRequester != batchRequesters[0])
                return ValidationResult.Invalid(
                    $"The model call for batch [{string.Join(',', actual.BatchLabels)}] serves '{batchRequesters[0]}' "
                    + $"but runs under '{actual.ExecutingRequester ?? "no authority"}'.");
        }

        if (rule == Rule.ModelCallUsesBatchAuthority && midTurn && actual.BatchLabels.Length == 0
            && actual.ExecutingRequester != state.TurnRequester)
            return ValidationResult.Invalid(
                $"A tool boundary without new input keeps the turn authority '{state.TurnRequester}'. "
                + $"Actual: '{actual.ExecutingRequester ?? "no authority"}'.");

        // Rule: a running turn has one authority, fixed when it starts. A tool boundary may add an input only
        // from the requester that owns the turn, and the turn keeps running under that requester.
        if (rule == Rule.RunningTurnKeepsItsAuthority && midTurn)
        {
            if (batchRequesters.Any(requester => requester != state.TurnRequester))
                return ValidationResult.Invalid(
                    $"The turn belongs to '{state.TurnRequester}'. Input from another requester must wait for its own turn. "
                    + $"Joined batch [{string.Join(',', actual.BatchLabels.Select(label => $"{label}={state.RequesterByLabel[label]}"))}].");
            if (actual.ExecutingRequester != state.TurnRequester)
                return ValidationResult.Invalid(
                    $"The turn belongs to '{state.TurnRequester}' and its authority must not change. "
                    + $"The model call runs under '{actual.ExecutingRequester ?? "no authority"}'.");
        }

        return ValidationResult.Valid();
    }

    private static MessageSource OperatorSource(string requester, string? messageId) => new()
    {
        ChannelType = ChannelType.SignalR,
        SenderId = new SenderId(requester),
        ChannelId = "operator-channel",
        MessageId = messageId,
        Audience = TrustAudience.Personal,
        Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(TrustAudience.Personal),
        Principal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
    };

    // Mirrors ReminderExecutionActor: the per-fire key is the message id, the turn id, and the reminder id.
    private static MessageSource ReminderSource(string fireKey) => new()
    {
        ChannelType = ChannelType.SignalR,
        SenderId = new SenderId(Automation),
        ChannelId = "operator-channel",
        MessageId = fireKey,
        TurnId = new TurnId(fireKey),
        ReminderId = new ReminderId(fireKey),
        Audience = TrustAudience.Personal,
        Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(TrustAudience.Personal),
        Principal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
    };

    // This adapter makes the same calls as the session actor. Only the Accordant model predicts its responses.
    private sealed class FlowScenario
    {
        private static readonly SessionId SessionId = new("input-flow/accordant");

        private readonly AccordantSpec _spec;
        private readonly Command[] _sequence;
        private readonly SessionInputFlow _flow = new();
        private readonly Dictionary<InputId, string> _labelById = [];
        private readonly List<string> _reminderFireKeys = [];
        private readonly List<string> _trace = [];
        private readonly HashSet<Explored> _explored = [];
        private SessionState _state = SessionState.Empty;
        private StateProfile _profile = new(new InputFlowModelState());
        private bool _turnOpen;

        public FlowScenario(AccordantSpec spec, Command[] sequence)
        {
            _spec = spec;
            _sequence = sequence;
        }

        public IReadOnlySet<Explored> Run()
        {
            for (var index = 0; index < _sequence.Length; index++)
            {
                var command = _sequence[index];
                _trace.Add($"c{index}:{command}");
                switch (command.Kind)
                {
                    case Kind.AdmitOperator:
                        Admit(new AdmitRequest(
                                $"c{index}",
                                command.Requester,
                                command.SourceMessageId is { } id ? $"{command.Requester}:{id}" : null,
                                null),
                            OperatorSource(command.Requester, command.SourceMessageId));
                        break;
                    case Kind.AdmitReminder:
                        Admit(new AdmitRequest($"c{index}", Automation, $"{Automation}:{command.SourceMessageId}", command.SourceMessageId),
                            ReminderSource(command.SourceMessageId!));
                        break;
                    case Kind.FinishTurn when _turnOpen:
                        FinishTurn();
                        break;
                    case Kind.FailTurn when _turnOpen:
                        FailTurn();
                        break;
                    case Kind.ToolBoundary when _turnOpen:
                        ToolBoundary();
                        break;
                }
            }

            return _explored;
        }

        private void Admit(AdmitRequest request, MessageSource source)
        {
            var model = (InputFlowModelState)_profile.SingleState();
            var command = new SendUserMessage { SessionId = SessionId, Content = request.Label, Source = source };
            var placement = _turnOpen ? InputPlacement.BufferDuringModel : InputPlacement.Activate;
            string outcome;
            string? admittedRequester = null;

            // The actor checks the reminder in-flight and processed sets first, then asks the owner to admit.
            if (_flow.IsReminderDuplicate(source.ReminderId, _state))
            {
                outcome = "Duplicate";
            }
            else
            {
                switch (_flow.PrepareAdmission(command, _state, SessionId, 1))
                {
                    case AdmissionDecision.Duplicate:
                        outcome = "Duplicate";
                        break;
                    case AdmissionDecision.Persist persist:
                        _state = _state.Apply(persist.Event);
                        var accepted = _flow.AdmissionCommitted(persist.Event, command, placement);
                        if (placement == InputPlacement.Activate)
                            _flow.ActivateInput(accepted, _state.PendingInputs, null);
                        _labelById[persist.Event.InputId] = request.Label;
                        admittedRequester = persist.Event.TurnContext.RequesterSenderId?.Value;
                        outcome = _turnOpen ? "Buffered" : "Activated";
                        if (request.ReminderFireKey is { } fire && !_reminderFireKeys.Contains(fire))
                        {
                            if (_reminderFireKeys.Any(known => known.Split(':')[0] == fire.Split(':')[0]))
                                _explored.Add(Explored.ReminderFiresOfOneReminder);
                            _reminderFireKeys.Add(fire);
                        }

                        _turnOpen = true;
                        break;
                    default:
                        throw new InvalidOperationException("Unexpected admission decision.");
                }
            }

            RecordAdmitCoverage(model, request, outcome);
            Verify("Admit", request, new AdmitObservation(outcome, admittedRequester));
        }

        private void RecordAdmitCoverage(InputFlowModelState model, AdmitRequest request, string outcome)
        {
            var isReminder = request.ReminderFireKey is not null;
            if (outcome == "Duplicate")
                _explored.Add(isReminder ? Explored.DuplicateReminder : Explored.DuplicateOperator);
            else if (outcome == "Activated")
                _explored.Add(Explored.Activated);
            else
            {
                _explored.Add(Explored.Buffered);
                if (isReminder)
                    _explored.Add(Explored.ReminderBuffered);
                if (model.Buffer.Concat([request.Label]).Select(label =>
                        label == request.Label ? request.Requester : model.RequesterByLabel[label]).Distinct().Count() > 1)
                    _explored.Add(Explored.MixedRequesterBuffer);
            }
        }

        private void FinishTurn()
        {
            var selected = _flow.SelectedInputs;
            var recorded = new TurnRecorded
            {
                SessionId = SessionId,
                UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "input" },
                AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "reply" },
                SourceReminderId = selected.ReminderId,
                SourceBackgroundJobId = selected.BackgroundJobId,
                ConsumedInputIds = selected.Ids
            };
            _state = _state.Apply(recorded, userAlreadyInHistory: true);
            _flow.TurnCommitted(recorded);
            Advance("FinishTurn", _flow.AdvanceAfterTurn(restartDrainRequested: false), isFailure: false);
        }

        private void FailTurn()
        {
            var selected = _flow.SelectedInputs.Ids;
            if (selected.Count > 0)
            {
                var closed = new InputClosed { SessionId = SessionId, InputIds = selected };
                _state = _state.CloseInputs(closed.InputIds);
                _flow.InputsClosed(closed);
            }

            _flow.FailedTurn();
            Advance("FailTurn", _flow.AdvanceAfterTurn(restartDrainRequested: false), isFailure: true);
        }

        private void Advance(string operation, AfterTurnResult next, bool isFailure)
        {
            var batch = next.Batch?.Messages ?? [];
            _turnOpen = next.Action == InputProgressAction.FollowUp;
            if (_turnOpen)
            {
                _explored.Add(Explored.FollowUpBatch);
                if (isFailure)
                    _explored.Add(Explored.FailTurnFollowUp);
            }

            Verify(operation, Unit.Value, Observe(next.Action == InputProgressAction.Ready ? "Ready" : "FollowUp", batch));
        }

        private void ToolBoundary()
        {
            var selected = _flow.SelectedInputs;
            _state = _state.CloseInputs(selected.Ids);
            _flow.ToolBatchCommitted();
            var advance = _flow.AdvanceAfterToolBatch(new BatchCompletion.Ready(_state, 1), compactionDue: false);
            var batch = advance.Batch?.Messages ?? [];
            if (batch.Count > 0)
                _explored.Add(Explored.ToolBoundaryDrain);
            Verify("ToolBoundary", Unit.Value, Observe("Continue", batch));
        }

        private AdvanceObservation Observe(string action, IReadOnlyList<SendUserMessage> batch)
            => new(
                action,
                batch.Select(message => _labelById[message.AdmittedInputId!.Value]).ToArray(),
                _flow.Authority.Context?.RequesterSenderId?.Value,
                _state.PendingInputs.Select(input => _labelById[input.InputId]).ToArray());

        private void Verify<TRequest, TResponse>(string name, TRequest request, TResponse response)
        {
            var operation = _spec.GetOperation<TRequest, TResponse>(name);
            var (valid, message, updated) = _spec.Allows(operation, request, response, _profile);
            Assert.True(valid, $"Accordant rejected {string.Join(" -> ", _trace)}: {message}");
            _profile = updated;
        }
    }
}
