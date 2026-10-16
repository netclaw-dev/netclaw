// -----------------------------------------------------------------------
// <copyright file="RestartResumeAccordantTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using CsCheck;
using Microsoft.Accordant;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Xunit;
using AccordantSpec = Microsoft.Accordant.Spec<Netclaw.Actors.Tests.Sessions.RestartResumeModelState>;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// What an observer needs to predict a restart-resume message: whether the session is up, whether it holds a stored
/// input that no turn finished, whether a restart reminder exists, and how many turns the model served.
/// </summary>
[State]
public partial class RestartResumeModelState
{
    // Idle: the session is up and no turn runs. Running: a turn runs. Down: the daemon restarted.
    public string Phase { get; set; } = "Idle";
    public int Pending { get; set; }
    public bool HasReminder { get; set; }
    public int Starts { get; set; }
}

/// <summary>
/// Executable spec: a restart-resume message is never an ordinary input (D2 decision 5). It resumes the stored input
/// of an interrupted turn when the session is back and holds such an input. In every other state the session
/// acknowledges it and does nothing. The session never stores it as an input, and the model never sees its text.
/// <para>
/// The seam is the real session actor. The routing of this message is in <c>LlmSessionActor</c>, before the input
/// flow, so a flow-level spec cannot see it. Each case runs a few real turns and restarts, so the property runs few
/// cases. CsCheck chooses the operation order, and Accordant predicts each reply. The model sees no actor state.
/// The harness waits for a model call only where the model predicts one, and the final count of calls checks the rest.
/// </para>
/// </summary>
public sealed class RestartResumeAccordantTests : ApprovalWaitTestBase
{
    // CsCheck replays this first case, then explores new random sequences. Failures report their own replay seed.
    private const string InitialSampleSeed = "0N0XIzNsQ0O2";

    private static int _caseCounter;

    private readonly ITestOutputHelper _output;

    public RestartResumeAccordantTests(ITestOutputHelper output) : base(output)
    {
        _output = output;
    }

    private enum Kind { Operator, Finish, Restart, Resume }

    private enum Resumed { Resumed, NoOpWhileRunning, NoOpNothingPending, NoOpWhileIdle }

    private sealed record Command(Kind Kind)
    {
        public override string ToString() => Kind.ToString();
    }

    private sealed record OperatorObservation(string Reply, int AdmittedDelta);
    private sealed record FinishObservation(int PendingAfter);
    private sealed record RestartObservation(bool ReminderIssued);
    private sealed record ResumeObservation(string Reply, int AdmittedDelta);

    // Random operation order rarely reaches a late resume message, because the session must restart twice first.
    // A few short episodes lead the sequence, and random operations follow. An illegal operation is skipped.
    private static Gen<Command[]> GenerateSequences()
    {
        static Command[] Episode(params Kind[] kinds) => kinds.Select(kind => new Command(kind)).ToArray();
        var episodes = Gen.OneOfConst(
            Episode(Kind.Operator, Kind.Restart, Kind.Resume),
            Episode(Kind.Operator, Kind.Restart, Kind.Resume, Kind.Resume),
            Episode(Kind.Operator, Kind.Restart, Kind.Resume, Kind.Finish, Kind.Restart, Kind.Resume),
            Episode(Kind.Operator, Kind.Restart, Kind.Resume, Kind.Finish, Kind.Resume),
            Episode(Kind.Operator, Kind.Finish, Kind.Restart, Kind.Resume),
            Episode(Kind.Operator));
        var random = Gen.Frequency(
                (3, Gen.Const(Kind.Operator)),
                (3, Gen.Const(Kind.Finish)),
                (3, Gen.Const(Kind.Restart)),
                (6, Gen.Const(Kind.Resume)))
            .Select(kind => new Command(kind))
            .Array[0, 6];
        return Gen.Select(episodes, random, (head, tail) => head.Concat(tail).ToArray());
    }

    // The model. Operator input starts a turn. A restart ends the session and keeps the stored input of a running turn.
    // The resume message acts only when the session is down and holds a stored input.
    private static bool Legal(RestartResumeModelState state, Kind kind) => kind switch
    {
        Kind.Operator => state.Phase == "Idle" || (state.Phase == "Down" && state.Pending == 0),
        Kind.Finish => state.Phase == "Running",
        Kind.Restart => state.Phase != "Down" && state.Starts > 0,
        Kind.Resume => state.HasReminder,
        _ => false
    };

    private static Resumed ResumeKindOf(RestartResumeModelState state)
        => state.Phase switch
        {
            "Down" when state.Pending > 0 => Resumed.Resumed,
            "Down" => Resumed.NoOpNothingPending,
            "Running" => Resumed.NoOpWhileRunning,
            _ => Resumed.NoOpWhileIdle
        };

