// -----------------------------------------------------------------------
// <copyright file="RestartSlashResumeQueueTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Xunit;
using AkkaError = Akka.Event.Error;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable specs for issue #2587. A restart reminder arrives in an idle session. The first pending input is a
/// slash command. The restart resume must not lose the pending inputs that follow the slash command.
/// <list type="bullet">
/// <item>After the resume, each pending input with a valid authority runs, or it closes with a defined outcome.</item>
/// <item>An input runs under its own recorded authority, not under the authority of the slash command.</item>
/// <item>When the slash command keeps the session in Ready, the session starts the next queued input.</item>
/// </list>
/// <para>
/// Each spec writes the pending inputs into the journal and then starts the session, as a daemon does after a
/// restart. An unknown slash command closes its input and keeps the session in Ready. A known inline skill starts a
/// model call, so the session moves to Processing. The system prompt names the audience of its turn, so a model call
/// shows which authority its turn ran under.
/// </para>
/// </summary>
public sealed class RestartSlashResumeQueueTests : ApprovalWaitTestBase
{
    private const string Alice = "U-alice";
    private const string Bob = "U-bob";
    private const string UnknownSlash = "/no-such-command";
    private const string InlineSkill = "resume-skill";
    private const string SkillBodyMarker = "SKILL-BODY-MARKER-2587";
    private const string SkillTask = "do the resumed skill task";
    private const string AliceLaterText = "Alice later work after the slash command.";
    private const string BobText = "Bob work after the slash command.";
    private const string Carol = "U-carol";
    private const string Zed = "U-zed";
    private const string ZedText = "Zed live work.";
    private const string TeamPromptMarker = ReminderPromptMarker;
    private const string InvalidAuthorityReason = "The interrupted input authority is invalid.";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    public RestartSlashResumeQueueTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        base.ConfigureSessionServices(services);

