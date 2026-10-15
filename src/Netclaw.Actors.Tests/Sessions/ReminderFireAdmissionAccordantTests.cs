// -----------------------------------------------------------------------
// <copyright file="ReminderFireAdmissionAccordantTests.cs" company="Petabridge, LLC">
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
using AccordantSpec = Microsoft.Accordant.Spec<Netclaw.Actors.Tests.Sessions.FireAdmissionModelState>;
using ChatRole = Netclaw.Actors.Protocol.ChatRole;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// What an observer needs to predict the admission of a reminder fire. The model holds labels, requester names,
/// reminder names, and the keys that the session admitted. It does not hold a <c>SessionState</c>, a buffer, or a
/// <c>TurnContext</c>, and it never calls the flow. Its state follows the observed outcome of each admission, so
/// each rule below checks one prediction on its own.
/// </summary>
[State]
public partial class FireAdmissionModelState
{
    public bool TurnOpen { get; set; }
    public List<string> Pending { get; set; } = [];
    public List<string> Running { get; set; } = [];
    public List<string> SeenKeys { get; set; } = [];
    public bool Restarted { get; set; }
    public Dictionary<string, string> RequesterByLabel { get; set; } = new();
    public Dictionary<string, string> ReminderByLabel { get; set; } = new();
}

/// <summary>
/// Executable spec for the admission of reminder fires at <see cref="SessionInputFlow.PrepareAdmission"/>, which is
/// the one place that decides after D2. A hand-written adapter drives the real flow with the same calls as the
/// session actor. The Accordant model predicts each outcome. CsCheck chooses the operation order.
/// <para>
/// The seam is the flow, as in <c>SessionInputFlowAccordantTests</c>. The flow owns the stored inputs, the selected
/// inputs, and the stored keys that the decision reads. The restart-resume rule lives in the actor. The example specs
/// in <c>ReminderFireAdmissionTests</c> cover it.
/// </para>
/// <para>
/// A reminder fire is "acknowledged" in both outcomes. "Admitted" stores one input. "NotAdmitted" stores none and
/// the session still acknowledges the fire. The reminder system therefore sees success for every outcome.
/// The spec depends on neither the name of the drop outcome nor its representation.
/// </para>
/// </summary>
public sealed class ReminderFireAdmissionAccordantTests(ITestOutputHelper output)
{
    // CsCheck replays this first case, then explores new random sequences. Failures report their own replay seed.
    private const string InitialSampleSeed = "0N0XIzNsQ0O2";

    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Automation = "reminder-bot";

    private enum Rule { RedeliveryIsADuplicate, FireIsAdmittedUnlessAnEarlierFireWaits, FireIsDroppedWhileAnEarlierFireWaits }

    private enum Kind { Operator, Fire, FinishTurn, ToolBoundary, FailTurn, Restart }

    private enum Prediction { Duplicate, Drop, Admit }

    private enum Explored
    {
        FireDropped, FireAdmittedWhileEarlierFireRuns, FireAdmittedAfterEarlierFireConsumed, RedeliveredFire,
        OtherReminderWaiting, RestartResumedInputs, FireAdmittedAfterRestart, RedeliveryAfterRestart
    }

    private sealed record Command(Kind Kind, string Requester, string? MessageId, string? ReminderId, long FireMs)
    {
        public string FireKey => $"{ReminderId}:{FireMs}";

        public override string ToString() => Kind switch
        {
            Kind.Operator => $"Operator({Requester},msg={MessageId ?? "none"})",
            Kind.Fire => $"Fire({FireKey})",
            _ => Kind.ToString()
        };
    }

    private sealed record AdmitRequest(string Label, string Requester, string? SourceKey, string? ReminderId);
    private sealed record AdmitObservation(string Outcome);
    private sealed record AdvanceObservation(string Action, string[] BatchLabels, string[] PendingLabels);
    private sealed record RestartObservation(string[] RunningLabels);