    private static AccordantSpec CreateSpec()
    {
        var spec = new AccordantSpec();
        spec.Operation<Unit, OperatorObservation>("Operator", (_, state) =>
            Expect.That<OperatorObservation>(actual =>
                    actual is { Reply: "Ack", AdmittedDelta: 1 }
                        ? ValidationResult.Valid()
                        : ValidationResult.Invalid(
                            $"A person's message must be acknowledged and stored as one input. Actual: {actual}."))
                .ThenState<RestartResumeModelState>(next =>
                {
                    next.Phase = "Running";
                    next.Pending = 1;
                    next.Starts = state.Starts + 1;
                }));
        spec.Operation<Unit, FinishObservation>("Finish", (_, _) =>
            Expect.That<FinishObservation>(actual =>
                    actual.PendingAfter == 0
                        ? ValidationResult.Valid()
                        : ValidationResult.Invalid($"A finished turn must leave no stored input. Actual: {actual.PendingAfter}."))
                .ThenState<RestartResumeModelState>(next =>
                {
                    next.Phase = "Idle";
                    next.Pending = 0;
                }));
        spec.Operation<Unit, RestartObservation>("Restart", (_, state) =>
            Expect.That<RestartObservation>(actual =>
                    actual.ReminderIssued == (state.Phase == "Running")
                        ? ValidationResult.Valid()
                        : ValidationResult.Invalid(
                            $"A restart reminder exists only when a turn is interrupted. Phase {state.Phase}, issued {actual.ReminderIssued}."))
                .ThenState<RestartResumeModelState>((RestartObservation actual, RestartResumeModelState next) =>
                {
                    next.Phase = "Down";
                    next.HasReminder = state.HasReminder || actual.ReminderIssued;
                }, mock: () => new RestartObservation(false)));
        spec.Operation<Unit, ResumeObservation>("Resume", (_, state) =>
            Expect.That<ResumeObservation>(actual =>
                    actual is { Reply: "Ack", AdmittedDelta: 0 }
                        ? ValidationResult.Valid()
                        : ValidationResult.Invalid(
                            $"A restart-resume message ({ResumeKindOf(state)}) must be acknowledged and must not become an input. Actual: {actual}."))
                .ThenState<RestartResumeModelState>(next =>
                {
                    if (state.Phase != "Down")
                        return;

                    if (state.Pending > 0)
                    {
                        next.Phase = "Running";
                        next.Starts = state.Starts + 1;
                    }
                    else
                    {
                        next.Phase = "Idle";
                    }
                }));
        return spec;
    }

    // The generator must reach each way a resume message can arrive. This check needs no actor.
    [Fact]
    public void The_generator_reaches_every_state_in_which_a_resume_message_arrives()
    {
        var reached = new Dictionary<Resumed, int>();
        GenerateSequences().Sample(sequence =>
        {
            var seen = new HashSet<Resumed>();
            var state = new RestartResumeModelState();
            foreach (var command in sequence)
            {
                if (!Legal(state, command.Kind))
                    continue;
                switch (command.Kind)
                {
                    case Kind.Operator:
                        state.Phase = "Running";
                        state.Pending = 1;
                        state.Starts++;
                        break;
                    case Kind.Finish:
                        state.Phase = "Idle";
                        state.Pending = 0;
                        break;
                    case Kind.Restart:
                        state.HasReminder |= state.Phase == "Running";
                        state.Phase = "Down";
                        break;
                    case Kind.Resume:
                        seen.Add(ResumeKindOf(state));
                        if (state.Phase == "Down")
                        {
                            if (state.Pending > 0)
                                state.Starts++;
                            state.Phase = state.Pending > 0 ? "Running" : "Idle";
                        }

                        break;
                }
            }
            foreach (var kind in seen)
                reached[kind] = reached.GetValueOrDefault(kind) + 1;
        }, iter: 1_000, seed: InitialSampleSeed, threads: 1);

        // The property runs few cases, so each way must be common enough to appear in a short run.
        _output.WriteLine($"cases per resume kind of 1000: {string.Join(", ", reached.Select(pair => $"{pair.Key}={pair.Value}"))}");
        foreach (var required in Enum.GetValues<Resumed>())
            Assert.True(reached.GetValueOrDefault(required) >= 100, $"Only {reached.GetValueOrDefault(required)} of 1000 cases reach {required}.");
    }

    [Fact]
    public void Generated_restart_resume_messages_are_acknowledged_and_never_stored_as_input()
    {
        // Act: CsCheck chooses the operation order. Each case runs on its own session.
        GenerateSequences().Sample(
            sequence => RunCaseAsync(sequence).GetAwaiter().GetResult(),
            iter: 12, seed: InitialSampleSeed, threads: 1, print: DescribeSequence);
    }

    private static string DescribeSequence(Command[] sequence)
        => string.Join(" -> ", sequence.Select((command, index) => $"c{index}:{command}"));

