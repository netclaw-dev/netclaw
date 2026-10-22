// -----------------------------------------------------------------------
// <copyright file="ChildConsentTestBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Skills;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Shared setup for the PR 1 specs of child-approval-events-and-parent-pause: approval notices, the child consent
/// pause, the reminder replacement rule, and the recovery of pending notices.
/// <para>
/// A child whose task label starts with <c>ask</c> calls <c>authority_probe</c> once. The probe needs consent, so the
/// child asks the parent session through the approval bridge, and the session emits a child prompt with the call ID
/// <c>{runId}/subagent-approval/{n}</c>. After an answer the child ends with its text. A child with another label ends
/// with its text at once. A spec reads the journal and the subscriber outputs, never the actor state.
/// </para>
/// <para>
/// An approval notice is an admitted input with the source kind <c>child-consent</c>. The specs find it by that source
/// kind and then assert the other facts of the notice. The notice text follows design D4.
/// </para>
/// </summary>
public abstract class ChildConsentTestBase : ChildNoticeTestBase
{
    protected const string NoticeKind = "child-consent";
    protected const string OpenNoticeHeading = "Child consent requests wait for the operator.";
    protected const string ClosingNoticeHeading = "Child consent requests are closed.";
    protected const string RoutedSkill = "ask-route";

    private static readonly Regex AskLabel = new(@"\[(ask[^\]\s]*)\]", RegexOptions.Compiled);

    protected ChildConsentTestBase(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        base.ConfigureSessionServices(services);

        // A routed skill lets a person start a child with a slash command. The skill file sits in the test temp folder.
        var skillDirectory = Path.Combine(TestPaths.WorkspacesDirectory, "skills", RoutedSkill);
        Directory.CreateDirectory(skillDirectory);
        var skillFile = Path.Combine(skillDirectory, "SKILL.md");
        File.WriteAllText(skillFile, $"""
            ---
            name: {RoutedSkill}
            description: Route a review to child-a.
            metadata:
              subagent: child-a
            ---

            # Ask Route

            Review the request.
            """);
        var skills = new SkillRegistry();
        skills.Register(new SkillEntry(RoutedSkill, "Ask Route", "Route a review to child-a.", skillFile, skillDirectory, null)
        {
            HasSubagentRoutingMetadata = true,
            Subagent = "child-a"
        });
        services.AddSingleton(skills);
    }

    /// <summary>Scripts both child clients. An "ask" child asks for consent once, and then ends with its text.</summary>
    protected void ScriptChildren()
    {
        Clients.A.ResponseContentsSelector = ChildReply;
        Clients.B.ResponseContentsSelector = ChildReply;
    }

    private static IReadOnlyList<AIContent>? ChildReply(IReadOnlyList<ChatMessage> request)
    {
        var task = request.FirstOrDefault(message => message.Role == Microsoft.Extensions.AI.ChatRole.User)?.Text ?? string.Empty;
        if (AskLabel.Match(task) is not { Success: true } match)
            return null;
        var label = match.Groups[1].Value;
        var probe = $"cprobe-{label}";
        return HasResult(request, probe)
            ? [new TextContent($"{ChildOutput} {label}")]
            : [Probe(probe)];
    }

    /// <summary>
    /// Starts a child of the requester. An "ask" label makes the child ask for consent. With <paramref name="hold"/>
    /// the child waits at its first model call until the caller opens the returned gate. The spawn turn completes
    /// before this method returns.
    /// </summary>
    private protected async Task<(ChildRunAccepted Accepted, TaskCompletionSource Gate)> StartConsentChildAsync(
        SessionId sessionId, Akka.TestKit.TestProbe subscriber, MessageSource source, string label, bool hold = true)
    {
        var journal = await JournalAsync();
        var gate = NewGate();
        if (hold)
            Clients.A.NextResponseGate = gate;
        else
            gate.TrySetResult();
        var before = Accepted(journal, sessionId).Count;
        await SendAsync(sessionId, $"spawn:child-a:{label}", source);
        await AwaitAssertAsync(() =>
        {
            Assert.Equal(before + 1, Accepted(journal, sessionId).Count);
            // The held child took the gate, so the next child can take a new one.
            if (hold)
                Assert.Null(Clients.A.NextResponseGate);
        }, Deadline, cancellationToken: Cancellation);
        await ExpectTurnsAsync(subscriber, 1);
        return (Accepted(journal, sessionId)[before], gate);
    }