    [Fact]
    public void Generated_redelivered_inputs_are_never_admitted_twice()
        => RunProperty(Rule.RedeliveryIsADuplicate);

    [Fact]
    public void Generated_fires_are_admitted_unless_an_earlier_fire_of_the_same_reminder_waits()
        => RunProperty(Rule.FireIsAdmittedUnlessAnEarlierFireWaits);

    [Fact]
    public void Generated_fires_are_dropped_while_an_earlier_fire_of_the_same_reminder_waits()
        => RunProperty(Rule.FireIsDroppedWhileAnEarlierFireWaits);

    // The shrunk counterexample of the property above, kept as a named case. A person's turn runs, one fire of
    // "check-pr" waits behind it, and a newer fire of the same reminder arrives.
    [Fact]
    public void A_newer_fire_is_dropped_while_the_earlier_fire_waits_behind_a_running_turn()
        => new FlowScenario(
            CreateSpec(Rule.FireIsDroppedWhileAnEarlierFireWaits),
            [
                new Command(Kind.Operator, Bob, "m0", null, 0),
                new Command(Kind.Fire, Automation, null, "check-pr", 1000),
                new Command(Kind.Fire, Automation, null, "check-pr", 2000)
            ]).Run();

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
            Gen.OneOfConst<string?>(null, "m0"),
            (requester, messageId) => new Command(Kind.Operator, requester, messageId, null, 0));
        // "check" and "check-pr" share a prefix. Three fire times let a fire repeat and let a newer fire arrive.
        var fire = Gen.Select(
            Gen.OneOfConst("check", "check-pr"),
            Gen.OneOfConst(1000L, 2000L, 3000L),
            (reminderId, fireMs) => new Command(Kind.Fire, Automation, null, reminderId, fireMs));
        var terminal = Gen.OneOfConst(Kind.FinishTurn, Kind.ToolBoundary, Kind.FailTurn, Kind.Restart)
            .Select(kind => new Command(kind, string.Empty, null, null, 0));
        return Gen.Frequency(
            (3, admitOperator),
            (6, fire),
            (5, terminal)).Array[1, 14];
    }

    private static AccordantSpec CreateSpec(Rule rule)
    {
        var spec = new AccordantSpec();
        spec.Operation<AdmitRequest, AdmitObservation>("Admit", (request, state) => Admit(request, state, rule));
        spec.Operation<Unit, AdvanceObservation>("FinishTurn", (_, state) => Terminal(state));
        spec.Operation<Unit, AdvanceObservation>("FailTurn", (_, state) => Terminal(state));
        spec.Operation<Unit, AdvanceObservation>("ToolBoundary", (_, state) => ToolBoundary(state));
        spec.Operation<Unit, RestartObservation>("Restart", (_, state) => Restart(state));
        return spec;
    }

    // The prediction. Rule: one admission per source key. Rule: a fire is dropped while an earlier input of the same
    // reminder waits (stored, and not selected by the running turn). Otherwise the input is admitted.
    private static Prediction Predict(AdmitRequest request, FireAdmissionModelState state)
    {
        if (request.SourceKey is { } key && state.SeenKeys.Contains(key))
            return Prediction.Duplicate;

        return request.ReminderId is { } reminderId && WaitingFireOf(state, reminderId)
            ? Prediction.Drop
            : Prediction.Admit;
    }

    private static bool WaitingFireOf(FireAdmissionModelState state, string reminderId)
        => state.Pending.Any(label =>
            !state.Running.Contains(label)
            && state.ReminderByLabel.TryGetValue(label, out var other)
            && other == reminderId);

    private static ExpectedOutcomes Admit(AdmitRequest request, FireAdmissionModelState state, Rule rule)
    {
        var prediction = Predict(request, state);
        return Expect.That<AdmitObservation>(actual =>
                Validate(request, prediction, actual, rule))
            .ThenState<FireAdmissionModelState>((AdmitObservation actual, FireAdmissionModelState next) =>
            {
                // The state follows the real outcome, so a failure of one rule does not hide the others.
                if (actual.Outcome != "Admitted")
                    return;

                if (request.SourceKey is { } sourceKey)
                    next.SeenKeys.Add(sourceKey);
                next.Pending.Add(request.Label);
                next.RequesterByLabel[request.Label] = request.Requester;
                if (request.ReminderId is { } reminderId)
                    next.ReminderByLabel[request.Label] = reminderId;
                if (!state.TurnOpen)
                {
                    next.TurnOpen = true;
                    next.Running = [request.Label];
                }
            }, mock: () => new AdmitObservation("Admitted"));
    }

    private static ValidationResult Validate(
        AdmitRequest request, Prediction prediction, AdmitObservation actual, Rule rule)
    {
        var admitted = actual.Outcome == "Admitted";
        return (rule, prediction) switch
        {
            (Rule.RedeliveryIsADuplicate, Prediction.Duplicate) when admitted => ValidationResult.Invalid(
                $"Source key '{request.SourceKey}' was admitted already, so a second delivery must not add an input."),
            (Rule.FireIsAdmittedUnlessAnEarlierFireWaits, Prediction.Admit) when !admitted => ValidationResult.Invalid(
                $"No earlier input of '{request.ReminderId ?? request.Requester}' waits and the key is new, so {request.Label} must be admitted."),
            (Rule.FireIsDroppedWhileAnEarlierFireWaits, Prediction.Drop) when admitted => ValidationResult.Invalid(
                $"An earlier fire of '{request.ReminderId}' waits, so the newer fire {request.Label} must not become a second input."),
            _ => ValidationResult.Valid()
        };
    }

    // A turn ends (recorded or failed). The flow either goes Ready or drains the waiting inputs into a follow-up batch.
    private static ExpectedOutcomes Terminal(FireAdmissionModelState state)
        => Expect.That<AdvanceObservation>(actual => ValidateAdvance(state, actual, midTurn: false))
            .ThenState<FireAdmissionModelState>((AdvanceObservation actual, FireAdmissionModelState next) =>
            {
                ApplyAdvance(state, actual, next);
                next.TurnOpen = actual.Action == "FollowUp";
            }, mock: () => new AdvanceObservation("Ready", [], []));

    // A tool batch completes inside an open turn. The turn continues and may take waiting inputs of its own requester.
    private static ExpectedOutcomes ToolBoundary(FireAdmissionModelState state)
        => Expect.That<AdvanceObservation>(actual => ValidateAdvance(state, actual, midTurn: true))
            .ThenState<FireAdmissionModelState>((AdvanceObservation actual, FireAdmissionModelState next) =>
                ApplyAdvance(state, actual, next),
                mock: () => new AdvanceObservation("Continue", [], []));

    private static List<string> Waiting(FireAdmissionModelState state)
        => state.Pending.Where(label => !state.Running.Contains(label)).ToList();

    private static void ApplyAdvance(FireAdmissionModelState state, AdvanceObservation actual, FireAdmissionModelState next)
    {
        // The selected inputs are consumed. The batch that the flow drained is now selected and still stored.
        next.Pending = Waiting(state);
        next.Running = [.. actual.BatchLabels];
    }

    private static ValidationResult ValidateAdvance(FireAdmissionModelState state, AdvanceObservation actual, bool midTurn)
    {
        var waiting = Waiting(state);
        if (!waiting.Take(actual.BatchLabels.Length).SequenceEqual(actual.BatchLabels))
            return ValidationResult.Invalid(
                $"The batch must be a prefix of the waiting inputs in admission order. Waiting [{string.Join(',', waiting)}], "
                + $"batch [{string.Join(',', actual.BatchLabels)}].");

        if (!midTurn)
        {
            var expectedAction = waiting.Count == 0 ? "Ready" : "FollowUp";
            if (actual.Action != expectedAction)
                return ValidationResult.Invalid(
                    $"With {waiting.Count} waiting input(s) the flow must be {expectedAction}. Actual: {actual.Action}.");
        }

        if (actual.BatchLabels.Select(label => state.RequesterByLabel[label]).Distinct().Count() > 1)
            return ValidationResult.Invalid("Inputs from different requesters must not share one batch.");

        // Rule: closure. Every consumed input leaves the stored list. Every other input stays in order.
        return actual.PendingLabels.SequenceEqual(waiting)
            ? ValidationResult.Valid()
            : ValidationResult.Invalid(
                $"Stored inputs must be exactly the unconsumed ones [{string.Join(',', waiting)}]. "
                + $"Actual [{string.Join(',', actual.PendingLabels)}].");
    }

    // The daemon restarts and the restart reminder resumes the stored inputs of one requester as the running turn.
    private static ExpectedOutcomes Restart(FireAdmissionModelState state)
        => Expect.That<RestartObservation>(actual =>
                actual.RunningLabels.SequenceEqual(state.Pending)
                    ? ValidationResult.Valid()
                    : ValidationResult.Invalid(
                        $"The resume must select every stored input [{string.Join(',', state.Pending)}]. "
                        + $"Actual [{string.Join(',', actual.RunningLabels)}]."))
            .ThenState<FireAdmissionModelState>((RestartObservation actual, FireAdmissionModelState next) =>
            {
                next.Restarted = true;
                next.Running = [.. state.Pending];
                next.TurnOpen = state.Pending.Count > 0;
            }, mock: () => new RestartObservation([]));

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
        {
            SourceKind = new SourceKind(ReminderExecutionActor.ReminderSourceKind)
        }
    };

    // This adapter makes the same calls as the session actor. Only the Accordant model predicts its responses.
    private sealed class FlowScenario
    {
        private static readonly SessionId SessionId = new("fire-admission/accordant");

        private readonly AccordantSpec _spec;
        private readonly Command[] _sequence;
        private readonly Dictionary<InputId, string> _labelById = [];
        private readonly List<string> _trace = [];
        private readonly HashSet<Explored> _explored = [];
        private SessionInputFlow _flow = new();
        private SessionState _state = SessionState.Empty;
        private StateProfile _profile = new(new FireAdmissionModelState());
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
                    case Kind.Operator:
                        Admit(new AdmitRequest(
                                $"c{index}",
                                command.Requester,
                                command.MessageId is { } id ? $"op:{command.Requester}:{id}" : null,
                                null),
                            OperatorSource(command.Requester, command.MessageId));
                        break;
                    case Kind.Fire:
                        Admit(new AdmitRequest($"c{index}", Automation, $"fire:{command.FireKey}", command.ReminderId),
                            ReminderSource(command.FireKey));
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
                    case Kind.Restart:
                        Restart();
                        break;
                }
            }

            return _explored;
        }

        private void Admit(AdmitRequest request, MessageSource source)
        {
            var model = (FireAdmissionModelState)_profile.SingleState();
            RecordCoverage(model, request);
            var command = new SendUserMessage { SessionId = SessionId, Content = request.Label, Source = source };
            var placement = _turnOpen ? InputPlacement.BufferDuringModel : InputPlacement.Activate;

            // PrepareAdmission is the only decision. A drop and a duplicate both store nothing and acknowledge.
            string outcome;
            switch (_flow.PrepareAdmission(command, _state, SessionId, 1))
            {
                case AdmissionDecision.Persist persist:
                    _state = _state.Apply(persist.Event);
                    var accepted = _flow.AdmissionCommitted(persist.Event, command, placement);
                    if (placement == InputPlacement.Activate)
                        _flow.ActivateInput(accepted, _state.PendingInputs, null);
                    _labelById[persist.Event.InputId] = request.Label;
                    _turnOpen = true;
                    outcome = "Admitted";
                    break;
                case AdmissionDecision.Reject rejected:
                    throw new InvalidOperationException($"The flow rejected a valid input: {rejected.Reason}");
                default:
                    outcome = "NotAdmitted";
                    break;
            }

            Verify("Admit", request, new AdmitObservation(outcome));
        }

        private void RecordCoverage(FireAdmissionModelState model, AdmitRequest request)
        {
            if (request.ReminderId is not { } reminderId)
                return;

            var prediction = Predict(request, model);
            var earlierPending = model.Pending.Any(label => model.ReminderByLabel.GetValueOrDefault(label) == reminderId);
            var earlierSeen = model.SeenKeys.Any(key => key.StartsWith($"fire:{reminderId}:", StringComparison.Ordinal));
            switch (prediction)
            {
                case Prediction.Drop:
                    _explored.Add(Explored.FireDropped);
                    break;
                case Prediction.Duplicate:
                    _explored.Add(Explored.RedeliveredFire);
                    if (model.Restarted)
                        _explored.Add(Explored.RedeliveryAfterRestart);
                    break;
                case Prediction.Admit:
                    if (earlierPending)
                        _explored.Add(Explored.FireAdmittedWhileEarlierFireRuns);
                    else if (earlierSeen)
                        _explored.Add(Explored.FireAdmittedAfterEarlierFireConsumed);
                    if (model.Pending.Any(label =>
                            !model.Running.Contains(label)
                            && model.ReminderByLabel.TryGetValue(label, out var other)
                            && other != reminderId))
                        _explored.Add(Explored.OtherReminderWaiting);
                    if (model.Restarted)
                        _explored.Add(Explored.FireAdmittedAfterRestart);
                    break;
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
            Advance("FinishTurn", _flow.AdvanceAfterTurn(restartDrainRequested: false));
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
            Advance("FailTurn", _flow.AdvanceAfterTurn(restartDrainRequested: false));
        }

        private void Advance(string operation, AfterTurnResult next)
        {
            _turnOpen = next.Action == InputProgressAction.FollowUp;
            Verify(operation, Unit.Value, Observe(
                next.Action == InputProgressAction.Ready ? "Ready" : "FollowUp",
                next.Batch?.Messages ?? []));
        }

        private void ToolBoundary()
        {
            var selected = _flow.SelectedInputs;
            _state = _state.CloseInputs(selected.Ids);
            _flow.ToolBatchCommitted();
            var advance = _flow.AdvanceAfterToolBatch(new BatchCompletion.Ready(_state, 1, ToolBudgetStatus.Ok.Instance), compactionDue: false);
            Verify("ToolBoundary", Unit.Value, Observe("Continue", advance.Batch?.Messages ?? []));
        }

        // The daemon restarts. The new flow has no memory. The restart reminder resumes the stored inputs of one
        // requester, as TryResumeInterruptedInput does. Stored inputs of several requesters do not resume (a
        // separate change), so the scenario skips that restart and keeps the old flow.
        private void Restart()
        {
            var model = (FireAdmissionModelState)_profile.SingleState();
            if (model.Pending.Select(label => model.RequesterByLabel[label]).Distinct().Count() > 1)
                return;

            var pending = _state.PendingInputs.ToArray();
            _flow = new SessionInputFlow();
            if (pending.Length > 0)
            {
                _explored.Add(Explored.RestartResumedInputs);
                Assert.True(
                    TurnContext.TryFromRecord(pending[0].TurnContext, out var context, out var reason) && context is not null,
                    reason);
                _flow.ActivateRecoveredInput(null, context, null);
                _flow.ResumePendingInputs(pending);
            }

            _turnOpen = pending.Length > 0;
            Verify("Restart", Unit.Value, new RestartObservation(
                _flow.SelectedInputs.Ids.Select(id => _labelById[id]).ToArray()));
        }

        private AdvanceObservation Observe(string action, IReadOnlyList<SendUserMessage> batch)
            => new(
                action,
                batch.Select(message => _labelById[message.AdmittedInputId!.Value]).ToArray(),
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