    private async Task RunCaseAsync(Command[] sequence)
    {
        var caseNumber = Interlocked.Increment(ref _caseCounter);
        var sessionId = new SessionId($"test-channel/restart-resume-model-{caseNumber}");
        var taskText = $"Finish task {caseNumber}";
        var spec = CreateSpec();
        var profile = new StateProfile(new RestartResumeModelState());
        var trace = new List<string>();
        ReminderDefinition? reminder = null;
        TaskCompletionSource? gate = null;

        void Verify<TResponse>(string name, TResponse response)
        {
            var operation = spec.GetOperation<Unit, TResponse>(name);
            var (valid, message, updated) = spec.Allows(operation, Unit.Value, response, profile);
            Assert.True(valid, $"Accordant rejected {string.Join(" -> ", trace)}: {message}");
            profile = updated;
        }

        try
        {
            for (var index = 0; index < sequence.Length; index++)
            {
                var kind = sequence[index].Kind;
                var model = (RestartResumeModelState)profile.SingleState();
                if (!Legal(model, kind))
                    continue;

                trace.Add($"c{index}:{kind}");
                var admittedBefore = await AdmittedCountAsync(sessionId);
                switch (kind)
                {
                    case Kind.Operator:
                    {
                        gate = await ArmGateAsync();
                        var callsBefore = FakeChat.CallCount;
                        var reply = await SendOperatorAsync(sessionId, taskText);
                        await AwaitCallAfterAsync(callsBefore);
                        Verify("Operator", new OperatorObservation(
                            ReplyName(reply), await AdmittedCountAsync(sessionId) - admittedBefore));
                        break;
                    }
                    case Kind.Finish:
                        gate!.TrySetResult();
                        await AwaitNoPendingInputAsync(sessionId);
                        Verify("Finish", new FinishObservation(
                            PendingInputIds(await ReadJournalAsync(sessionId)).Count));
                        break;
                    case Kind.Restart:
                    {
                        var session = await ResolveSessionAsync(sessionId);
                        Watch(session);
                        var drained = await SessionManager.Ask<DaemonRestartPrepared>(
                            new PrepareForDaemonRestart(sessionId, "daemon-stop"),
                            TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
                        Sys.Stop(session);
                        await ExpectTerminatedAsync(session, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
                        reminder = drained.RestartReminder ?? reminder;
                        Verify("Restart", new RestartObservation(drained.RestartReminder is not null));
                        break;
                    }
                    case Kind.Resume:
                    {
                        // Synchronization only: the harness waits for a model call where the model predicts a resume.
                        var resumes = ResumeKindOf(model) == Resumed.Resumed;
                        var callsBefore = FakeChat.CallCount;
                        if (resumes)
                            gate = await ArmGateAsync();
                        var reply = await SessionManager.Ask<ISessionResponse>(new SendUserMessage
                        {
                            SessionId = sessionId,
                            Content = reminder!.Instructions,
                            Source = RestartReminderSource(reminder)
                        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                        if (resumes)
                            await AwaitCallAfterAsync(callsBefore);
                        Verify("Resume", new ResumeObservation(
                            ReplyName(reply), await AdmittedCountAsync(sessionId) - admittedBefore));
                        break;
                    }
                }
            }

            // The model served one call for each turn start. A resume message adds none, and its text never reaches the model.
            var final = (RestartResumeModelState)profile.SingleState();
            gate?.TrySetResult();
            if (final.Phase == "Running")
                await AwaitNoPendingInputAsync(sessionId);
            await AwaitAssertAsync(
                () =>
                {
                    var served = FakeChat.ReceivedMessages.Count(request => LastUserText(request) == taskText);
                    Assert.True(served == final.Starts,
                        $"The model must serve {final.Starts} turn start(s) for [{string.Join(" -> ", trace)}]. Actual: {served}.");
                    return Task.CompletedTask;
                },
                TimeSpan.FromSeconds(10),
                cancellationToken: TestContext.Current.CancellationToken);
            if (reminder is not null)
                Assert.DoesNotContain(FakeChat.ReceivedMessages, request => UserTexts(request).Contains(reminder.Instructions));
        }
        finally
        {
            gate?.TrySetResult();
        }
    }

    // Holds the next model call. The harness opens the gate when the case finishes the turn.
    private Task<TaskCompletionSource> ArmGateAsync()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeChat.NextResponseGate = gate;
        return Task.FromResult(gate);
    }

    private async Task AwaitCallAfterAsync(int callsBefore)
        => await AwaitAssertAsync(
            () =>
            {
                Assert.True(FakeChat.CallCount > callsBefore, "The held model call did not start.");
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

    private async Task AwaitNoPendingInputAsync(SessionId sessionId)
        => await AwaitAssertAsync(
            async () => Assert.Empty(PendingInputIds(await ReadJournalAsync(sessionId))),
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

    private async Task<ISessionResponse> SendOperatorAsync(SessionId sessionId, string text)
        => await SessionManager.Ask<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = text,
            Source = RequesterSource(Requester)
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private async Task<int> AdmittedCountAsync(SessionId sessionId)
        => (await ReadJournalAsync(sessionId)).Count(entry => entry.Payload is InputAdmitted);

    private static string ReplyName(ISessionResponse reply)
        => reply switch
        {
            CommandNack => "Nack",
            CommandAck => "Ack",
            _ => reply.GetType().Name
        };
}