    /// <summary>The child prompt of a run. The runtime makes its call ID from the run ID.</summary>
    protected ValueTask<ToolInteractionRequest> FishChildPromptAsync(Akka.TestKit.TestProbe subscriber, SubAgentRunId runId)
        => subscriber.FishForMessageAsync<ToolInteractionRequest>(
            request => request.CallId.Value.StartsWith($"{runId.Value}/subagent-approval/", StringComparison.Ordinal),
            Deadline, cancellationToken: Cancellation);

    /// <summary>
    /// The child prompts of several runs. The prompts can arrive in any order, and a fish drops each message that does
    /// not match, so this method keeps every prompt of the set that it sees.
    /// </summary>
    private protected async Task<IReadOnlyDictionary<SubAgentRunId, ToolInteractionRequest>> FishChildPromptsAsync(
        Akka.TestKit.TestProbe subscriber, IReadOnlyCollection<SubAgentRunId> runIds)
    {
        var prompts = new Dictionary<SubAgentRunId, ToolInteractionRequest>();
        while (prompts.Count < runIds.Count)
        {
            var request = await subscriber.FishForMessageAsync<ToolInteractionRequest>(
                candidate => runIds.Any(runId => candidate.CallId.Value.StartsWith($"{runId.Value}/subagent-approval/", StringComparison.Ordinal)),
                Deadline, cancellationToken: Cancellation);
            prompts[runIds.First(runId => request.CallId.Value.StartsWith($"{runId.Value}/subagent-approval/", StringComparison.Ordinal))] = request;
        }

        return prompts;
    }

    /// <summary>
    /// Lets the notices that a new child request causes run to their end. The barrier makes sure that a notice that the
    /// session admits for the request is in the journal. Then the method waits until each notice is consumed and the
    /// session is idle. On a session that admits no notice it returns at once.
    /// </summary>
    private protected async Task SettleAfterRequestAsync(AcceptanceFault journal, SessionId sessionId, ChildRunAccepted run)
    {
        await BarrierAsync(sessionId, run);
        await SettleNoticesAsync(journal, sessionId);
    }

    /// <summary>Every approval notice of the session, in journal order.</summary>
    private protected static IReadOnlyList<InputAdmitted> ConsentNotices(AcceptanceFault journal, SessionId sessionId)
        => journal.Committed.OfType<InputAdmitted>()
            .Where(admitted => admitted.SessionId == sessionId && admitted.TurnContext.SourceKind == NoticeKind)
            .ToArray();

    /// <summary>The admissions of a reminder fire key of the session, in journal order.</summary>
    private protected static IReadOnlyList<InputAdmitted> FireAdmissions(AcceptanceFault journal, SessionId sessionId, string fireKey)
        => journal.Committed.OfType<InputAdmitted>()
            .Where(admitted => admitted.SessionId == sessionId && admitted.SourceMessageId == fireKey)
            .ToArray();

    /// <summary>The child result notice of a run, the #2515 input with field 14.</summary>
    private protected static IReadOnlyList<InputAdmitted> ResultNoticesOf(AcceptanceFault journal, SubAgentRunId runId)
        => journal.Committed.OfType<InputAdmitted>().Where(admitted => admitted.ChildNotice?.RunId == runId).ToArray();

    /// <summary>The position of the first committed event that matches. -1 when no event matches.</summary>
    private protected static int IndexOfEvent(AcceptanceFault journal, Func<object, bool> match)
    {
        var events = journal.Committed.ToArray();
        for (var index = 0; index < events.Length; index++)
        {
            if (match(events[index]))
                return index;
        }

        return -1;
    }

    /// <summary>
    /// A barrier through the session mailbox. The session answers a status command only after it handled every earlier
    /// message and ran every pending persist callback, so a notice that the session admitted by mistake is in the
    /// journal when this method returns.
    /// </summary>
    private protected async Task BarrierAsync(SessionId sessionId, ChildRunAccepted run)
    {
        var session = await ResolveSessionAsync(sessionId);
        _ = await session.Ask<string>(new ChildRunControl(run.Run.RunId, false, run.Run.OriginalContext), Deadline, Cancellation);
    }