        // The skill file is in the test temp folder, so the base class deletes it.
        var skillDir = Path.Combine(TestPaths.BasePath, InlineSkill);
        Directory.CreateDirectory(skillDir);
        var skillFile = Path.Combine(skillDir, "SKILL.md");
        File.WriteAllText(skillFile, $"""
            ---
            name: {InlineSkill}
            description: Resume skill.
            ---

            # Resume skill

            {SkillBodyMarker}
            """);
        var skills = new SkillRegistry();
        skills.Register(new SkillEntry(InlineSkill, "Resume skill", "Resume skill.", skillFile, skillDir, null));
        services.AddSingleton(skills);
    }

    /// <summary>
    /// Case 1. Alice has a pending unknown slash command and a later pending text input. The slash command closes and
    /// keeps the session in Ready. The text input of Alice must then run as its own turn, under the authority of Alice.
    /// Bug on dev: CompleteSlashResume returns early in Ready, so it does not queue the text input. No turn runs it.
    /// </summary>
    [Fact]
    public async Task A_slash_command_that_keeps_ready_does_not_strand_a_later_input_of_the_same_requester()
    {
        // Arrange
        var sessionId = new SessionId("test-channel/restart-slash-ready-same-requester");
        var slash = Admitted(sessionId, "slash", UnknownSlash, PersonSource(Alice, TrustAudience.Personal), executableText: UnknownSlash);
        var later = Admitted(sessionId, "later", AliceLaterText, PersonSource(Alice, TrustAudience.Personal));
        await SeedJournalAsync(sessionId, slash, later);
        var subscriber = await JoinNewSubscriberAsync(sessionId);

        // Act
        var reply = await SendRestartResumeAsync(sessionId, TrustAudience.Personal);

        // Assert: the slash command closes with a skipped turn. The text input of Alice then runs as a turn.
        Assert.IsAssignableFrom<CommandAck>(reply);
        await ExpectSkippedTurnAsync(subscriber);
        await AwaitAssertAsync(
            () => Assert.True(
                FakeChat.ReceivedMessages.Any(request => LastUserText(request) == AliceLaterText),
                "The pending text input of the same requester must run after the slash command. No model call carried it."),
            Deadline, cancellationToken: TestContext.Current.CancellationToken);
        var request = Assert.Single(FakeChat.ReceivedMessages, request => LastUserText(request) == AliceLaterText);
        Assert.Contains(RequesterPromptMarker, SystemText(request));
        Assert.DoesNotContain(UnknownSlash, UserTexts(request));
        await ExpectTurnsAsync(subscriber, 1);
        await AssertConsumedByTurnAsync(sessionId, later.InputId);
        await AssertNoInputStaysPendingAsync(sessionId);
    }

    /// <summary>
    /// Case 2. Alice has a pending unknown slash command. Bob has a later pending text input, with another audience.
    /// The text input of Bob must run as its own turn under the authority of Bob, not under the authority of Alice.
    /// Bug on dev: TryResumeInterruptedInput queues the inputs of another requester only in Processing. No turn runs
    /// the input of Bob.
    /// </summary>
    [Fact]
    public async Task A_slash_command_that_keeps_ready_does_not_strand_a_later_input_of_another_requester()
    {
        // Arrange
        var sessionId = new SessionId("test-channel/restart-slash-ready-other-requester");
        var slash = Admitted(sessionId, "slash", UnknownSlash, PersonSource(Alice, TrustAudience.Personal), executableText: UnknownSlash);
        var bob = Admitted(sessionId, "bob", BobText, PersonSource(Bob, TrustAudience.Team));
        await SeedJournalAsync(sessionId, slash, bob);
        var subscriber = await JoinNewSubscriberAsync(sessionId);

        // Act
        var reply = await SendRestartResumeAsync(sessionId, TrustAudience.Personal);

        // Assert: the slash command closes. The text input of Bob then runs under the authority of Bob.
        Assert.IsAssignableFrom<CommandAck>(reply);
        await ExpectSkippedTurnAsync(subscriber);
        await AwaitAssertAsync(
            () => Assert.True(
                FakeChat.ReceivedMessages.Any(request => LastUserText(request) == BobText),
                "The pending text input of another requester must run after the slash command. No model call carried it."),
            Deadline, cancellationToken: TestContext.Current.CancellationToken);
        var request = Assert.Single(FakeChat.ReceivedMessages, request => LastUserText(request) == BobText);
        Assert.Contains(TeamPromptMarker, SystemText(request));
        Assert.DoesNotContain(RequesterPromptMarker, SystemText(request));
        await ExpectTurnsAsync(subscriber, 1);
        await AssertConsumedByTurnAsync(sessionId, bob.InputId);
        await AssertNoInputStaysPendingAsync(sessionId);
    }

    /// <summary>
    /// Case 5. Alice has a pending unknown slash command. A reminder fire and a later text input of Alice wait behind
    /// it. Each one runs exactly once, in admission order, and each one runs under its own authority. A redelivered
    /// restart reminder must not run the fire again.
    /// Bug on dev: no turn runs the fire or the later input when one restart reminder arrives. On dev, a second
    /// restart reminder resumes the inputs, so the redelivered case passes on dev. It guards against a double run.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_slash_command_that_keeps_ready_runs_a_waiting_reminder_fire_once(bool redelivered)
    {
        // Arrange
        var sessionId = new SessionId($"test-channel/restart-slash-ready-fire-{(redelivered ? "redelivered" : "once")}");
        const string fireKey = "build-check:1700000000000";
        var slash = Admitted(sessionId, "slash", UnknownSlash, PersonSource(Alice, TrustAudience.Personal), executableText: UnknownSlash);
        var fire = Admitted(sessionId, "fire", ReminderText, ReminderSource(fireKey));
        var later = Admitted(sessionId, "later", AliceLaterText, PersonSource(Alice, TrustAudience.Personal));
        await SeedJournalAsync(sessionId, slash, fire, later);
        var subscriber = await JoinNewSubscriberAsync(sessionId);

        // Act
        var reply = await SendRestartResumeAsync(sessionId, TrustAudience.Personal);
        if (redelivered)
            Assert.IsAssignableFrom<CommandAck>(await SendRestartResumeAsync(sessionId, TrustAudience.Personal));

        // Assert: the fire runs under the reminder authority, and the later input of Alice runs after it.
        Assert.IsAssignableFrom<CommandAck>(reply);
        await AwaitAssertAsync(
            () => Assert.True(
                FakeChat.ReceivedMessages.Any(request => LastUserText(request) == AliceLaterText),
                "The pending inputs behind the slash command must run. No model call carried the later input of Alice."),
            Deadline, cancellationToken: TestContext.Current.CancellationToken);
        await ExpectTurnsAsync(subscriber, 2);
        await AssertNoInputStaysPendingAsync(sessionId);
        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);

        var requests = FakeChat.ReceivedMessages;
        var fireRequest = Assert.Single(requests, request => LastUserText(request) == ReminderText);
        var laterRequest = Assert.Single(requests, request => LastUserText(request) == AliceLaterText);
        Assert.Contains(TeamPromptMarker, SystemText(fireRequest));
        Assert.Contains(RequesterPromptMarker, SystemText(laterRequest));
        Assert.True(
            IndexOfRequest(requests, fireRequest) < IndexOfRequest(requests, laterRequest),
            "The fire was admitted before the later input of Alice, so it must run first.");
        var events = await ReadJournalAsync(sessionId);
        Assert.Single(events.Select(entry => entry.Payload).OfType<InputAdmitted>(),
            admitted => admitted.SourceMessageId == fireKey);
        Assert.Single(events.Select(entry => entry.Payload).OfType<TurnRecorded>(),
            turn => turn.ConsumedInputIds.Contains(fire.InputId));
    }

    /// <summary>
    /// Case 3, guard. The pending slash command names an inline skill, so the session moves to Processing. The skill
    /// turn runs first. The later text input then runs as its own turn under its own authority. Dev passes this spec.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_slash_command_that_moves_to_processing_still_runs_the_later_input(bool otherRequester)
    {
        // Arrange
        var sessionId = new SessionId($"test-channel/restart-slash-processing-{(otherRequester ? "other" : "same")}");
        var skillCommand = $"/{InlineSkill} {SkillTask}";
        var laterText = otherRequester ? BobText : AliceLaterText;
        var laterSource = otherRequester
            ? PersonSource(Bob, TrustAudience.Team)
            : PersonSource(Alice, TrustAudience.Personal);
        var slash = Admitted(sessionId, "slash", skillCommand, PersonSource(Alice, TrustAudience.Personal), executableText: skillCommand);
        var later = Admitted(sessionId, "later", laterText, laterSource);
        await SeedJournalAsync(sessionId, slash, later);
        var subscriber = await JoinNewSubscriberAsync(sessionId);

        // Act
        var reply = await SendRestartResumeAsync(sessionId, TrustAudience.Personal);

        // Assert: the skill turn runs under Alice. The later input runs after it, as its own turn.
        Assert.IsAssignableFrom<CommandAck>(reply);
        await ExpectTurnsAsync(subscriber, 2);
        var requests = FakeChat.ReceivedMessages;
        // The skill turn sends the remainder of the command as its user text, with the skill body.
        var skillRequest = Assert.Single(requests, request => LastUserText(request) == SkillTask);
        Assert.Contains(skillRequest, message => message.Text.Contains(SkillBodyMarker, StringComparison.Ordinal));
        var laterRequest = Assert.Single(requests, request => LastUserText(request) == laterText);
        Assert.Contains(RequesterPromptMarker, SystemText(skillRequest));
        Assert.Contains(otherRequester ? TeamPromptMarker : RequesterPromptMarker, SystemText(laterRequest));
        Assert.True(
            IndexOfRequest(requests, skillRequest) < IndexOfRequest(requests, laterRequest),
            "The slash command was admitted first, so its skill turn must run first.");
        await AssertConsumedByTurnAsync(sessionId, later.InputId);
        await AssertNoInputStaysPendingAsync(sessionId);
    }

    /// <summary>
    /// The restart resume path for the #2576 rows.
    /// </summary>
    public enum ResumePath
    {
        /// <summary>An idle session resumes the leading inputs as one model turn.</summary>
        Ready,

        /// <summary>The first input is an unknown slash command of Alice. It closes and keeps the session in Ready.</summary>
        Slash,

        /// <summary>A live turn of Zed runs, so the restart message only queues the pending inputs.</summary>
        Processing
    }

    /// <summary>
    /// Issue #2576. One rule covers a pending input with a stored authority that does not parse. The rule applies to
    /// each restart resume path:
    /// <list type="bullet">
    /// <item>The reply is a nack only when the damaged input is in the leading inputs that the restart message resumes.
    /// In an idle session, only a damaged first input does this. In Processing, the message resumes no leading
    /// inputs, so it never sends a nack.</item>
    /// <item>A damaged input ends the leading inputs. The session logs it and keeps it pending in the journal.</item>
    /// <item>Each valid input runs once, under its own authority. After a nack, no input runs and each input stays
    /// pending.</item>
    /// <item>The session does not throw. An actor restart logs an Error event with a cause.</item>
    /// </list>
    /// A layout gives the pending inputs in admission order. A, B and C are Alice, Bob and Carol. The "!" prefix marks
    /// a damaged input.
    /// </summary>
    [Theory]
    [InlineData(ResumePath.Ready, "A !A", false)]
    [InlineData(ResumePath.Ready, "A B !C", false)]
    [InlineData(ResumePath.Ready, "A !C B", false)]
    [InlineData(ResumePath.Ready, "!A B", true)]
    [InlineData(ResumePath.Slash, "A !A", false)]
    [InlineData(ResumePath.Slash, "A B !C", false)]
    [InlineData(ResumePath.Slash, "A !C B", false)]
    [InlineData(ResumePath.Slash, "!A B", true)]
    [InlineData(ResumePath.Processing, "A !A", false)]
    [InlineData(ResumePath.Processing, "A B !C", false)]
    [InlineData(ResumePath.Processing, "A !C B", false)]
    [InlineData(ResumePath.Processing, "!A B", false)]
    public async Task A_damaged_pending_input_stays_pending_and_the_valid_inputs_run(ResumePath path, string layout, bool nack)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var thread = $"damaged-{path}-{layout.Replace(' ', '-').Replace('!', 'x')}".ToLowerInvariant();
        var sessionId = new SessionId($"test-channel/{thread}");
        var inputs = layout.Split(' ').Select((token, index) => LayoutEntry(sessionId, path, token, index)).ToArray();
        await SeedJournalAsync(sessionId, inputs.Select(input => input.Admitted).ToArray<ISessionEvent>());
        await JoinNewSubscriberAsync(sessionId);
        var lifecycle = CreateTestProbe("session-lifecycle");
        lifecycle.Watch(await ResolveSessionAsync(sessionId));
        var errors = CreateTestProbe("session-errors");
        Sys.EventStream.Subscribe(errors.Ref, typeof(AkkaError));
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (path == ResumePath.Processing)
        {
            // A live input of another requester holds its model call open, so the session stays in Processing.
            FakeChat.NextResponseGate = hold;
            await SendAsync(sessionId, ZedText, PersonSource(Zed, TrustAudience.Team));
            await FakeChat.FirstCallEntered.Task.WaitAsync(Deadline, ct);
        }

        // Act: a probe takes the reply, so a session that throws before it replies gives a clear failure.
        var replies = CreateTestProbe("restart-replies");
        SessionManager.Tell(RestartResumeMessage(sessionId, TrustAudience.Personal), replies.Ref);
        var crashes = new List<AkkaError>();
        await AwaitAssertAsync(
            () => Assert.True(CollectCrashes(errors, thread, crashes) || replies.HasMessages, "The restart message got no reply."),
            Deadline, cancellationToken: ct);
        hold.TrySetResult();

        // Assert
        AssertNoCrash();
        var reply = replies.ReceiveOne(TimeSpan.Zero, ct);
        Assert.True(
            nack ? reply is CommandNack { Reason: InvalidAuthorityReason } : reply is CommandAck,
            $"The rule expects {(nack ? "a nack" : "an ack")}. The reply was {reply}.");
        var runs = nack ? [] : inputs.Where(input => !input.Damaged).ToArray();
        var stays = inputs.Except(runs).Select(input => input.Admitted.InputId).ToArray();
        await AwaitAssertAsync(
            async () =>
            {
                AssertNoCrash();
                Assert.All(runs.Where(input => input.Marker is not null), input => Assert.True(
                    FakeChat.ReceivedMessages.Any(request => LastUserText(request) == input.Content),
                    $"No model call carried the valid input {input.Admitted.InputId}."));
                Assert.Equal(stays, PendingInputIds(await ReadJournalAsync(sessionId)));
            },
            Deadline, cancellationToken: ct);
        foreach (var input in inputs)
        {
            if (runs.Contains(input) && input.Marker is { } marker)
                Assert.Contains(marker, SystemText(
                    Assert.Single(FakeChat.ReceivedMessages, request => LastUserText(request) == input.Content)));
            else
                Assert.DoesNotContain(FakeChat.ReceivedMessages, request => UserTexts(request).Contains(input.Content));
        }

        void AssertNoCrash()
        {
            Assert.False(CollectCrashes(errors, thread, crashes),
                $"The session must not throw for a damaged pending input. It threw: {crashes.FirstOrDefault()?.Cause}");
            Assert.False(lifecycle.HasMessages, "The session actor must not stop for a damaged pending input.");
        }
    }

    // Marker is the prompt audience of the turn that runs the input. A slash command runs no model call, so it has none.
    private sealed record LayoutInput(InputAdmitted Admitted, string Content, string? Marker, bool Damaged);

    private static LayoutInput LayoutEntry(SessionId sessionId, ResumePath path, string token, int index)
    {
        var damaged = token.StartsWith('!');
        var requester = token.TrimStart('!');
        var (sender, audience) = requester switch
        {
            "A" => (Alice, TrustAudience.Personal),
            "B" => (Bob, TrustAudience.Team),
            "C" => (Carol, TrustAudience.Team),
            _ => throw new ArgumentException($"Unknown requester {requester} in the layout.", nameof(token))
        };
        var slash = path == ResumePath.Slash && index == 0;
        var content = slash ? UnknownSlash : $"Work {index} of {requester}.";
        var admitted = Admitted(sessionId, $"{index}", content, PersonSource(sender, audience), slash ? UnknownSlash : null);
        if (damaged)
            admitted = admitted with { TurnContext = admitted.TurnContext with { TurnId = string.Empty } };
        var marker = slash ? null : audience == TrustAudience.Personal ? RequesterPromptMarker : TeamPromptMarker;
        return new LayoutInput(admitted, content, marker, damaged);
    }

    // An actor restart logs its exception as an Error event with a cause. The log line for a damaged input has no cause.
    private static bool CollectCrashes(Akka.TestKit.TestProbe errors, string thread, List<AkkaError> crashes)
    {
        while (errors.HasMessages)
        {
            if (errors.ReceiveOne(TimeSpan.Zero) is AkkaError { Cause: not null } error
                && error.LogSource.Contains(thread, StringComparison.Ordinal))
                crashes.Add(error);
        }

        return crashes.Count > 0;
    }

    private static InputAdmitted Admitted(
        SessionId sessionId,
        string name,
        string content,
        MessageSource source,
        string? executableText = null)
    {
        // The same shape as SessionInputFlow.PrepareAdmission writes for a live input.
        var context = TurnContext.FromMessageSource(sessionId, source.TurnId ?? new TurnId($"turn-{name}"), source);
        return new InputAdmitted
        {
            SessionId = sessionId,
            InputId = new InputId($"input-{name}"),
            SourceMessageId = source.MessageId ?? source.ReminderId?.Value,
            UserMessage = new SerializableChatMessage
            {
                Role = Netclaw.Actors.Protocol.ChatRole.User,
                Content = content
            },
            ExecutableText = executableText,
            TurnContext = context.ToRecord() with { AdoptedSpeakerIds = context.AdoptedSpeakerIds.ToArray() },
            AdmittedAtMs = 1_700_000_000_000
        };
    }

    private static MessageSource PersonSource(string senderId, TrustAudience audience)
    {
        var boundary = audience == TrustAudience.Personal ? TrustBoundary.Personal : TrustBoundary.Team;
        return RequesterSource(senderId) with { Audience = audience, Boundary = boundary };
    }

    // The restart reminder is not an input. The session knows it by its id prefix and its fixed instruction.
    private Task<ISessionResponse> SendRestartResumeAsync(SessionId sessionId, TrustAudience audience)
        => SessionManager.Ask<ISessionResponse>(
            RestartResumeMessage(sessionId, audience), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private static SendUserMessage RestartResumeMessage(SessionId sessionId, TrustAudience audience)
    {
        var reminderId = $"{ReminderIdGenerator.Generate("restart resume").Value}:1";
        return new SendUserMessage
        {
            SessionId = sessionId,
            Content = SessionExecutionLifetime.RestartReminderInstruction,
            Source = new MessageSource
            {
                ChannelType = ChannelType.Slack,
                SenderId = new SenderId("reminder-system"),
                MessageId = reminderId,
                TurnId = new TurnId(reminderId),
                Audience = audience,
                Boundary = audience == TrustAudience.Personal ? TrustBoundary.Personal : TrustBoundary.Team,
                Principal = PrincipalClassification.VerifiedAutomation,
                Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted),
                ReceivedAt = new DateTimeOffset(2026, 5, 17, 12, 0, 0, TimeSpan.Zero),
                ReminderId = new ReminderId(reminderId)
            }
        };
    }

    private static async Task ExpectSkippedTurnAsync(Akka.TestKit.TestProbe subscriber)
        => await subscriber.FishForMessageAsync(
            message => message is TurnCompleted { Outcome: TurnOutcome.Skipped },
            Deadline,
            cancellationToken: TestContext.Current.CancellationToken);

    private async Task AssertConsumedByTurnAsync(SessionId sessionId, InputId inputId)
        => await AwaitAssertAsync(
            async () =>
            {
                var events = await ReadJournalAsync(sessionId);
                Assert.Single(events.Select(entry => entry.Payload).OfType<TurnRecorded>(),
                    turn => turn.ConsumedInputIds.Contains(inputId));
            },
            Deadline, cancellationToken: TestContext.Current.CancellationToken);

    private static int IndexOfRequest(
        IReadOnlyList<IReadOnlyList<ChatMessage>> requests,
        IReadOnlyList<ChatMessage> request)
    {
        for (var index = 0; index < requests.Count; index++)
        {
            if (ReferenceEquals(requests[index], request))
                return index;
        }

        throw new InvalidOperationException("The request is not in the list.");
    }

    private async Task SeedJournalAsync(SessionId sessionId, params ISessionEvent[] events)
    {
        var seeder = Sys.ActorOf(Props.Create(() => new JournalSeeder($"session-{sessionId.Value}")));
        foreach (var evt in events)
            await seeder.Ask<Akka.Done>(evt, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Watch(seeder);
        Sys.Stop(seeder);
        await ExpectTerminatedAsync(seeder, cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class JournalSeeder : ReceivePersistentActor
    {
        public JournalSeeder(string persistenceId)
        {
            PersistenceId = persistenceId;
            Recover<object>(_ => { });
            Command<ISessionEvent>(evt =>
            {
                var replyTo = Sender;
                Persist(evt, _ => replyTo.Tell(Akka.Done.Instance));
            });
        }

        public override string PersistenceId { get; }
    }
}
