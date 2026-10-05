// -----------------------------------------------------------------------
// <copyright file="TelegramSessionBindingContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Telegram;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels.Contracts;

/// <summary>
/// Enrolls the real TelegramSessionBindingActor in the shared session-binding
/// contract. The fixture drives production Telegram behavior through the fake
/// Telegram bot API: prompts render through the real transport, callbacks
/// carry option-key-only callback_data and resolve by prompt message id, and
/// pending approvals recover from the journal.
///
/// Capability flags stay at their defaults: Telegram has no thread-history
/// hydration (forum topics are session-isolation keys) and no text approvals
/// or sender-reply acks (approval responses are callback-query answers).
/// </summary>
public sealed class TelegramSessionBindingContractTests(ITestOutputHelper output)
    : SessionBindingContractTests(output)
{
    private const long ChatId = 77;

    // The shared cold-binding case renders no prompt through this fixture's
    // pipeline, so the fixture seeds the real journal with a tracked prompt
    // from a "previous binding life" and maps the cold call id to it. The
    // binding under test then recovers the entry through the production
    // replay path, exactly as it would after passivation.
    private const string ColdCallId = "call-cold";
    private const int ColdPromptMessageId = 990_001;

    private readonly TelegramTransportTests.FakeTelegramBotApiClient _fake = new();
    private int _messageIdCounter = 55;
    private int _queryIdCounter;

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // The binding journals pending-approval prompts and recovers them on
        // start, so the test system needs the persistence setup the channel
        // contract tests use.
        builder.WithInMemoryJournal().WithInMemorySnapshotStore().WithNetclawSerialization();
    }

    protected override IActorRef CreateBindingActor(
        SessionId sessionId,
        RecordingSessionPipeline pipeline,
        ConfigurablePromptInjectionDetector detector)
        => SpawnBinding(sessionId, pipeline, detector);

    protected override IActorRef CreateBindingActorWithPipeline(
        SessionId sessionId,
        ISessionPipeline pipeline,
        ConfigurablePromptInjectionDetector detector)
        => SpawnBinding(sessionId, pipeline, detector);

    protected override object CreateInboundMessage(string text, string senderId)
    {
        var message = new TelegramInboundMessage(
            ChatId,
            UserId: MapSenderId(senderId),
            MessageId: Interlocked.Increment(ref _messageIdCounter),
            Text: text,
            IsDirectMessage: true);
        return new TelegramSessionInbound(
            new SessionId($"{ChatId}/chat"),
            message,
            AllowedDecision(message),
            message.Text);
    }

    protected override object CreateApprovalResponse(string callId, string selectedKey, string senderId)
    {
        // Rendered prompts map to the real Telegram message id the fake API
        // assigned. The seeded cold prompt maps to its journaled id. There is
        // no CallId in callback_data — production resolves identity through
        // the pending request recovered for that prompt message id.
        var messageId = _fake.SentKeyboards.Count > 0
            ? _fake.SentKeyboards[^1].MessageId
            : callId == ColdCallId
                ? ColdPromptMessageId
                : throw new InvalidOperationException(
                    $"No rendered Telegram prompt maps call id '{callId}'.");
        return new TelegramCallbackQuery(
            ChatId,
            UserId: MapSenderId(senderId),
            MessageId: messageId,
            QueryId: $"q-{Interlocked.Increment(ref _queryIdCounter)}",
            Data: selectedKey);
    }

    // Telegram's user-visible warning surfaces are both chat posts and
    // callback-query alerts, so the contract's posted-text view covers both.
    protected override IReadOnlyList<string> GetPostedTexts()
        => _fake.SentTexts.Select(post => post.Text)
            .Concat(_fake.AnsweredCallbacks.Select(answered => answered.Text ?? string.Empty))
            .ToList();

    // Rendered keyboards stay recorded across clears: the approval cases
    // clear posted text and then resolve the callback through the prompt's
    // message id.
    protected override void ClearPostedTexts()
    {
        _fake.SentTexts.Clear();
        _fake.AnsweredCallbacks.Clear();
    }

    protected override void SetReplyClientThrows(Exception ex)
        => _fake.StickySendMessageFailure = ex;

    protected override void SetReplyClientThrowsOnce(Exception ex)
        => _fake.SendMessageFailures.Enqueue(ex);

    protected override void ClearReplyClientThrows()
        => _fake.StickySendMessageFailure = null;

    protected override ChannelType ExpectedChannelType => ChannelType.Telegram;

    // Telegram approvals are button/callback only; ordinary text ingress is
    // never parsed as an approval reply.
    protected override bool SupportsTextApprovalResponses => false;

    // callback_data carries only the option key, so a prompt whose pending
    // entry was durably cleared fails closed as expired instead of routing.
    protected override bool SupportsApprovalAfterTurnCompleted => false;

    // --- helpers ---

    private IActorRef SpawnBinding(
        SessionId sessionId,
        ISessionPipeline pipeline,
        IPromptInjectionDetector detector)
    {
        SeedColdApprovalJournal(sessionId);

        var options = new TelegramChannelOptions
        {
            Enabled = true,
            BotToken = new SensitiveString("12345:test-token"),
            AllowedChatIds = [ChatId.ToString()],
            AllowDirectMessages = true
        };
        var transport = new TelegramTransport(
            options,
            NullLogger<TelegramTransport>.Instance,
            (_, _) => _fake);
        transport.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        var dependencies = new TelegramGatewayDependencies(
            pipeline,
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            options,
            transport,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: ToolAudienceProfileDefaults.CreateProfiles(),
            ModelCapabilities: new ModelCapabilities(),
            StorageResolver: TestSessionStorageResolver.Instance,
            ChannelRegistry: null,
            PromptInjectionDetector: detector);

        return Sys.ActorOf(TelegramSessionBindingActor.CreateProps(sessionId, new TelegramChatId(ChatId), dependencies));
    }

    private void SeedColdApprovalJournal(SessionId sessionId)
    {
        var probe = CreateTestProbe();
        var seed = Sys.ActorOf(
            Props.Create(() => new ApprovalJournalSeedActor(PersistenceIdFor(sessionId))),
            $"journal-seed-{Guid.NewGuid():N}");
        seed.Tell(new PendingApprovalPromptTracked
        {
            CallId = ColdCallId,
            RequesterSenderId = "1001",
            RequesterPrincipal = null,
            OptionKeys = [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
            PromptId = ColdPromptMessageId.ToString(CultureInfo.InvariantCulture),
            ToolName = "execute_shell",
            DisplayText = "seeded cold approval"
        }, probe.Ref);
        // The ack fires from the Persist callback, so the write is confirmed
        // before the binding under test spawns and replays the journal.
        probe.ExpectMsg<ApprovalJournalSeedActor.Seeded>();
        Sys.Stop(seed);
    }

    private static string PersistenceIdFor(SessionId sessionId)
        => $"telegram-session-binding-{Uri.EscapeDataString(sessionId.Value)}";

    private static ChannelAclDecision AllowedDecision(TelegramInboundMessage message)
        => TelegramAclPolicy.EvaluateInbound(message, new TelegramChannelOptions
        {
            Enabled = true,
            BotToken = new SensitiveString("12345:test-token"),
            AllowedChatIds = [ChatId.ToString()],
            AllowDirectMessages = true
        });

    /// <summary>
    /// The contract's synthetic sender ids ("user-1") are opaque tokens; the
    /// fixture maps each to a deterministic Telegram user id. Numeric ids
    /// pass through unchanged.
    /// </summary>
    private static long MapSenderId(string senderId)
        => long.TryParse(senderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : Fnv1a(senderId);

    private static long Fnv1a(string value)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }

            // Telegram user ids are positive; keep the mapped value positive.
            return (long)(hash & 0x7FFFFFFFFFFFFFFFUL);
        }
    }

    /// <summary>
    /// Test-only journal writer. Persists a tracked prompt under the binding's
    /// persistence id so a later spawn replays it — the durable stand-in for a
    /// prompt a previous binding life rendered.
    /// </summary>
    private sealed class ApprovalJournalSeedActor : ReceivePersistentActor
    {
        public ApprovalJournalSeedActor(string persistenceId)
        {
            PersistenceId = persistenceId;
            Command<PendingApprovalPromptTracked>(tracked => Persist(tracked, _ =>
                Sender.Tell(Seeded.Instance)));
        }

        public override string PersistenceId { get; }

        internal sealed record Seeded
        {
            public static readonly Seeded Instance = new();
        }
    }
}
