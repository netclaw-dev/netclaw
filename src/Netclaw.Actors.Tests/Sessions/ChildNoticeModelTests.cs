// -----------------------------------------------------------------------
// <copyright file="ChildNoticeModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using CsCheck;
using Microsoft.Accordant;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Xunit;
using AccordantSpec = Microsoft.Accordant.Spec<Netclaw.Actors.Tests.Sessions.NoticeModelState>;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// What an observer needs to predict the notices of a session: which children started for which requester, which of
/// them ended, which user inputs the session acknowledged and in what order, and whether a turn or an approval is open.
/// The model holds labels and requester names only. It does not mirror any journal event, state record, or flow list.
/// </summary>
[State]
public partial class NoticeModelState
{
    public List<string> Runs { get; set; } = [];
    public Dictionary<string, string> Owner { get; set; } = new();
    public List<string> Live { get; set; } = [];
    public List<string> Ended { get; set; } = [];
    public List<string> UserInputs { get; set; } = [];
    public bool TurnOpen { get; set; }
    public bool ApprovalOpen { get; set; }
    public bool ApprovalParked { get; set; }
    public bool ApprovalBuffered { get; set; }
    public bool SessionAlive { get; set; }
}

/// <summary>
/// Executable model spec for S2: each terminal child run yields one notice, and the notice joins the flow of inputs
/// without a second scheduler. A generated sequence starts children for two requesters, ends them with success,
/// failure, or loss, sends user input, opens and closes a turn, opens and closes an approval, passivates the session,
/// and restarts the daemon. A hand-written adapter drives the real session actor through its journal and its
/// observable model requests. The Accordant model above predicts what the journal may hold after each step and what it
/// must hold when the session has settled. CsCheck chooses the order of operations.
/// <para>
/// Sources of intent, by property:
/// </para>
/// <list type="bullet">
/// <item>One notice per run: plan rule 2 and <c>s2/DESIGN.md</c> section 2 ("one admitted notice per child run").</item>
/// <item>Admission order: S2 decision 3 (order by admission time) and PRD-001 FR-017 (ordered batches).</item>
/// <item>No mixed requesters: plan rule 1 (a turn has one authority; a notice joins a running turn only for the same requester).</item>
/// <item>No abandonment: tool-authorization TA-11 (the wait ends only with an answer, a cancellation, or a new user message).</item>
/// <item>No lost result: S2-F5, S2-F7 and S8 (a result survives a restart and a passivation).</item>
/// </list>
/// The restart operation is the daemon restart drain with its restart reminder. A crash with an interrupted turn is a
/// separate failure mode of the journal and has its own example specs.
/// </summary>
public sealed class ChildNoticeModelTests(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    // CsCheck replays this first case, then explores new random sequences. Failures report their own replay seed.
    private const string InitialSampleSeed = "0N0XIzNsQ0O2";
    private const int CasesPerProperty = 20;
    private const int MaxChildren = 3;

    // Every wait here ends on an in-process effect. A failed case reports as soon as the wait ends, and a later case
    // in the same process (the shrink run of CsCheck) waits less after a first failure.
    private static readonly TimeSpan EffectWait = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ShortEffectWait = TimeSpan.FromSeconds(1);
    private static volatile bool _failureSeen;

    protected override bool UseTestScheduler => true;

    private enum Rule { OneNoticePerRun, AdmissionOrder, NoMixedRequesters, NoAbandonment, NoLostResult }

    private enum Kind { Spawn, EndChild, UserInput, TurnStart, TurnEnd, ApprovalOpen, ApprovalClose, Passivate, Restart }

    private enum Ending { Success, Failure, Lost }

    private enum Explored
    {
        TwoRequestersWithChildren, EndWhileTurnOpen, MixedRequesterWaiting, FailureEnd, LostEnd,
        RestartWithLiveChild, RestartWithOpenApproval, EndWhileApprovalOpen, PassivateIdle
    }

    private sealed record Op(Kind Kind, string Requester, int Pick, Ending Ending)
    {
        public override string ToString() => Kind switch
        {
            Kind.Spawn or Kind.UserInput or Kind.TurnStart or Kind.ApprovalOpen => $"{Kind}({Requester})",
            Kind.EndChild => $"EndChild(#{Pick},{Ending})",
            _ => Kind.ToString()
        };
    }

    private sealed record Admission(int Index, string Label, string Requester, bool IsNotice, bool Real);
    private sealed record Consumption(int Index, IReadOnlyList<string> Labels, IReadOnlyList<string> Requesters);
    private sealed record RunFacts(string Label, string Owner, IReadOnlyList<Admission> Notices, int NoticeConsumptions, bool Delivered);
    private sealed record Facts(
        IReadOnlyList<Admission> Admissions,
        IReadOnlyList<Consumption> Consumptions,
        IReadOnlyList<string> AbandonedBy,
        IReadOnlyList<RunFacts> Runs,
        IReadOnlyList<string> RefusedAnswers);


    [Fact]
    public void Generated_terminal_runs_each_yield_exactly_one_notice()
        => RunProperty(Rule.OneNoticePerRun);

    [Fact]
    public void Generated_inputs_and_notices_keep_admission_order()
        => RunProperty(Rule.AdmissionOrder);

    [Fact]
    public void Generated_turns_never_mix_requesters()
        => RunProperty(Rule.NoMixedRequesters);

    [Fact]
    public void Generated_notices_never_abandon_an_open_approval()
        => RunProperty(Rule.NoAbandonment);

    [Fact]
    public void Generated_results_survive_restart_and_passivation()
        => RunProperty(Rule.NoLostResult);

    /// <summary>
    /// Negative control for the oracle. The checks of the model must accept a journal that follows every rule, and each
    /// planted defect must trip the rule that names it. The facts here are hand-written. No production code runs.
    /// </summary>
    [Fact]
    public void The_oracle_accepts_an_ideal_journal_and_rejects_each_planted_defect()
    {
        // Arrange: Alice's child c0 and Bob's child c1 ended. Each has one notice that ran once, in admission order.
        var model = new NoticeModelState
        {
            Runs = ["c0", "c1"],
            Owner = new Dictionary<string, string> { ["c0"] = Alice, ["c1"] = Bob },
            Ended = ["c0", "c1"],
            UserInputs = ["spawn:child-a:c0", "spawn:child-a:c1", "msg-2"],
            SessionAlive = true
        };
        var admissions = new[]
        {
            new Admission(0, "spawn:child-a:c0", Alice, false, true), new Admission(1, "spawn:child-a:c1", Bob, false, true),
            new Admission(2, "msg-2", Alice, false, true), new Admission(3, "notice:c0", Alice, true, true),
            new Admission(4, "notice:c1", Bob, true, true)
        };
        var consumptions = new[]
        {
            new Consumption(0, ["spawn:child-a:c0"], [Alice]), new Consumption(1, ["spawn:child-a:c1"], [Bob]),
            new Consumption(2, ["msg-2", "notice:c0"], [Alice, Alice]), new Consumption(3, ["notice:c1"], [Bob])
        };
        var ideal = new Facts(admissions, consumptions, ["user"],
            [
                new RunFacts("c0", Alice, [admissions[3]], 1, true), new RunFacts("c1", Bob, [admissions[4]], 1, true)
            ], []);

        // Assert: the ideal journal passes every rule.
        foreach (var rule in Enum.GetValues<Rule>())
            Assert.True(Check(rule, model, ideal, settle: true) is { IsValid: true }, $"{rule} rejected the ideal journal.");

        // Assert: each planted defect trips its rule.
        var twoNotices = ideal with
        {
            Runs = [ideal.Runs[0] with { Notices = [admissions[3], admissions[3] with { Index = 9 }] }, ideal.Runs[1]]
        };
        Assert.False(Check(Rule.OneNoticePerRun, model, twoNotices, settle: true).IsValid, "A second notice must trip the rule.");
        var noNotice = ideal with { Runs = [ideal.Runs[0] with { Notices = [] }, ideal.Runs[1]] };
        Assert.False(Check(Rule.OneNoticePerRun, model, noNotice, settle: true).IsValid, "A missing notice must trip the rule.");
        var wrongOwner = ideal with { Runs = [ideal.Runs[0] with { Notices = [admissions[3] with { Requester = Bob }] }, ideal.Runs[1]] };
        Assert.False(Check(Rule.OneNoticePerRun, model, wrongOwner, settle: true).IsValid, "A notice for the wrong requester must trip the rule.");
        var reordered = ideal with { Consumptions = [consumptions[0], consumptions[3], consumptions[1], consumptions[2]] };
        Assert.False(Check(Rule.AdmissionOrder, model, reordered, settle: true).IsValid, "A consumption out of admission order must trip the rule.");
        var mixed = ideal with { Consumptions = [consumptions[0], consumptions[1], new Consumption(2, ["msg-2", "notice:c1"], [Alice, Bob]), consumptions[3]] };
        Assert.False(Check(Rule.NoMixedRequesters, model, mixed, settle: true).IsValid, "A turn with two requesters must trip the rule.");
        var refused = ideal with { RefusedAnswers = ["probe-p1-b by bob: approval_wrong_requester"] };
        Assert.False(Check(Rule.NoMixedRequesters, model, refused, settle: true).IsValid, "A refused answer must trip the rule.");
        var abandoned = ideal with { AbandonedBy = ["notice"] };
        Assert.False(Check(Rule.NoAbandonment, model, abandoned, settle: true).IsValid, "An abandonment by a notice must trip the rule.");
        var lost = ideal with { Runs = [ideal.Runs[0] with { NoticeConsumptions = 0 }, ideal.Runs[1]] };
        Assert.False(Check(Rule.NoLostResult, model, lost, settle: true).IsValid, "A notice that never ran must trip the rule.");
        var twice = ideal with { Runs = [ideal.Runs[0] with { NoticeConsumptions = 2 }, ideal.Runs[1]] };
        Assert.False(Check(Rule.NoLostResult, model, twice, settle: false).IsValid, "A notice that ran twice must trip the rule.");
    }

    private void RunProperty(Rule rule)
    {
        // Arrange
        ScriptParent();
        var journal = JournalAsync().GetAwaiter().GetResult();
        var spec = CreateSpec(rule);
        var cases = 0;
        var runs = 0;
        var operations = 0;
        var explored = new HashSet<Explored>();
        var token = Cancellation;

        // Act: CsCheck chooses the operations. Accordant checks the journal facts after each one.
        GenerateSequences().Sample(sequence =>
        {
            var scenario = new Scenario(this, rule, spec, journal, new SessionId($"notice/model-{rule}-{runs++}"), sequence, token);
            try
            {
                explored.UnionWith(scenario.RunAsync().GetAwaiter().GetResult());
            }
            catch
            {
                _failureSeen = true;
                throw;
            }

            cases++;
            operations += sequence.Length;
        }, iter: rule == Rule.NoMixedRequesters ? 60 : CasesPerProperty, seed: InitialSampleSeed, threads: 1, print: Describe);

        // Assert: these counts check exploration. The model rules below check behavior.
        Note($"rule={rule}; first-case-seed={InitialSampleSeed}; cases={cases}; operations={operations}; explored={string.Join(',', explored)}");
        Assert.True(cases >= CasesPerProperty, $"CsCheck ran only {cases} cases.");
        Assert.True(operations > cases, $"Only {operations} operations ran in {cases} cases.");
        Assert.True(explored.Count >= 7, $"The sequences explored only [{string.Join(", ", explored)}].");
    }

    private static string Describe(Op[] sequence)
        => string.Join(" -> ", sequence.Select((op, index) => $"o{index}:{op}"));

    // A sequence is a short list of chunks. A chunk is one random operation, or a fragment of an interaction that a pure random
    // order reaches too rarely: a child that ends during the approval or the turn of another requester, and a restart
    // that meets a live child.
    private static Gen<Op[]> GenerateSequences()
    {
        var requester = Gen.OneOfConst(Alice, Bob);
        var spawn = requester.Select(who => new Op(Kind.Spawn, who, 0, Ending.Success));
        var end = Gen.Select(Gen.Int[0, 2], Gen.OneOfConst(Ending.Success, Ending.Success, Ending.Failure, Ending.Lost),
            (pick, ending) => new Op(Kind.EndChild, string.Empty, pick, ending));
        var input = requester.Select(who => new Op(Kind.UserInput, who, 0, Ending.Success));
        var turnStart = requester.Select(who => new Op(Kind.TurnStart, who, 0, Ending.Success));
        var approvalOpen = requester.Select(who => new Op(Kind.ApprovalOpen, who, 0, Ending.Success));
        var plain = Gen.OneOfConst(Kind.TurnEnd, Kind.ApprovalClose, Kind.Passivate, Kind.Restart)
            .Select(kind => new Op(kind, string.Empty, 0, Ending.Success));
        var single = Gen.Frequency(
            (4, spawn), (5, end), (3, input), (3, turnStart), (3, plain), (3, approvalOpen))
            .Select(op => new[] { op });

        var endDuringApproval = Gen.Select(requester, requester, Gen.OneOfConst(Ending.Success, Ending.Failure, Ending.Lost),
            (owner, other, ending) => new[]
            {
                new Op(Kind.Spawn, owner, 0, Ending.Success), new Op(Kind.ApprovalOpen, other, 0, Ending.Success),
                new Op(Kind.EndChild, string.Empty, 0, ending), new Op(Kind.ApprovalClose, string.Empty, 0, Ending.Success)
            });
        var endDuringTurn = Gen.Select(requester, requester, Gen.OneOfConst(Ending.Success, Ending.Failure, Ending.Lost),
            (owner, other, ending) => new[]
            {
                new Op(Kind.Spawn, owner, 0, Ending.Success), new Op(Kind.TurnStart, other, 0, Ending.Success),
                new Op(Kind.EndChild, string.Empty, 0, ending), new Op(Kind.TurnEnd, string.Empty, 0, Ending.Success)
            });
        var inputBeforeResult = Gen.Select(requester, requester, requester,
            (owner, turn, other) => new[]
            {
                new Op(Kind.Spawn, owner, 0, Ending.Success), new Op(Kind.TurnStart, turn, 0, Ending.Success),
                new Op(Kind.UserInput, other, 0, Ending.Success), new Op(Kind.EndChild, string.Empty, 0, Ending.Success),
                new Op(Kind.TurnEnd, string.Empty, 0, Ending.Success)
            });
        var restartWithApproval = Gen.Select(requester, requester, (owner, other) => new[]
        {
            new Op(Kind.Spawn, owner, 0, Ending.Success), new Op(Kind.ApprovalOpen, other, 0, Ending.Success),
            new Op(Kind.Restart, string.Empty, 0, Ending.Success), new Op(Kind.ApprovalClose, string.Empty, 0, Ending.Success)
        });
        var restartWithChild = requester.Select(owner => new[]
        {
            new Op(Kind.Spawn, owner, 0, Ending.Success), new Op(Kind.Restart, string.Empty, 0, Ending.Success)
        });
        var passivateAfterEnd = Gen.Select(requester, Gen.OneOfConst(Ending.Success, Ending.Failure, Ending.Lost),
            (owner, ending) => new[]
            {
                new Op(Kind.Spawn, owner, 0, Ending.Success), new Op(Kind.EndChild, string.Empty, 0, ending),
                new Op(Kind.Passivate, string.Empty, 0, Ending.Success)
            });
        var chunk = Gen.Frequency((8, single), (2, endDuringApproval), (2, endDuringTurn), (2, inputBeforeResult), (2, restartWithApproval), (1, restartWithChild), (1, passivateAfterEnd));
        return chunk.Array[1, 5].Select(chunks => chunks.SelectMany(item => item).ToArray());
    }

    // The model predicts from labels and requester names. The checks read only facts that the adapter took from the journal.
    private static AccordantSpec CreateSpec(Rule rule)
    {
        var spec = new AccordantSpec();
        foreach (var name in Enum.GetNames<Kind>())
            spec.Operation<Op, Facts>(name, (op, state) => Predict(rule, op, state, settle: false));
        spec.Operation<Op, Facts>("Settle", (op, state) => Predict(rule, op, state, settle: true));
        return spec;
    }

    // The facts come after the operation, so the checks compare them with the state that the operation predicts.
    private static ExpectedOutcomes Predict(Rule rule, Op op, NoticeModelState state, bool settle)
        => Expect.That<Facts>(facts => Check(rule, Predicted(rule, op, state, settle), facts, settle))
            .ThenState<NoticeModelState>((Facts _, NoticeModelState next) => Transition(rule, op, state, next, settle),
                mock: () => new Facts([], [], [], [], []));

    private static NoticeModelState Predicted(Rule rule, Op op, NoticeModelState before, bool settle)
    {
        var next = new NoticeModelState
        {
            Runs = [.. before.Runs],
            Owner = new Dictionary<string, string>(before.Owner),
            Live = [.. before.Live],
            Ended = [.. before.Ended],
            UserInputs = [.. before.UserInputs],
            TurnOpen = before.TurnOpen,
            ApprovalOpen = before.ApprovalOpen,
            ApprovalParked = before.ApprovalParked,
            ApprovalBuffered = before.ApprovalBuffered,
            SessionAlive = before.SessionAlive
        };
        Transition(rule, op, before, next, settle);
        return next;
    }

    // The model transition for one operation. It uses only the operation, never the facts.
    private static void Transition(Rule rule, Op op, NoticeModelState before, NoticeModelState next, bool settle)
    {
        if (settle)
        {
            next.TurnOpen = false;
            next.ApprovalOpen = false;
            next.ApprovalParked = false;
            next.ApprovalBuffered = false;
            next.SessionAlive = true;
            next.Ended = [.. before.Runs];
            next.Live = [];
            return;
        }

        switch (op.Kind)
        {
            case Kind.Spawn:
                var label = $"c{before.Runs.Count}";
                next.Runs.Add(label);
                next.Owner[label] = op.Requester;
                next.Live.Add(label);
                next.UserInputs.Add($"spawn:child-a:{label}");
                next.SessionAlive = true;
                break;
            case Kind.EndChild:
                var ended = before.Live[op.Pick % before.Live.Count];
                next.Live.Remove(ended);
                next.Ended.Add(ended);
                next.ApprovalBuffered |= before.ApprovalOpen && !before.ApprovalParked;
                break;
            case Kind.UserInput:
            case Kind.ApprovalOpen:
                next.UserInputs.Add(UserLabel(rule, op, before.UserInputs.Count));
                next.SessionAlive = true;
                next.ApprovalOpen |= op.Kind == Kind.ApprovalOpen;
                next.ApprovalBuffered |= op.Kind == Kind.UserInput && before.ApprovalOpen && !before.ApprovalParked;
                break;
            case Kind.TurnStart:
                next.UserInputs.Add(UserLabel(rule, op, before.UserInputs.Count));
                next.SessionAlive = true;
                next.TurnOpen = true;
                break;
            case Kind.TurnEnd:
                next.TurnOpen = false;
                break;
            case Kind.ApprovalClose:
                next.ApprovalOpen = false;
                next.ApprovalParked = false;
                next.ApprovalBuffered = false;
                break;
            case Kind.Passivate:
                next.SessionAlive = false;
                break;
            case Kind.Restart:
                next.Ended.AddRange(before.Live);
                next.Live = [];
                next.TurnOpen = false;
                next.ApprovalParked = before.ApprovalOpen;
                next.ApprovalBuffered = false;
                next.SessionAlive = false;
                break;
        }
    }

    // The turn of an approval asks once. For the property about requesters it asks twice, so that a second tool boundary
    // exists in the turn. At that boundary a child result must not change the authority of the turn.
    private static string UserLabel(Rule rule, Op op, int index) => op.Kind switch
    {
        Kind.ApprovalOpen => rule == Rule.NoMixedRequesters ? $"probe2:p{index}" : $"probe:p{index}",
        _ => $"msg-{index}"
    };

    private static ValidationResult Check(Rule rule, NoticeModelState state, Facts facts, bool settle)
    {
        var noticeRuns = facts.Runs.Where(run => run.Notices.Count > 0).ToArray();
        switch (rule)
        {
            case Rule.OneNoticePerRun:
                foreach (var run in facts.Runs)
                {
                    if (!state.Runs.Contains(run.Label))
                        return ValidationResult.Invalid($"The journal holds run {run.Label}, which the model never started.");
                    if (run.Notices.Count > 1)
                        return ValidationResult.Invalid($"Run {run.Label} has {run.Notices.Count} notices. A run gets one.");
                    if (run.Notices.Count == 1 && !state.Ended.Contains(run.Label) && !state.Live.Contains(run.Label))
                        return ValidationResult.Invalid($"Run {run.Label} has a notice but the model never ended it.");
                    if (run.Notices.Count == 1 && state.Live.Contains(run.Label))
                        return ValidationResult.Invalid($"Run {run.Label} has a notice while it still runs.");
                    if (run.Notices.Count == 1 && run.Notices[0].Requester != state.Owner[run.Label])
                        return ValidationResult.Invalid(
                            $"The notice of run {run.Label} names requester '{run.Notices[0].Requester}', but the owner is '{state.Owner[run.Label]}'.");
                }

                if (settle)
                {
                    var missing = state.Runs.Where(label => noticeRuns.All(run => run.Label != label)).ToArray();
                    if (missing.Length > 0)
                        return ValidationResult.Invalid(
                            $"After the settle, every terminal run must have one notice. Runs without a notice: [{string.Join(", ", missing)}].");
                }

                return ValidationResult.Valid();

            case Rule.AdmissionOrder:
            {
                // The session acknowledged each user input before the next operation, so the journal admits them in send order.
                var admitted = facts.Admissions.Where(item => !item.IsNotice).Select(item => item.Label).ToArray();
                if (!admitted.SequenceEqual(state.UserInputs))
                    return ValidationResult.Invalid(
                        $"User inputs must be admitted in send order [{string.Join(", ", state.UserInputs)}]. Journal: [{string.Join(", ", admitted)}].");

                // A turn consumes inputs in admission order, so the consumption order follows the admission order.
                var order = facts.Admissions.ToDictionary(item => item.Label, item => item.Index);
                var consumed = facts.Consumptions.SelectMany(item => item.Labels).Where(order.ContainsKey).ToArray();
                for (var index = 1; index < consumed.Length; index++)
                {
                    if (order[consumed[index]] < order[consumed[index - 1]])
                        return ValidationResult.Invalid(
                            $"Input {consumed[index]} was admitted before {consumed[index - 1]} but ran after it. Consumption order: [{string.Join(", ", consumed)}].");
                }

                return ValidationResult.Valid();
            }

            case Rule.NoMixedRequesters:
                // The requester of a turn answers every approval of that turn. A refusal means that the turn
                // lost its authority to another requester.
                if (facts.RefusedAnswers.Count > 0)
                    return ValidationResult.Invalid(
                        $"The owner of an approval could not answer it: [{string.Join("; ", facts.RefusedAnswers)}]. The authority of the turn changed.");
                foreach (var consumption in facts.Consumptions)
                {
                    if (consumption.Requesters.Distinct().Count() > 1)
                        return ValidationResult.Invalid(
                            $"One turn consumed inputs of different requesters: {string.Join(", ", consumption.Labels.Zip(consumption.Requesters, (label, who) => $"{label}={who}"))}.");
                }

                foreach (var run in noticeRuns)
                {
                    if (run.Notices[0].Requester != state.Owner[run.Label])
                        return ValidationResult.Invalid(
                            $"The notice of run {run.Label} runs under '{run.Notices[0].Requester}', but the owner is '{state.Owner[run.Label]}'.");
                }

                return ValidationResult.Valid();

            case Rule.NoAbandonment:
                // Only a new user message may end a parked wait. A notice, a child result, or a restart never does.
                for (var index = 0; index < facts.AbandonedBy.Count; index++)
                {
                    if (facts.AbandonedBy[index] != "user")
                        return ValidationResult.Invalid(
                            $"Abandonment #{index} was followed by '{facts.AbandonedBy[index]}' and not by a user input. Only a user message may abandon an open approval.");
                }

                return ValidationResult.Valid();

            default:
                foreach (var run in facts.Runs)
                {
                    if (run.NoticeConsumptions > 1)
                        return ValidationResult.Invalid($"The notice of run {run.Label} ran {run.NoticeConsumptions} times.");
                }

                if (settle)
                {
                    foreach (var label in state.Runs)
                    {
                        var run = facts.Runs.FirstOrDefault(item => item.Label == label);
                        if (run is null || run.Notices.Count != 1)
                            return ValidationResult.Invalid($"The result of run {label} has no notice after the settle.");
                        if (run.NoticeConsumptions != 1)
                            return ValidationResult.Invalid($"The notice of run {label} ran {run.NoticeConsumptions} times. It must run once.");
                        if (!run.Delivered)
                            return ValidationResult.Invalid($"No model request carried the result of run {label}.");
                    }
                }

                return ValidationResult.Valid();
        }
    }

    // The adapter makes the same calls as a client and a daemon. Only the Accordant model predicts the facts.
    private sealed class Scenario
    {
        private readonly ChildNoticeModelTests _test;
        private readonly Rule _rule;
        private readonly AccordantSpec _spec;
        private readonly AcceptanceFault _journal;
        private readonly SessionId _sessionId;
        private readonly Op[] _sequence;
        private readonly CancellationToken _token;
        private readonly List<string> _trace = [];
        private readonly List<(string Label, string Owner, ChildRunAccepted Accepted, TaskCompletionSource Gate)> _children = [];
        private readonly HashSet<Explored> _explored = [];
        private StateProfile _profile = new(new NoticeModelState());
        private Akka.TestKit.TestProbe? _subscriber;
        private IActorRef? _subscribedTo;
        private TaskCompletionSource? _turnGate;
        private string _approvalCall = string.Empty;
        private string _approvalRequester = string.Empty;
        private string _lastInputRequester = string.Empty;
        private readonly List<string> _refusedAnswers = [];

        public Scenario(ChildNoticeModelTests test, Rule rule, AccordantSpec spec, AcceptanceFault journal, SessionId sessionId, Op[] sequence, CancellationToken token)
        {
            _test = test;
            _rule = rule;
            _spec = spec;
            _journal = journal;
            _sessionId = sessionId;
            _sequence = sequence;
            _token = token;
        }

        private NoticeModelState Model => (NoticeModelState)_profile.SingleState();

        private TimeSpan Wait => _failureSeen ? ShortEffectWait : EffectWait;

        public async Task<IReadOnlySet<Explored>> RunAsync()
        {
            _test.Clients.A.PlannedResponses.Clear();
            _test.Clients.A.NextResponseGate = null;
            _test.Clients.Parent.NextResponseGate = null;
            try
            {
                for (var index = 0; index < _sequence.Length; index++)
                {
                    var op = _sequence[index];
                    _trace.Add($"o{index}:{op}");
                    await ApplyAsync(op);
                }

                await SettleAsync();
            }
            finally
            {
                ReleaseEverything();
            }

            return _explored;
        }

        private async Task ApplyAsync(Op op)
        {
            var model = Model;
            switch (op.Kind)
            {
                case Kind.Spawn when !model.TurnOpen && !model.ApprovalOpen && model.Runs.Count < MaxChildren:
                    await SpawnAsync(op);
                    break;
                case Kind.EndChild when model.Live.Count > 0:
                    await EndChildAsync(op);
                    break;
                case Kind.UserInput when !model.ApprovalParked:
                    await SendInputAsync(op);
                    break;
                case Kind.TurnStart when !model.TurnOpen && !model.ApprovalOpen:
                    await TurnStartAsync(op);
                    break;
                case Kind.TurnEnd when model.TurnOpen:
                    _turnGate?.TrySetResult();
                    _turnGate = null;
                    Verify(op);
                    break;
                case Kind.ApprovalOpen when !model.TurnOpen && !model.ApprovalOpen:
                    await ApprovalOpenAsync(op);
                    break;
                case Kind.ApprovalClose when model.ApprovalOpen:
                    await ApprovalCloseAsync(op);
                    break;
                case Kind.Passivate when !model.TurnOpen && !model.ApprovalOpen && model.Live.Count == 0 && model.SessionAlive:
                    await PassivateAsync(op);
                    break;
                // The drain of a live approval wait needs an empty buffer. With input behind the wait it waits for a person.
                case Kind.Restart when model.SessionAlive && !(model.ApprovalOpen && !model.ApprovalParked && model.ApprovalBuffered):
                    await RestartAsync(op);
                    break;
            }
        }

        private async Task SpawnAsync(Op op)
        {
            var label = $"c{Model.Runs.Count}";
            var (accepted, gate) = await _test.StartChildAsync(_sessionId, op.Requester, label);
            _children.Add((label, op.Requester, accepted, gate));
            if (Model.Owner.Values.Any(owner => owner != op.Requester))
                _explored.Add(Explored.TwoRequestersWithChildren);
            Verify(op);
        }

        private async Task EndChildAsync(Op op)
        {
            var model = Model;
            var label = model.Live[op.Pick % model.Live.Count];
            var child = _children.Single(item => item.Label == label);
            if (model.TurnOpen)
            {
                _explored.Add(Explored.EndWhileTurnOpen);
                if (model.UserInputs.Count > 0 && model.Owner[label] != LastInputRequester())
                    _explored.Add(Explored.MixedRequesterWaiting);
            }

            if (model.ApprovalOpen)
                _explored.Add(Explored.EndWhileApprovalOpen);
            switch (op.Ending)
            {
                case Ending.Success:
                    child.Gate.TrySetResult();
                    break;
                case Ending.Failure:
                    _explored.Add(Explored.FailureEnd);
                    var session = await _test.ResolveSessionAsync(_sessionId);
                    await session.Ask<string>(new ChildRunControl(
                        child.Accepted.Run.RunId, true, child.Accepted.Run.OriginalContext), TimeSpan.FromHours(1), _token)
                        .WaitAsync(Wait, _token);
                    break;
                default:
                    _explored.Add(Explored.LostEnd);
                    var actorPath = $"/user/session-manager/{Uri.EscapeDataString(_sessionId.Value)}/subagent-child-a-{child.Accepted.Run.RunId.Value}";
                    var actor = await _test.Sys.ActorSelection(actorPath).ResolveOne(Wait, _token);
                    _test.Sys.Stop(actor);
                    break;
            }

            // The wait ends when the result is ready in the form of the day: ResultPrepared before S2, the notice after S2.
            // The window between the terminal record and the notice has its own example specs.
            await _test.AwaitAssertAsync(() => Assert.True(IsResultReady(_journal, child.Accepted.Run.RunId),
                $"The result of {label} did not become ready."), Wait, cancellationToken: _token);
            Verify(op);
        }

        private async Task SendInputAsync(Op op)
        {
            var index = Model.UserInputs.Count;
            await _test.SendAsync(_sessionId, UserLabel(_rule, op, index), Operator(op.Requester));
            Verify(op);
        }

        private async Task TurnStartAsync(Op op)
        {
            var index = Model.UserInputs.Count;
            _lastInputRequester = op.Requester;
            _turnGate = NewGate();
            _test.Clients.Parent.NextResponseGate = _turnGate;
            await _test.SendAsync(_sessionId, UserLabel(_rule, op, index), Operator(op.Requester));
            // The gate is consumed when a model call of the session enters it. From then on the turn stays open.
            await _test.AwaitAssertAsync(() => Assert.Null(_test.Clients.Parent.NextResponseGate), Wait, cancellationToken: _token);
            Verify(op);
        }

        private async Task ApprovalOpenAsync(Op op)
        {
            var index = Model.UserInputs.Count;
            await SubscribeAsync();
            _approvalCall = $"probe-p{index}";
            _approvalRequester = op.Requester;
            await _test.SendAsync(_sessionId, UserLabel(_rule, op, index), Operator(op.Requester));
            await _test.FishApprovalAsync(_subscriber!, _approvalCall).AsTask().WaitAsync(Wait, _token);
            Verify(op);
        }

        // The turn of the approval asks twice. The requester answers both prompts. A refused answer is a fact for the check.
        private async Task ApprovalCloseAsync(Op op)
        {
            await SubscribeAsync();
            var last = _approvalCall;
            var calls = _rule == Rule.NoMixedRequesters ? new[] { _approvalCall, $"{_approvalCall}-b" } : [_approvalCall];
            foreach (var call in calls)
            {
                if (call != _approvalCall)
                    await _test.FishApprovalAsync(_subscriber!, call).AsTask().WaitAsync(Wait, _token);
                var response = await _test.AskManagerAsync<ISessionResponse>(new ToolInteractionResponse
                {
                    SessionId = _sessionId, CallId = new Netclaw.Tools.ToolCallId(call),
                    SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
                    SenderId = new SenderId(_approvalRequester)
                });
                if (response is CommandNack nack)
                {
                    _refusedAnswers.Add($"{call} by {_approvalRequester}: {nack.Reason}");
                    break;
                }

                last = call;
            }

            if (_refusedAnswers.Count == 0)
            {
                await _test.AwaitAssertAsync(() => Assert.Contains(_journal.Committed.OfType<ToolCallRecorded>(),
                    evt => evt.SessionId == _sessionId && evt.ToolResult.ToolCallId?.Value == last), Wait, cancellationToken: _token);
            }

            Verify(op);
        }

        private async Task PassivateAsync(Op op)
        {
            // The session passivates when it is idle: no child runs, no turn runs, and every result has become an input.
            await QuietAsync();
            var session = await _test.ResolveSessionAsync(_sessionId);
            var watcher = _test.CreateTestProbe();
            watcher.Watch(session);
            session.Tell(ReceiveTimeout.Instance);
            await _test.AwaitAssertAsync(() =>
            {
                _test.AdvanceScheduler(TimeSpan.FromSeconds(1));
                Assert.True(watcher.HasMessages, "The idle session did not passivate.");
            }, Wait, cancellationToken: _token);
            _explored.Add(Explored.PassivateIdle);
            Verify(op);
        }

        private async Task RestartAsync(Op op)
        {
            var model = Model;
            if (model.Live.Count > 0)
                _explored.Add(Explored.RestartWithLiveChild);
            if (model.ApprovalOpen)
                _explored.Add(Explored.RestartWithOpenApproval);
            var session = await _test.ResolveSessionAsync(_sessionId);
            var watcher = _test.CreateTestProbe();
            watcher.Watch(session);
            var drain = _test.AskManagerAsync<DaemonRestartPrepared>(new PrepareForDaemonRestart(_sessionId, "daemon-stop"));
            // The drain waits for the grace period of a live model call. The spec advances that period.
            await _test.AwaitAssertAsync(() =>
            {
                _test.AdvanceScheduler(TimeSpan.FromSeconds(3));
                Assert.True(drain.IsCompleted, "The restart drain did not finish.");
            }, Wait, cancellationToken: _token);
            var prepared = await drain;
            await watcher.ExpectTerminatedAsync(session, Wait, cancellationToken: _token);
            _turnGate = null;
            _subscribedTo = null;

            // The reminder system sends the restart reminder to the recovered session, as it does after a daemon restart.
            if (prepared.RestartReminder is { } reminder)
            {
                var fire = reminder.Schedule.FireAt ?? throw new InvalidOperationException("The restart reminder has no fire time.");
                await _test.AskManagerAsync<ISessionResponse>(new SendUserMessage
                {
                    SessionId = _sessionId,
                    Content = reminder.Instructions,
                    Source = Reminder($"{reminder.Id.Value}:{fire.ToUnixTimeMilliseconds()}")
                });
            }

            Verify(op);
        }

        // The settle ends every open piece of work and waits until the session has nothing left to do.
        private async Task SettleAsync()
        {
            _trace.Add("settle");
            await SubscribeAsync();
            _turnGate?.TrySetResult();
            _turnGate = null;
            if (Model.ApprovalOpen)
                await ApprovalCloseAsync(new Op(Kind.ApprovalClose, string.Empty, 0, Ending.Success));
            foreach (var child in _children.Where(child => Model.Live.Contains(child.Label)))
                child.Gate.TrySetResult();
            await QuietAsync();
            Verify(new Op(Kind.TurnEnd, string.Empty, 0, Ending.Success), settle: true);
        }

        // Quiet: every ended run has a notice, and every admitted input has run. A timeout is not an error here. The
        // facts of the following check name the property that did not hold.
        private async Task QuietAsync()
        {
            try
            {
                await _test.AwaitAssertAsync(() =>
                {
                    var facts = ReadFacts();
                    var model = Model;
                    var ended = model.Ended.Concat(model.Live).Distinct().ToArray();
                    Assert.All(ended, label => Assert.Contains(facts.Runs, run => run.Label == label && run.Notices.Count > 0));
                    var consumed = facts.Consumptions.SelectMany(item => item.Labels).ToHashSet();
                    Assert.All(facts.Admissions, item => Assert.Contains(item.Label, consumed));
                }, Wait, cancellationToken: _token);
            }
            catch (Exception error) when (error is Xunit.Sdk.XunitException or TimeoutException)
            {
                _trace.Add("quiet:timeout");
            }
        }

        private void Verify(Op op, bool settle = false)
        {
            var name = settle ? "Settle" : op.Kind.ToString();
            var operation = _spec.GetOperation<Op, Facts>(name);
            var facts = ReadFacts();
            var (valid, message, updated) = _spec.Allows(operation, op, facts, _profile);
            Assert.True(valid, $"Accordant rejected {string.Join(" -> ", _trace)}: {message}");
            _profile = updated;
        }

        private async Task SubscribeAsync()
        {
            IActorRef? current;
            try
            {
                current = await _test.ResolveSessionAsync(_sessionId);
            }
            catch (ActorNotFoundException)
            {
                current = null;
            }

            if (current is null || !Equals(current, _subscribedTo))
            {
                _subscriber = await _test.JoinAsync(_sessionId);
                _subscribedTo = await _test.ResolveSessionAsync(_sessionId);
            }
        }

        private string LastInputRequester() => _lastInputRequester;

        private void ReleaseEverything()
        {
            _turnGate?.TrySetResult();
            foreach (var child in _children)
                child.Gate.TrySetResult();
        }

        // The projection reads the committed events of this session. It never calls the logic that it checks.
        private Facts ReadFacts()
        {
            var events = _journal.Committed.OfType<ISessionEvent>().Where(evt => evt.SessionId == _sessionId).ToArray();
            var runIdToLabel = _children.ToDictionary(child => child.Accepted.Run.RunId.Value, child => child.Label);
            var admissions = new List<Admission>();
            var inputLabels = new Dictionary<InputId, (string Label, string Requester)>();
            foreach (var evt in events)
            {
                if (evt is InputAdmitted admitted)
                {
                    var requester = admitted.TurnContext.RequesterSenderId?.Value ?? string.Empty;
                    var noticeRun = _children.FirstOrDefault(child => IsNoticeFor(admitted, child.Accepted.Run.RunId));
                    var isNotice = noticeRun.Label is not null;
                    var label = isNotice ? $"notice:{noticeRun.Label}" : admitted.UserMessage.Content ?? string.Empty;
                    if (!isNotice && !IsKnownUserLabel(label))
                        continue;
                    admissions.Add(new Admission(admissions.Count, label, requester, isNotice, Real: true));
                    inputLabels[admitted.InputId] = (label, requester);
                }
                else if (evt is ChildRunEvent legacy && legacy.GetType().Name == "ResultPrepared"
                         && _children.FirstOrDefault(child => child.Accepted.Run.RunId == legacy.RunId) is { Label: not null } owner)
                {
                    // Before S2 the prepared result is the admission of the result. It is not an input.
                    admissions.Add(new Admission(admissions.Count, $"notice:{owner.Label}", owner.Owner, IsNotice: true, Real: false));
                }
            }

            var consumptions = new List<Consumption>();
            for (var index = 0; index < events.Length; index++)
            {
                IReadOnlyList<InputId> ids = events[index] switch
                {
                    ToolBatchStarted batch => batch.ConsumedInputIds,
                    TurnRecorded turn => turn.ConsumedInputIds,
                    InputClosed closed => closed.InputIds,
                    _ => []
                };
                var labels = new List<string>();
                var requesters = new List<string>();
                foreach (var id in ids.Where(inputLabels.ContainsKey))
                {
                    labels.Add(inputLabels[id].Label);
                    requesters.Add(inputLabels[id].Requester);
                }

                // The legacy continuation joins a child result to a turn. Its owner counts as a requester of that turn.
                if (events[index].GetType().GetProperty("ChildContinuation")?.GetValue(events[index]) is ChildContinuation continuation
                    && _children.FirstOrDefault(child => child.Accepted.Run.RunId == continuation.RunId) is { Label: not null } owner)
                {
                    labels.Add($"notice:{owner.Label}");
                    requesters.Add(owner.Owner);
                }

                if (labels.Count > 0)
                    consumptions.Add(new Consumption(consumptions.Count, labels, requesters));
            }

            var abandonedBy = new List<string>();
            for (var index = 0; index < events.Length; index++)
            {
                if (events[index] is not ToolBatchAbandoned)
                    continue;
                var next = events.Skip(index + 1).OfType<InputAdmitted>().FirstOrDefault();
                abandonedBy.Add(next is null ? "none" : _children.Any(child => IsNoticeFor(next, child.Accepted.Run.RunId)) ? "notice" : "user");
            }

            var requests = _test.Clients.Parent.ReceivedMessages;
            var runs = _children.Select(child =>
            {
                var notices = admissions.Where(item => item.Real && item.IsNotice && item.Label == $"notice:{child.Label}").ToArray();
                var consumed = consumptions.Count(c => c.Labels.Contains($"notice:{child.Label}"));
                var delivered = requests.Any(request => CarriesChildResult(request, child.Accepted.Run.RunId));
                return new RunFacts(child.Label, child.Owner, notices, consumed, delivered);
            }).ToArray();
            return new Facts(admissions, consumptions, abandonedBy, runs, [.. _refusedAnswers]);

            bool IsKnownUserLabel(string label)
                => label.StartsWith("msg-", StringComparison.Ordinal)
                   || label.StartsWith("probe:", StringComparison.Ordinal)
                   || label.StartsWith("probe2:", StringComparison.Ordinal)
                   || label.StartsWith("spawn:", StringComparison.Ordinal);
        }
    }
}
