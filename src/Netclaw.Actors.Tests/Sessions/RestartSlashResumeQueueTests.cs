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
    /// Case 4, guard. The input after the slash command has a damaged authority record. The restart resume validates
    /// the inputs before it runs the slash command. It answers with a CommandNack, runs no command and no model call,
    /// and the journal keeps both inputs pending. Dev passes this spec.
    /// </summary>
    [Fact]
    public async Task A_damaged_input_after_the_slash_command_gets_a_nack_and_runs_nothing()
    {
        // Arrange
        var sessionId = new SessionId("test-channel/restart-slash-damaged-later");
        var slash = Admitted(sessionId, "slash", UnknownSlash, PersonSource(Alice, TrustAudience.Personal), executableText: UnknownSlash);
        var later = Admitted(sessionId, "later", AliceLaterText, PersonSource(Alice, TrustAudience.Personal));
        var damaged = later with { TurnContext = later.TurnContext with { TurnId = string.Empty } };
        await SeedJournalAsync(sessionId, slash, damaged);
        await JoinNewSubscriberAsync(sessionId);

        // Act
        var reply = await SendRestartResumeAsync(sessionId, TrustAudience.Personal);

        // Assert
        Assert.Equal(InvalidAuthorityReason, Assert.IsType<CommandNack>(reply).Reason);
        Assert.Equal(0, FakeChat.CallCount);
        var pending = PendingInputIds(await ReadJournalAsync(sessionId));
        Assert.Equal(new[] { slash.InputId, damaged.InputId }, pending);
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
    {
        var reminderId = $"{ReminderIdGenerator.Generate("restart resume").Value}:1";
        return SessionManager.Ask<ISessionResponse>(new SendUserMessage
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
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
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