    // The same barrier with a run that the session does not own. The session answers with an error text, after it
    // handled every earlier message.
    private protected async Task BarrierAsync(SessionId sessionId)
    {
        var session = await ResolveSessionAsync(sessionId);
        _ = await session.Ask<string>(new ChildRunControl(new SubAgentRunId("barrier"), false,
            new TurnContextRecord { SessionId = sessionId, TurnId = "barrier" }), Deadline, Cancellation);
    }

    /// <summary>
    /// Waits until every approval notice of the session is consumed and the session is idle. Each check starts with a
    /// barrier, so a notice that the session admitted at the end of a turn is in the journal before the check. The
    /// session logs each phase change before it answers the barrier, so the phase that the check reads is current.
    /// </summary>
    private protected async Task SettleNoticesAsync(AcceptanceFault journal, SessionId sessionId)
        => await AwaitAssertAsync(async () =>
        {
            await BarrierAsync(sessionId);
            Assert.All(ConsentNotices(journal, sessionId), notice => Assert.True(ConsumedAt(journal, notice.InputId) >= 0,
                $"The notice {notice.InputId} is not consumed yet."));
            Assert.True(await IsIdleAsync(sessionId), "The session still runs a turn.");
        }, Deadline, cancellationToken: Cancellation);

    /// <summary>The model requests of the parent that carry the text of the input.</summary>
    protected IReadOnlyList<IReadOnlyList<ChatMessage>> ParentRequestsWith(string text)
        => Clients.Parent.ReceivedMessages
            .Where(request => request.Any(message => message.Role == Microsoft.Extensions.AI.ChatRole.User
                                                     && message.Text.Contains(text, StringComparison.Ordinal)))
            .ToArray();

    /// <summary>The text of the tool result of a parent call, from the model requests.</summary>
    protected string? ParentToolResult(string callId)
        => Clients.Parent.ReceivedMessages.SelectMany(Results)
            .FirstOrDefault(result => result.CallId == callId)?.Result?.ToString();

    protected static readonly ChannelDeliveryTargetInfo AliceTarget = new("signalr", "thread", "room-1", threadOrRootId: "alice-thread");
    protected static readonly ChannelDeliveryTargetInfo AliceRequestedTarget = new("signalr", "thread", "room-1", threadOrRootId: "alice-reply");
    protected static readonly ChannelDeliveryTargetInfo BobTarget = new("signalr", "thread", "room-1", threadOrRootId: "bob-thread");

    /// <summary>An operator source with delivery targets, so a spec can see which targets a notice copies.</summary>
    protected static MessageSource OperatorWithTargets(string sender, ChannelDeliveryTargetInfo defaultTarget,
        ChannelDeliveryTargetInfo? requestedTarget = null) => Operator(sender) with
    {
        DefaultDeliveryTarget = defaultTarget,
        RequestedDeliveryTarget = requestedTarget
    };

    /// <summary>
    /// A webhook input whose event type reads <c>child-consent</c>. The webhook path sets the authenticity Verified in
    /// code. The sender here is the notice sender, so only the authenticity tells the input from a notice.
    /// </summary>
    protected static MessageSource WebhookPosingAsNotice() => new()
    {
        ChannelType = ChannelType.Webhook, SenderId = new SenderId(NoticeKind),
        MessageId = "webhook-delivery-1",
        Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
        Principal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        {
            SourceKind = new SourceKind(NoticeKind)
        },
        ReceivedAt = TimeProvider.System.GetUtcNow()
    };

    /// <summary>A reminder fire key <c>{reminderId}:{fireMs}</c>.</summary>
    protected static string FireKey(string reminderId, long fireMs) => $"{reminderId}:{fireMs}";

    /// <summary>A reminder fire in this session. It mirrors the Mode B source that the reminder system builds.</summary>
    protected static MessageSource Fire(string reminderId, long fireMs) => Reminder(FireKey(reminderId, fireMs));

    protected static ReminderId ReminderIdOf(string reminderId) => new(reminderId);
}
