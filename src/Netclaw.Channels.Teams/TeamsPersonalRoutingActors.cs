// -----------------------------------------------------------------------
// <copyright file="TeamsPersonalRoutingActors.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
using System.Security.Cryptography;
using System.Text;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Channels;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using static Netclaw.Actors.Reminders.ReminderProtocol;
using static Netclaw.Actors.Sessions.SessionProtocol;
using LegacyProto = Netclaw.Actors.Serialization.Proto;

namespace Netclaw.Channels.Teams;

/// <summary>
/// Dependencies shared by the Teams personal conversation and binding actors.
/// </summary>
public sealed record TeamsConversationDependencies(
    TeamsChannelOptions Options,
    ISessionPipeline Pipeline,
    ITeamsReplyClient ReplyClient,
    TeamsOutputRenderer OutputRenderer,
    TimeProvider TimeProvider)
{
    /// <summary>
    /// Classifies executable Teams text before it can enter a session. A
    /// missing detector is deliberately handled as unavailable at ingress.
    /// </summary>
    public IPromptInjectionDetector? PromptInjectionDetector { get; init; }

    /// <summary>
    /// Resolves a presentation-only operator label from an already cached
    /// directory record. It must not perform network I/O.
    /// </summary>
    public Func<string, string?>? CachedOperatorLabel { get; init; }

    public ITeamsAttachmentDownloader? AttachmentDownloader { get; init; }

    public IContentScanner? ContentScanner { get; init; }

    public ToolAudienceProfiles? AudienceProfiles { get; init; }

    public ModelCapabilities? ModelCapabilities { get; init; }

    public ISessionStorageResolver? StorageResolver { get; init; }
}

internal readonly record struct TeamsApprovalPromptId(string Value);

public sealed record TeamsConversationIngress(
    TeamsInboundActivity Activity,
    CancellationToken CancellationToken) : INoSerializationVerificationNeeded;

public sealed record TeamsBindingIngress(
    TeamsInboundActivity Activity,
    CancellationToken CancellationToken,
    bool IsEstablishedThreadContinuation = false) : INoSerializationVerificationNeeded;

public sealed record TeamsBindingApprovalAction(
    TeamsApprovalAction Action,
    CancellationToken CancellationToken) : INoSerializationVerificationNeeded;

public sealed record TeamsConversationApprovalAction(
    TeamsApprovalAction Action,
    CancellationToken CancellationToken) : INoSerializationVerificationNeeded;

public sealed record TeamsConversationReminder(
    DeliverTrustedSessionTurn Reminder,
    string? KnownDestinationKey = null) : INoSerializationVerificationNeeded;

public sealed record TeamsBindingReminder(
    DeliverTrustedSessionTurn Reminder,
    string? KnownDestinationKey = null) : INoSerializationVerificationNeeded;

public enum TeamsBindingRouteDisposition
{
    Accepted,
    Duplicate,
    Ignored,
    Denied,
    Unavailable,
    Failed,
    Cancelled
}

public sealed record TeamsBindingRouteResult(TeamsBindingRouteDisposition Disposition);

public enum TeamsMigrationHealthState
{
    NotRequired,
    Pending,
    Completed,
    Failed
}

public enum TeamsProactiveHealthState
{
    Disabled,
    Unavailable,
    Available,
    CapacityPressure
}

/// <summary>
/// Provides bounded state from one binding actor. It contains only counts and
/// classification values. It never includes Teams routing or message data.
/// </summary>
public sealed record TeamsBindingProactiveDiagnostics(
    TeamsProactiveHealthState Health,
    TeamsMigrationHealthState Migration,
    int PersonalDestinationCount,
    int ChannelDestinationCount,
    int PendingDeliveryCount,
    int RetryableFailureCount,
    int PermanentFailureCount,
    int UnknownDeliveryCount,
    int RetainedDeliveryCount,
    bool HasCapacityPressure,
    string? ReasonCode = null) : INoSerializationVerificationNeeded
{
    public int TerminalDeliveryCount { get; init; }
    public int InvalidatedDestinationCount { get; init; }
    public int MissingTargetCount { get; init; }
    public int AmbiguousTargetCount { get; init; }
    public bool HasInvalidRecoveredState { get; init; }
}

public sealed record GetTeamsBindingProactiveDiagnostics : INoSerializationVerificationNeeded
{
    public static readonly GetTeamsBindingProactiveDiagnostics Instance = new();
}

/// <summary>
/// Builds actor names from a canonical Teams session ID. The codec constructs
/// the session ID before this type applies the repository actor-name escape.
/// </summary>
public static class TeamsActorNames
{
    public static string Conversation(SessionId sessionId) =>
        "teams-conversation-" + Uri.EscapeDataString(sessionId.Value);

    public static string ChannelConversation(SessionId sessionId) =>
        "teams-channel-conversation-" + Uri.EscapeDataString(sessionId.Value);

    public static string Binding(SessionId sessionId) =>
        "teams-binding-" + Uri.EscapeDataString(sessionId.Value);
}

/// <summary>
/// Resolves a personal conversation actor from a validated Teams activity.
/// This sink has no durable duplicate state and does not dispatch a session.
/// </summary>
public sealed class TeamsActorConversationIngressSink : ITeamsConversationIngressSink
{
    private static readonly TimeSpan RouteTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ApprovalAuthorizationEvidenceTtl = TimeSpan.FromMinutes(10);
    private const int MaximumApprovalAuthorizationEvidence = 1_024;

    private readonly ActorSystem _actorSystem;
    private readonly TeamsChannelOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly object _creationLock = new();
    private readonly Dictionary<string, IActorRef> _conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<TeamsApprovalAuthorizationKey, DateTimeOffset> _approvalAuthorizationEvidence = [];

    public TeamsActorConversationIngressSink(
        ActorSystem actorSystem,
        TeamsChannelOptions options,
        IServiceProvider serviceProvider)
    {
        _actorSystem = actorSystem ?? throw new ArgumentNullException(nameof(actorSystem));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public async ValueTask<TeamsIngressSinkResult> RouteAsync(
        TeamsInboundActivity activity,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return TeamsIngressSinkResult.Cancelled;

        if (!TryCreateConversationId(activity.Trust, out var conversationId))
        {
            return TeamsIngressSinkResult.Denied;
        }

        if (activity.Trust.Scope == TeamsConversationScope.Personal)
        {
            var structural = TeamsPersonalAclPolicy.EvaluateStructuralAccess(activity, _options);
            if (!structural.IsAllowed)
                return TeamsIngressSinkResult.Denied;

            var authorization = await CreateAuthorizationAsync(activity, structural, cancellationToken).ConfigureAwait(false);
            if (authorization is null)
                return TeamsIngressSinkResult.Denied;

            var result = await RoutePersonalAsync(activity.WithAuthorization(authorization), conversationId, cancellationToken);
            RememberApprovalAuthorization(activity, result);
            return result;
        }

        if (activity.Trust.Scope == TeamsConversationScope.GroupChat)
        {
            var structural = TeamsGroupChatAclPolicy.EvaluateStructuralAccess(activity, _options);
            if (!structural.IsAllowed)
            {
                return string.Equals(structural.DenyReason, "group_chat_unmentioned", StringComparison.Ordinal)
                    ? TeamsIngressSinkResult.Ignored
                    : TeamsIngressSinkResult.Denied;
            }

            var authorization = await CreateAuthorizationAsync(activity, structural, cancellationToken).ConfigureAwait(false);
            if (authorization is null)
                return TeamsIngressSinkResult.Denied;

            var result = await RoutePersonalAsync(activity.WithAuthorization(authorization), conversationId, cancellationToken);
            RememberApprovalAuthorization(activity, result);
            return result;
        }

        if (activity.Trust.Scope != TeamsConversationScope.Channel)
            return TeamsIngressSinkResult.Unavailable;

        var policy = TeamsChannelAclPolicy.EvaluateStructuralAccess(activity, _options);
        if (policy.Disposition != TeamsChannelPolicyDisposition.Allowed || policy.Acl is null)
            return TeamsIngressSinkResult.Denied;

        var channelAuthorization = await CreateAuthorizationAsync(activity, policy.Acl, cancellationToken).ConfigureAwait(false);
        if (channelAuthorization is null)
            return TeamsIngressSinkResult.Denied;

        var routed = await RouteChannelAsync(activity.WithAuthorization(channelAuthorization), conversationId, cancellationToken);
        RememberApprovalAuthorization(activity, routed);
        return routed;
    }

    public async ValueTask<TeamsApprovalActionResult> RouteApprovalAsync(
        TeamsApprovalAction action,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Cancelled);

        if (!TryCreateApprovalActivity(action, out var activity)
            || !TryCreateConversationId(action.Trust, out var conversationId))
        {
            return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Rejected);
        }

        if (action.Trust.Scope == TeamsConversationScope.Personal)
        {
            if (RequiresApprovalAuthorizationEvidence(activity))
            {
                if (!HasApprovalAuthorizationEvidence(activity))
                    return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Rejected);
            }
            else if (!TeamsPersonalAclPolicy.Evaluate(activity, _options).IsAllowed)
            {
                return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Rejected);
            }

            return await RoutePersonalApprovalAsync(action, conversationId, cancellationToken);
        }

        if (action.Trust.Scope == TeamsConversationScope.GroupChat)
        {
            var structural = TeamsGroupChatAclPolicy.EvaluateStructuralAccess(activity, _options);
            if (!structural.IsAllowed)
                return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Rejected);

            var authorization = await CreateAuthorizationAsync(activity, structural, cancellationToken).ConfigureAwait(false);
            if (authorization is null)
                return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Rejected);

            return await RoutePersonalApprovalAsync(action, conversationId, cancellationToken);
        }

        if (action.Trust.Scope != TeamsConversationScope.Channel)
        {
            return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Rejected);
        }

        // Teams omits channelData from a valid adaptiveCard/action invoke.
        // The binding validates a missing destination field against the
        // durable destination that the original approved message captured.
        if (RequiresApprovalAuthorizationEvidence(activity))
        {
            if (!HasApprovalAuthorizationEvidence(activity)
                || ((action.TeamId is not null || action.ChannelId is not null)
                    && TeamsChannelAclPolicy.EvaluateStructuralAccess(activity, _options).Disposition != TeamsChannelPolicyDisposition.Allowed))
            {
                return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Rejected);
            }
        }
        else if ((action.TeamId is not null || action.ChannelId is not null)
                 && TeamsChannelAclPolicy.EvaluateAccess(activity, _options).Disposition != TeamsChannelPolicyDisposition.Allowed)
        {
            return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Rejected);
        }

        return await RouteChannelApprovalAsync(action, conversationId, cancellationToken);
    }

    public bool TryGetReminderConversation(SessionId sessionId, out IActorRef conversation)
    {
        conversation = ActorRefs.Nobody;
        if (!TeamsSessionIdentifierCodec.TryParse(sessionId, out var identifier, out _))
            return false;

        var dependencies = CreateDependencies();
        if (identifier.Scope == TeamsConversationScope.Personal)
        {
            conversation = GetOrCreatePersonalConversation(sessionId, dependencies);
            return true;
        }

        if (identifier.Scope == TeamsConversationScope.GroupChat)
        {
            conversation = GetOrCreatePersonalConversation(sessionId, dependencies);
            return true;
        }

        if (identifier.Scope != TeamsConversationScope.Channel
            || !TeamsSessionIdentifierCodec.TryCreatePersonal(
                identifier.TenantId,
                identifier.ConversationId,
                out var conversationId,
                out _))
        {
            return false;
        }

        conversation = GetOrCreateChannelConversation(conversationId, dependencies);
        return true;
    }

    private async ValueTask<TeamsApprovalActionResult> RoutePersonalApprovalAsync(
        TeamsApprovalAction action,
        SessionId conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = GetOrCreatePersonalConversation(conversationId, CreateDependencies());
        return await AskApprovalAsync(conversation, action, cancellationToken);
    }

    private async ValueTask<TeamsApprovalActionResult> RouteChannelApprovalAsync(
        TeamsApprovalAction action,
        SessionId conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = GetOrCreateChannelConversation(conversationId, CreateDependencies());
        return await AskApprovalAsync(conversation, action, cancellationToken);
    }

    private TeamsConversationDependencies CreateDependencies() => new(
        _options,
        _serviceProvider.GetRequiredService<ISessionPipeline>(),
        _serviceProvider.GetRequiredService<ITeamsReplyClient>(),
        _serviceProvider.GetRequiredService<TeamsOutputRenderer>(),
        _serviceProvider.GetRequiredService<TimeProvider>())
    {
        PromptInjectionDetector = _serviceProvider.GetService<IPromptInjectionDetector>(),
        CachedOperatorLabel = CreateCachedOperatorLabelResolver(),
        AttachmentDownloader = _serviceProvider.GetService<ITeamsAttachmentDownloader>(),
        ContentScanner = _serviceProvider.GetService<IContentScanner>(),
        AudienceProfiles = _serviceProvider.GetService<ToolConfig>()?.AudienceProfiles,
        ModelCapabilities = _serviceProvider.GetService<ModelCapabilities>(),
        StorageResolver = _serviceProvider.GetService<ISessionStorageResolver>()
    };

    private Func<string, string?>? CreateCachedOperatorLabelResolver()
    {
        var cache = _serviceProvider.GetService<ITeamsDirectoryUserCache>();
        return cache is null ? null : userId => TryGetCachedOperatorLabel(cache, userId);
    }

    private static string? TryGetCachedOperatorLabel(ITeamsDirectoryUserCache cache, string userId)
    {
        try
        {
            if (!cache.TryGetCachedUser(userId, out var user)
                || string.IsNullOrWhiteSpace(user.UserPrincipalName))
            {
                return null;
            }

            var label = string.IsNullOrWhiteSpace(user.DisplayName)
                ? user.UserPrincipalName
                : $"{user.DisplayName} <{user.UserPrincipalName}>";
            return TeamsApprovalAction.NormalizeOperatorDisplayName(label);
        }
        catch
        {
            return null;
        }
    }

    private async ValueTask<TeamsIngressAuthorization?> CreateAuthorizationAsync(
        TeamsInboundActivity activity,
        ChannelAclDecision structural,
        CancellationToken cancellationToken)
    {
        // Minimal hosts can route Teams without Graph registration. The local
        // authorizer retains legacy and explicit-user behavior, while a group
        // rule still fails closed because it has no directory boundary.
        var authorizer = _serviceProvider.GetService<TeamsPrincipalAuthorizer>()
                         ?? new TeamsPrincipalAuthorizer(_options, directory: null);
        var decision = await authorizer.AuthorizeAsync(activity, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped(decision.ReasonCode);
            return null;
        }

        return TeamsIngressAuthorization.Create(
            activity,
            ChannelAclDecision.Allow(structural.Audience, decision.Principal, structural.Provenance));
    }

    private bool RequiresApprovalAuthorizationEvidence(TeamsInboundActivity activity)
        => TeamsPrincipalRequirements.Resolve(activity, _options).HasRestriction;

    private void RememberApprovalAuthorization(TeamsInboundActivity activity, TeamsIngressSinkResult result)
    {
        if (activity.Trust.Scope == TeamsConversationScope.GroupChat
            || result is not (TeamsIngressSinkResult.Accepted or TeamsIngressSinkResult.Duplicate)
            || !RequiresApprovalAuthorizationEvidence(activity))
        {
            return;
        }

        lock (_creationLock)
        {
            var now = _serviceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
            RemoveExpiredApprovalAuthorizationEvidence(now);
            if (_approvalAuthorizationEvidence.Count >= MaximumApprovalAuthorizationEvidence)
                _approvalAuthorizationEvidence.Remove(_approvalAuthorizationEvidence.Keys.First());

            _approvalAuthorizationEvidence[TeamsApprovalAuthorizationKey.From(activity)] = now + ApprovalAuthorizationEvidenceTtl;
        }
    }

    private bool HasApprovalAuthorizationEvidence(TeamsInboundActivity activity)
    {
        lock (_creationLock)
        {
            var now = _serviceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
            RemoveExpiredApprovalAuthorizationEvidence(now);
            return _approvalAuthorizationEvidence.TryGetValue(TeamsApprovalAuthorizationKey.From(activity), out var expiresAt)
                   && expiresAt > now;
        }
    }

    private readonly record struct TeamsApprovalAuthorizationKey(
        string SenderId,
        string TenantId,
        string ConversationId,
        TeamsConversationScope Scope)
    {
        public static TeamsApprovalAuthorizationKey From(TeamsInboundActivity activity) => new(
            activity.Trust.SenderId,
            activity.Trust.TenantId,
            activity.Trust.ConversationId,
            activity.Trust.Scope);
    }

    private void RemoveExpiredApprovalAuthorizationEvidence(DateTimeOffset now)
    {
        foreach (var (key, expiresAt) in _approvalAuthorizationEvidence.Where(entry => entry.Value <= now).ToArray())
            _approvalAuthorizationEvidence.Remove(key);
    }

    private static async ValueTask<TeamsApprovalActionResult> AskApprovalAsync(
        IActorRef conversation,
        TeamsApprovalAction action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await conversation.Ask<TeamsApprovalActionResult>(
                new TeamsConversationApprovalAction(action, cancellationToken),
                RouteTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Cancelled);
        }
        catch (AskTimeoutException)
        {
            return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Unavailable);
        }
        catch (Exception)
        {
            return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Failed);
        }
    }

    private static bool TryCreateApprovalActivity(TeamsApprovalAction action, out TeamsInboundActivity activity)
    {
        activity = null!;
        if (!TeamsApprovalAction.IsBoundedOpaqueValue(action.CorrelationId, TeamsApprovalAction.MaxCorrelationLength)
            || !TeamsApprovalAction.IsBoundedOpaqueValue(action.Nonce, TeamsApprovalAction.MaxNonceLength)
            || !TeamsApprovalAction.IsSupportedAction(action.Action)
            || !TeamsOutboundDestination.IsValidServiceUrl(action.ServiceUrl))
        {
            return false;
        }

        try
        {
            activity = new TeamsInboundActivity(
                action.Trust,
                string.Empty,
                new TeamsReplyMetadata(null, action.RootActivityId, action.ServiceUrl),
                isMentioned: true,
                kind: TeamsIngressActivityKind.AdaptiveCardAction,
                teamId: action.TeamId,
                channelId: action.ChannelId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async ValueTask<TeamsIngressSinkResult> RoutePersonalAsync(
        TeamsInboundActivity activity,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var conversation = GetOrCreatePersonalConversation(sessionId, CreateDependencies());
        try
        {
            var result = await conversation.Ask<TeamsBindingRouteResult>(
                new TeamsConversationIngress(activity, cancellationToken),
                TeamsIngressTimeouts.ConversationRoute(activity),
                cancellationToken);
            return result.Disposition switch
            {
                TeamsBindingRouteDisposition.Accepted => TeamsIngressSinkResult.Accepted,
                TeamsBindingRouteDisposition.Duplicate => TeamsIngressSinkResult.Duplicate,
                TeamsBindingRouteDisposition.Denied => TeamsIngressSinkResult.Denied,
                TeamsBindingRouteDisposition.Unavailable => TeamsIngressSinkResult.Unavailable,
                TeamsBindingRouteDisposition.Cancelled => TeamsIngressSinkResult.Cancelled,
                TeamsBindingRouteDisposition.Failed => TeamsIngressSinkResult.Failed,
                _ => throw new ArgumentOutOfRangeException()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TeamsIngressSinkResult.Cancelled;
        }
        catch (AskTimeoutException)
        {
            return TeamsIngressSinkResult.Unavailable;
        }
        catch (Exception)
        {
            return TeamsIngressSinkResult.Failed;
        }
    }

    private async ValueTask<TeamsIngressSinkResult> RouteChannelAsync(
        TeamsInboundActivity activity,
        SessionId conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = GetOrCreateChannelConversation(conversationId, CreateDependencies());
        try
        {
            var result = await conversation.Ask<TeamsBindingRouteResult>(
                new TeamsConversationIngress(activity, cancellationToken),
                TeamsIngressTimeouts.ConversationRoute(activity),
                cancellationToken);
            return result.Disposition switch
            {
                TeamsBindingRouteDisposition.Accepted => TeamsIngressSinkResult.Accepted,
                TeamsBindingRouteDisposition.Duplicate => TeamsIngressSinkResult.Duplicate,
                TeamsBindingRouteDisposition.Ignored => TeamsIngressSinkResult.Ignored,
                TeamsBindingRouteDisposition.Denied => TeamsIngressSinkResult.Denied,
                TeamsBindingRouteDisposition.Unavailable => TeamsIngressSinkResult.Unavailable,
                TeamsBindingRouteDisposition.Cancelled => TeamsIngressSinkResult.Cancelled,
                TeamsBindingRouteDisposition.Failed => TeamsIngressSinkResult.Failed,
                _ => throw new ArgumentOutOfRangeException()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TeamsIngressSinkResult.Cancelled;
        }
        catch (AskTimeoutException)
        {
            return TeamsIngressSinkResult.Unavailable;
        }
        catch (Exception)
        {
            return TeamsIngressSinkResult.Failed;
        }
    }

    private IActorRef GetOrCreatePersonalConversation(
        SessionId sessionId,
        TeamsConversationDependencies dependencies)
    {
        lock (_creationLock)
        {
            if (_conversations.TryGetValue(sessionId.Value, out var existing))
                return existing;

            var actor = _actorSystem.ActorOf(
                TeamsPersonalConversationActor.CreateProps(sessionId, dependencies),
                TeamsActorNames.Conversation(sessionId));
            _conversations.Add(sessionId.Value, actor);
            return actor;
        }
    }

    private IActorRef GetOrCreateChannelConversation(
        SessionId conversationId,
        TeamsConversationDependencies dependencies)
    {
        lock (_creationLock)
        {
            var key = "channel:" + conversationId.Value;
            if (_conversations.TryGetValue(key, out var existing))
                return existing;

            var actor = _actorSystem.ActorOf(
                TeamsConversationActor.CreateProps(conversationId, dependencies),
                TeamsActorNames.ChannelConversation(conversationId));
            _conversations.Add(key, actor);
            return actor;
        }
    }

    private static bool TryCreateConversationId(TeamsIngressTrustContext trust, out SessionId conversationId)
        => trust.Scope switch
        {
            TeamsConversationScope.Personal => TeamsSessionIdentifierCodec.TryCreatePersonal(
                trust.TenantId,
                trust.ConversationId,
                out conversationId,
                out _),
            TeamsConversationScope.GroupChat => TeamsSessionIdentifierCodec.TryCreateGroupChat(
                trust.TenantId,
                trust.ConversationId,
                out conversationId,
                out _),
            TeamsConversationScope.Channel => TeamsSessionIdentifierCodec.TryCreatePersonal(
                trust.TenantId,
                trust.ConversationId,
                out conversationId,
                out _),
            _ => SetInvalidConversation(out conversationId)
        };

    private static bool SetInvalidConversation(out SessionId conversationId)
    {
        conversationId = default;
        return false;
    }
}

/// <summary>
/// Owns deterministic binding-child lookup for one canonical Teams Personal
/// or GroupChat conversation. It owns no pipeline work or durable duplicate state.
/// </summary>
public sealed class TeamsPersonalConversationActor : ReceiveActor
{
    private static readonly TimeSpan BindingRouteTimeout = TimeSpan.FromSeconds(10);

    private readonly SessionId _sessionId;
    private readonly TeamsConversationDependencies _dependencies;
    private readonly ILoggingAdapter _log;

    public TeamsPersonalConversationActor(SessionId sessionId, TeamsConversationDependencies dependencies)
    {
        _sessionId = sessionId;
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        _log = Context.GetLogger().WithContext("Adapter", "teams");

        ReceiveAsync<TeamsConversationIngress>(HandleIngressAsync);
        ReceiveAsync<TeamsConversationApprovalAction>(HandleApprovalAsync);
        Receive<TeamsConversationReminder>(HandleReminder);
    }

    public static Props CreateProps(SessionId sessionId, TeamsConversationDependencies dependencies) =>
        Props.Create(() => new TeamsPersonalConversationActor(sessionId, dependencies));

    private async Task HandleIngressAsync(TeamsConversationIngress ingress)
    {
        var replyTo = Sender;
        if (ingress.CancellationToken.IsCancellationRequested)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Cancelled));
            return;
        }

        if (!TryCreateFlatSessionId(ingress.Activity.Trust, out var expectedSessionId)
            || expectedSessionId != _sessionId)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Unavailable));
            return;
        }

        if (TeamsActorAclEvaluator.Evaluate(ingress.Activity, _dependencies.Options) is null)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("flat_conversation_acl_denied");
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Denied));
            return;
        }

        var binding = Context.Child(TeamsActorNames.Binding(_sessionId));
        if (binding.IsNobody())
        {
            binding = Context.ActorOf(
                TeamsSessionBindingActor.CreateProps(_sessionId, _dependencies),
                TeamsActorNames.Binding(_sessionId));
            _log.Info("flat_conversation_binding_created");
        }

        try
        {
            var result = await binding.Ask<TeamsBindingRouteResult>(
                new TeamsBindingIngress(ingress.Activity, ingress.CancellationToken),
                TeamsIngressTimeouts.BindingRoute(ingress.Activity),
                ingress.CancellationToken);
            replyTo.Tell(result);
        }
        catch (OperationCanceledException) when (ingress.CancellationToken.IsCancellationRequested)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Cancelled));
        }
        catch (AskTimeoutException)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Unavailable));
        }
        catch (Exception)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Failed));
        }
    }

    private async Task HandleApprovalAsync(TeamsConversationApprovalAction action)
    {
        var replyTo = Sender;
        if (action.CancellationToken.IsCancellationRequested
            || !TryCreateFlatSessionId(action.Action.Trust, out var expectedSessionId)
            || expectedSessionId != _sessionId)
        {
            replyTo.Tell(new TeamsApprovalActionResult(action.CancellationToken.IsCancellationRequested
                ? TeamsApprovalActionDisposition.Cancelled
                : TeamsApprovalActionDisposition.Rejected));
            return;
        }

        var binding = Context.Child(TeamsActorNames.Binding(_sessionId));
        if (binding.IsNobody())
        {
            binding = Context.ActorOf(
                TeamsSessionBindingActor.CreateProps(_sessionId, _dependencies),
                TeamsActorNames.Binding(_sessionId));
        }

        try
        {
            replyTo.Tell(await binding.Ask<TeamsApprovalActionResult>(
                new TeamsBindingApprovalAction(action.Action, action.CancellationToken),
                BindingRouteTimeout,
                action.CancellationToken));
        }
        catch (OperationCanceledException) when (action.CancellationToken.IsCancellationRequested)
        {
            replyTo.Tell(new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Cancelled));
        }
        catch (AskTimeoutException)
        {
            replyTo.Tell(new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Unavailable));
        }
        catch (Exception)
        {
            replyTo.Tell(new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Failed));
        }
    }

    private void HandleReminder(TeamsConversationReminder reminder)
    {
        if (reminder.Reminder.SessionId != _sessionId)
        {
            Sender.Tell(CommandNack.For(reminder.Reminder.SessionId, "Teams session does not match the flat conversation."));
            return;
        }

        var binding = Context.Child(TeamsActorNames.Binding(_sessionId));
        if (binding.IsNobody())
        {
            binding = Context.ActorOf(
                TeamsSessionBindingActor.CreateProps(_sessionId, _dependencies),
                TeamsActorNames.Binding(_sessionId));
        }

        binding.Forward(new TeamsBindingReminder(reminder.Reminder, reminder.KnownDestinationKey));
    }

    private static bool TryCreateFlatSessionId(TeamsIngressTrustContext trust, out SessionId sessionId)
        => trust.Scope switch
        {
            TeamsConversationScope.Personal => TeamsSessionIdentifierCodec.TryCreatePersonal(
                trust.TenantId,
                trust.ConversationId,
                out sessionId,
                out _),
            TeamsConversationScope.GroupChat => TeamsSessionIdentifierCodec.TryCreateGroupChat(
                trust.TenantId,
                trust.ConversationId,
                out sessionId,
                out _),
            _ => SetInvalidSession(out sessionId)
        };

    private static bool SetInvalidSession(out SessionId sessionId)
    {
        sessionId = default;
        return false;
    }
}

/// <summary>
/// Owns a flat Teams session pipeline and durable activity duplicate
/// history. A durable reservation occurs before local queue admission.
/// </summary>
public sealed class TeamsSessionBindingActor : ReceivePersistentActor
{
    internal const int ProcessedActivityCapacity = 1_024;

    internal const int ApprovalCapacity = 128;

    internal const int ProactiveDeliveryCapacity = 1_024;

    private const long SnapshotInterval = 64;

    private static readonly TimeSpan ApprovalOperationTimeout = TimeSpan.FromSeconds(10);

    private readonly SessionId _sessionId;
    private readonly TeamsConversationDependencies _dependencies;
    private readonly SessionPipelineHandle _pipelineHandle;
    private readonly ILoggingAdapter _log;
    private readonly bool _isChannelBinding;
    private readonly bool _isGroupChatBinding;
    private readonly HashSet<string> _processedActivityIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _processedActivityOrder = new();
    private readonly Dictionary<string, TeamsPendingApproval> _pendingApprovals = new(StringComparer.Ordinal);
    private readonly List<PendingApprovalRequest<TeamsApprovalPromptId>> _pendingApprovalRequests = [];
    private readonly List<PendingApprovalRequest<TeamsApprovalPromptId>> _outputEnginePendingRequests = [];
    private readonly ApprovalResponseFlow<PendingApprovalRequest<TeamsApprovalPromptId>, TeamsApprovalPromptId> _approvalFlow;
    private readonly ChannelOutputEngine<PendingApprovalRequest<TeamsApprovalPromptId>, TeamsApprovalPromptId> _outputEngine;
    private readonly SafeTransportCall _safeTransportCall;
    private readonly Dictionary<string, TeamsProactiveDeliveryState> _proactiveDeliveries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _proactiveDeliveryGenerations = new(StringComparer.Ordinal);
    private readonly Queue<string> _proactiveDeliveryOrder = new();
    private readonly Dictionary<string, IActorRef> _reminderDeliveryObservers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reminderTextDelivered = new(StringComparer.Ordinal);
    private TurnNumber _lastCompletedTurnNumber;
    private TeamsOutboundDestination? _destination;
    private long _destinationGeneration;
    private bool _requiresMigrationSnapshot;
    private bool _migrationSnapshotSaveInFlight;
    private bool _migrationSnapshotFailed;

    public TeamsSessionBindingActor(SessionId sessionId, TeamsConversationDependencies dependencies)
    {
        _sessionId = sessionId;
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        _log = Context.GetLogger().WithContext("Adapter", "teams");
        _isChannelBinding = TeamsSessionIdentifierCodec.TryParse(_sessionId, out var identifier, out _)
                            && identifier.Scope == TeamsConversationScope.Channel;
        _isGroupChatBinding = TeamsSessionIdentifierCodec.TryParse(_sessionId, out identifier, out _)
                              && identifier.Scope == TeamsConversationScope.GroupChat;
        _pipelineHandle = new SessionPipelineHandle(
            _dependencies.Pipeline,
            _log,
            _isChannelBinding
                ? "teams-channel"
                : _isGroupChatBinding
                    ? "teams-groupchat"
                    : "teams-personal");
        _approvalFlow = new ApprovalResponseFlow<PendingApprovalRequest<TeamsApprovalPromptId>, TeamsApprovalPromptId>(
            sessionId: _sessionId,
            channelType: ChannelType.Teams,
            channelName: "Teams",
            pipeline: _dependencies.Pipeline,
            operationTimeout: ApprovalOperationTimeout,
            pendingRequests: _pendingApprovalRequests,
            hasObservedApprovalRequest: () => _pendingApprovalRequests.Count > 0,
            postWrongRequesterWarningAsync: () => Task.CompletedTask,
            // Teams journals its opaque transport decision independently. The
            // shared flow still removes its recovered request only after the
            // session acknowledges the exact selected option.
            persistPromptCleared: _ => { },
            renderResolvedPromptAsync: (_, _, _, _, _, _, _) => Task.CompletedTask,
            log: _log);
        _safeTransportCall = new SafeTransportCall(
            ChannelType.Teams,
            _dependencies.TimeProvider,
            NotifyDeliveryFailedAsync);
        _outputEngine = new ChannelOutputEngine<PendingApprovalRequest<TeamsApprovalPromptId>, TeamsApprovalPromptId>(
            channelType: ChannelType.Teams,
            channelName: "Teams",
            // Teams has no ordered, safely hydrated message history. Its
            // transport-owned output does not persist a replay cursor.
            cursorComparer: StringComparer.Ordinal,
            pendingRequests: _outputEnginePendingRequests,
            createPendingRequest: request => new PendingApprovalRequest<TeamsApprovalPromptId>(request),
            // Adaptive Cards require Teams' durable nonce/correlation model, so
            // they stay in the Teams transport path below.
            isApprovalRequest: _ => false,
            renderTextOutput: textOutput => textOutput.Text,
            renderErrorOutput: errorOutput => $"Warning: {errorOutput.Message}",
            postTextAsync: PostTextAsync,
            uploadFileAsync: FailUnsupportedFileOutputAsync,
            postApprovalPromptAsync: _ => Task.FromResult<TeamsApprovalPromptId?>(null),
            readPromptIdValue: promptId => promptId.Value,
            onApprovalPromptFailedAsync: _ => Task.CompletedTask,
            persistPromptTracked: _ => { },
            handleChannelSpecificOutputAsync: HandleChannelSpecificOutputAsync,
            advanceCursor: _ => { },
            postEmptyTurnFallbackAsync: () => Task.CompletedTask,
            onEmptyTurnSuppressedAsync: _ => Task.CompletedTask,
            readObservedAtMs: output => output.TimestampMs);

        Recover<DurableActivityDispatchReserved>(ApplyReserved);
        Recover<DurableActivityDispatchReleased>(ApplyReleased);
        Recover<TeamsApprovalPendingCreated>(ApplyApprovalPendingCreated);
        Recover<TeamsApprovalCardDelivered>(ApplyApprovalCardDelivered);
        Recover<TeamsApprovalCardReissued>(ApplyApprovalCardReissued);
        Recover<TeamsApprovalForwardingStarted>(ApplyApprovalForwardingStarted);
        Recover<TeamsApprovalConsumed>(ApplyApprovalConsumed);
        Recover<TeamsProactiveDestinationCaptured>(ApplyDestinationCaptured);
        Recover<TeamsProactiveDeliveryRecorded>(ApplyProactiveDeliveryRecorded);
        Recover<LegacyChannelPersistenceEnvelope>(ApplyLegacyPersistence);
        Recover<RecoveryCompleted>(_ =>
        {
            Self.Tell(new MarkRecoveredProactiveDeliveriesUnknown());
            Self.Tell(new RecoverPendingApprovalForwardsCommand());
            Self.Tell(new RecoverPendingApprovalPresentationsCommand());
            if (_requiresMigrationSnapshot)
                Self.Tell(new SaveTeamsMigrationSnapshot());
        });
        Recover<SnapshotOffer>(offer =>
        {
            if (offer.Snapshot is TeamsBindingSnapshot snapshot)
                ApplySnapshot(snapshot);
            else if (offer.Snapshot is DurableActivityDispatchSnapshot legacySnapshot)
                ApplySnapshot(new TeamsBindingSnapshot(legacySnapshot.ActivityFingerprints));
            else if (offer.Snapshot is LegacyChannelPersistenceEnvelope legacyEnvelope)
                ApplyLegacyPersistence(legacyEnvelope);
        });

        Context.SetReceiveTimeout(TimeSpan.FromHours(1));
        Command<ReceiveTimeout>(_ => Context.Stop(Self));
        CommandAsync<TeamsBindingIngress>(HandleIngressAsync);
        CommandAsync<TeamsBindingReminder>(HandleReminderAsync);
        Command<GetTeamsBindingProactiveDiagnostics>(_ => Sender.Tell(CreateProactiveDiagnostics()));
        CommandAsync<TeamsBindingApprovalAction>(HandleApprovalActionAsync);
        CommandAsync<ForwardTeamsApprovalDecision>(ForwardApprovalDecisionAsync);
        Command<RecoverPendingApprovalForwardsCommand>(_ => RecoverPendingApprovalForwards());
        Command<RecoverPendingApprovalPresentationsCommand>(_ => RecoverPendingApprovalPresentations());
        CommandAsync<DenyTeamsApprovalRequest>(DenyApprovalRequestAsync);
        CommandAsync<DispatchReservedActivity>(DispatchReservedActivityAsync);
        Command<ReleaseReservedActivity>(release =>
            ReleaseReservation(release.Dispatch, release.Disposition));
        Command<BeginTeamsReminderDispatch>(BeginReminderDispatch);
        CommandAsync<DispatchTeamsReminder>(DispatchReminderAsync);
        Command<CompleteReminderDispatchFailure>(failure =>
        {
            CompleteReminderFailure(failure.DeliveryKey, failure.FailureReason);
            failure.ReplyTo.Tell(CommandNack.For(_sessionId, failure.ReplyMessage));
        });
        Command<MarkRecoveredProactiveDeliveriesUnknown>(_ => MarkRecoveredDeliveriesUnknown());
        Command<SaveTeamsMigrationSnapshot>(_ => SaveMigrationSnapshot());
        CommandAsync<BindingOutput>(HandleOutputAsync);
        CommandAsync<DeliverTeamsApprovalCard>(DeliverApprovalCardAsync);
        Command<OutputStreamTerminated>(terminated =>
        {
            if (terminated.Generation == _pipelineHandle.Generation)
                Self.Tell(new ReinitializePipeline());
        });
        CommandAsync<ReinitializePipeline>(async _ =>
        {
            await _pipelineHandle.ReinitializeAsync(
                _isChannelBinding
                    ? "Teams channel pipeline output terminated"
                    : _isGroupChatBinding
                        ? "Teams group chat pipeline output terminated"
                        : "Teams personal pipeline output terminated",
                () => ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped(
                    _isChannelBinding
                        ? "channel_pipeline_reinitialize_failed"
                        : _isGroupChatBinding
                            ? "group_chat_pipeline_reinitialize_failed"
                            : "personal_pipeline_reinitialize_failed"));
        });
        Command<SaveSnapshotSuccess>(saved =>
        {
            if (_migrationSnapshotSaveInFlight)
            {
                _migrationSnapshotSaveInFlight = false;
                _migrationSnapshotFailed = false;
                _requiresMigrationSnapshot = false;
            }

            DeleteMessages(saved.Metadata.SequenceNr);
            if (saved.Metadata.SequenceNr > 1)
                DeleteSnapshots(new SnapshotSelectionCriteria(saved.Metadata.SequenceNr - 1));
        });
        Command<SaveSnapshotFailure>(_ =>
        {
            if (_migrationSnapshotSaveInFlight)
            {
                _migrationSnapshotSaveInFlight = false;
                _migrationSnapshotFailed = true;
                ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("teams_migration_snapshot_failed");
                return;
            }

            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("personal_processed_state_snapshot_failed");
        });
    }

    public override string PersistenceId =>
        (TeamsSessionIdentifierCodec.TryParse(_sessionId, out var identifier, out _)
         && identifier.Scope == TeamsConversationScope.Channel
            ? "teams-channel-binding-"
            : identifier.Scope == TeamsConversationScope.GroupChat
                ? "teams-groupchat-binding-"
                : "teams-personal-binding-") + Uri.EscapeDataString(_sessionId.Value);

    public static Props CreateProps(SessionId sessionId, TeamsConversationDependencies dependencies) =>
        Props.Create(() => new TeamsSessionBindingActor(sessionId, dependencies));

    protected override void PostStop()
    {
        _pipelineHandle.Dispose();
        base.PostStop();
    }

    private async Task HandleIngressAsync(TeamsBindingIngress ingress)
    {
        var replyTo = Sender;
        if (ingress.CancellationToken.IsCancellationRequested)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Cancelled));
            return;
        }

        if (!TryGetExpectedSessionId(ingress.Activity, out var expectedSessionId)
            || expectedSessionId != _sessionId)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Unavailable));
            return;
        }

        var acl = EvaluateAcl(ingress.Activity);
        if (acl is null)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped(
                _isChannelBinding
                    ? "channel_acl_denied"
                    : _isGroupChatBinding
                        ? "group_chat_acl_denied"
                        : "personal_acl_denied");
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Denied));
            return;
        }

        if (!IsAuthorizedMentionOnlyIngress(ingress))
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered(
                _isGroupChatBinding
                    ? "group_chat_unmentioned"
                    : "channel_unmentioned_not_established_owner");
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Ignored));
            return;
        }

        if (_dependencies.PromptInjectionDetector is null)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("prompt_injection_detector_unavailable");
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Unavailable));
            return;
        }

        var classification = await PromptClassifier.ClassifyAsync(
            _dependencies.PromptInjectionDetector,
            ingress.Activity.Text,
            _isChannelBinding
                ? "teams-channel"
                : _isGroupChatBinding
                    ? "teams-groupchat"
                    : "teams-personal",
            _log,
            ingress.CancellationToken);
        if (classification.Outcome == ClassificationOutcome.Block)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("prompt_injection_blocked");
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Denied));
            return;
        }

        if (classification.Outcome == ClassificationOutcome.DetectorUnavailable)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("prompt_injection_detector_unavailable");
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Unavailable));
            return;
        }

        var activityFingerprint = ActivityFingerprint.Create(ingress.Activity.Trust.ActivityId);
        if (_processedActivityIds.Contains(activityFingerprint))
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("durable_activity_duplicate");
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Duplicate));
            return;
        }

        TeamsOutboundDestination destination;
        try
        {
            destination = CreateDestination(ingress.Activity);
        }
        catch (ArgumentException)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Denied));
            return;
        }
        catch (InvalidOperationException)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Denied));
            return;
        }

        if (!Equals(_destination, destination))
        {
            var generation = _destinationGeneration == 0 ? 1 : checked(_destinationGeneration + 1);
            Persist(ToCapturedDestination(destination, generation), captured =>
            {
                ApplyDestinationCaptured(captured);
                RecordDestinationTelemetry("proactive_destination_captured");
                RecoverPendingApprovalPresentations();
                ReserveIngress(ingress, activityFingerprint, replyTo);
            });
            return;
        }

        ReserveIngress(ingress, activityFingerprint, replyTo);
    }

    private void ReserveIngress(
        TeamsBindingIngress ingress,
        string activityFingerprint,
        IActorRef replyTo)
    {
        var evictedActivityFingerprint = _processedActivityOrder.Count == ProcessedActivityCapacity
            ? _processedActivityOrder.Peek()
            : null;
        Persist(
            new DurableActivityDispatchReserved(activityFingerprint, evictedActivityFingerprint),
            reserved =>
            {
                ApplyReserved(reserved);
                SaveSnapshotWhenDue();
                Self.Tell(new DispatchReservedActivity(
                    ingress.Activity,
                    activityFingerprint,
                    replyTo,
                    ingress.CancellationToken));
            });
    }

    private async Task DispatchReservedActivityAsync(DispatchReservedActivity dispatch)
    {
        // CommandAsync continuations run outside Akka's ambient actor context.
        // Attachment ingress performs real asynchronous I/O, so capture the
        // context-bound values before its first await and return durable failure
        // handling to the mailbox below.
        var context = Context;
        var self = Self;

        try
        {
            var input = await BuildChannelInputAsync(dispatch.Activity, dispatch.CancellationToken).ConfigureAwait(false);
            if (input is null)
            {
                ChannelTelemetry.For(ChannelType.Teams).RecordExtra("attachment_only_rejected");
                dispatch.ReplyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Accepted));
                return;
            }

            var writer = await EnsurePipelineAsync(context, self, dispatch.CancellationToken);
            await writer.WriteAsync(input, dispatch.CancellationToken);
            ChannelTelemetry.For(ChannelType.Teams).RecordMessageEnqueued();
            ChannelTelemetry.For(ChannelType.Teams).RecordEventRouted(
                _isChannelBinding
                    ? "channel_binding"
                    : _isGroupChatBinding
                        ? "group_chat_binding"
                        : "personal_binding");
            dispatch.ReplyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Accepted));
        }
        catch (OperationCanceledException) when (dispatch.CancellationToken.IsCancellationRequested)
        {
            self.Tell(new ReleaseReservedActivity(dispatch, TeamsBindingRouteDisposition.Cancelled));
        }
        catch (ChannelClosedException)
        {
            self.Tell(new ReleaseReservedActivity(dispatch, TeamsBindingRouteDisposition.Failed));
        }
        catch (Exception)
        {
            self.Tell(new ReleaseReservedActivity(dispatch, TeamsBindingRouteDisposition.Failed));
        }
    }

    private async Task HandleReminderAsync(TeamsBindingReminder received)
    {
        var reminder = received.Reminder;
        var replyTo = Sender;
        if (reminder.SessionId != _sessionId
            || reminder.Source.ReminderId is not { } reminderId
            || string.IsNullOrWhiteSpace(reminderId.Value))
        {
            replyTo.Tell(CommandNack.For(reminder.SessionId, "Teams reminder delivery is invalid."));
            return;
        }

        var destinationResolution = ResolveReminderDestination(received.KnownDestinationKey);
        if (destinationResolution.Disposition != TeamsDestinationResolutionDisposition.Resolved)
        {
            RecordDestinationTelemetry(destinationResolution.ReasonCode ?? "proactive_destination_missing");
            replyTo.Tell(CommandNack.For(_sessionId, "Teams proactive destination is unavailable."));
            return;
        }

        var deliveryKey = reminderId.Value;
        if (_proactiveDeliveries.TryGetValue(deliveryKey, out var state))
        {
            if (state == TeamsProactiveDeliveryState.Sent)
            {
                replyTo.Tell(CommandAck.For(_sessionId));
                if (reminder.Source.DeliveryObserver is { } observer)
                    observer.Tell(new ReminderDeliveryResult(reminderId, ChannelType.Teams, true,
                        ObservedAtMs: _dependencies.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
                return;
            }

            if (state is TeamsProactiveDeliveryState.Sending or TeamsProactiveDeliveryState.DeliveryUnknown or TeamsProactiveDeliveryState.FailedPermanent)
            {
                replyTo.Tell(CommandNack.For(_sessionId, "Teams reminder delivery requires operator review."));
                return;
            }

            // Pending is safe to resume because no send attempt was recorded.
            // FailedRetryable is retried only when the generic reminder system
            // redelivers this same stable delivery key.
            Self.Tell(new BeginTeamsReminderDispatch(reminder, replyTo, deliveryKey));
            return;
        }

        if (_proactiveDeliveryOrder.Count >= ProactiveDeliveryCapacity)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("proactive_delivery_capacity_reached");
            replyTo.Tell(CommandNack.For(_sessionId, "Teams proactive delivery capacity is reached."));
            return;
        }

        Persist(new TeamsProactiveDeliveryRecorded
        {
            DeliveryKey = deliveryKey,
            State = (int)TeamsProactiveDeliveryState.Pending,
            DestinationGeneration = _destinationGeneration
        }, recorded =>
        {
            ApplyProactiveDeliveryRecorded(recorded);
            Self.Tell(new BeginTeamsReminderDispatch(reminder, replyTo, deliveryKey));
        });
    }

    private void BeginReminderDispatch(BeginTeamsReminderDispatch dispatch)
    {
        if (!_proactiveDeliveries.TryGetValue(dispatch.DeliveryKey, out var state)
            || state is not (TeamsProactiveDeliveryState.Pending or TeamsProactiveDeliveryState.FailedRetryable))
        {
            return;
        }

        Persist(new TeamsProactiveDeliveryRecorded
        {
            DeliveryKey = dispatch.DeliveryKey,
            State = (int)TeamsProactiveDeliveryState.Sending,
            DestinationGeneration = _proactiveDeliveryGenerations.GetValueOrDefault(dispatch.DeliveryKey, _destinationGeneration)
        }, recorded =>
        {
            ApplyProactiveDeliveryRecorded(recorded);
            Self.Tell(new DispatchTeamsReminder(dispatch.Reminder, dispatch.ReplyTo, dispatch.DeliveryKey));
        });
    }

    private async Task DispatchReminderAsync(DispatchTeamsReminder dispatch)
    {
        var context = Context;
        var self = Self;

        try
        {
            var writer = await EnsurePipelineAsync(context, self, CancellationToken.None);
            var source = dispatch.Reminder.Source with { AckTarget = dispatch.ReplyTo };
            if (source.DeliveryObserver is { } observer)
                _reminderDeliveryObservers[dispatch.DeliveryKey] = observer;

            await writer.WriteAsync(new ChannelInput
            {
                SenderId = source.SenderId,
                ChannelId = _destination!.ConversationId,
                MessageId = source.MessageId,
                Audience = source.Audience,
                Boundary = source.Boundary,
                Principal = source.Principal,
                Provenance = source.Provenance,
                Contents = [new TextContent(dispatch.Reminder.Content)],
                ReceivedAt = _dependencies.TimeProvider.GetUtcNow(),
                ExecutableText = dispatch.Reminder.Content,
                ReminderId = source.ReminderId,
                AckTarget = source.AckTarget
            }, CancellationToken.None);
            ChannelTelemetry.For(ChannelType.Teams).RecordExtra("proactive_delivery_attempted");
        }
        catch (ChannelClosedException)
        {
            self.Tell(new CompleteReminderDispatchFailure(
                dispatch.DeliveryKey,
                dispatch.ReplyTo,
                "Teams pipeline is unavailable.",
                "Teams pipeline is unavailable."));
        }
        catch (Exception)
        {
            self.Tell(new CompleteReminderDispatchFailure(
                dispatch.DeliveryKey,
                dispatch.ReplyTo,
                "Teams pipeline dispatch failed.",
                "Teams pipeline dispatch failed."));
        }
    }

    private void ReleaseReservation(
        DispatchReservedActivity dispatch,
        TeamsBindingRouteDisposition disposition)
    {
        Persist(
            new DurableActivityDispatchReleased(dispatch.ActivityFingerprint),
            released =>
            {
                ApplyReleased(released);
                SaveSnapshotWhenDue();
                ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped(
                    disposition == TeamsBindingRouteDisposition.Cancelled
                        ? "pipeline_dispatch_cancelled"
                        : "pipeline_dispatch_failed");
                dispatch.ReplyTo.Tell(new TeamsBindingRouteResult(disposition));
            });
    }

    private async Task<ChannelWriter<ChannelInput>> EnsurePipelineAsync(
        IActorContext context,
        IActorRef self,
        CancellationToken cancellationToken)
    {
        if (_pipelineHandle.InputQueue is { } writer)
            return writer;

        return await _pipelineHandle.InitializeWithChannelAsync(
            context,
            _sessionId,
            new SessionPipelineOptions
            {
                ChannelType = ChannelType.Teams,
                Filter = OutputFilter.Text | OutputFilter.ProcessingState
            },
            output => self.Tell(new BindingOutput(output)),
            (generation, cause) => self.Tell(new OutputStreamTerminated(generation, cause)),
            cancellationToken);
    }

    private async Task<ChannelInput?> BuildChannelInputAsync(
        TeamsInboundActivity activity,
        CancellationToken cancellationToken)
    {
        var acl = EvaluateAcl(activity) ?? throw new InvalidOperationException("The Teams activity is not authorized for dispatch.");
        var sourceScope = activity.Trust.Scope switch
        {
            TeamsConversationScope.Personal => "teams-personal",
            TeamsConversationScope.GroupChat => "teams-groupchat",
            TeamsConversationScope.Channel => "teams-channel",
            _ => throw new InvalidOperationException("The Teams activity has an unsupported scope.")
        };
        var boundary = activity.Trust.Scope switch
        {
            TeamsConversationScope.Personal => TrustBoundary.Personal,
            TeamsConversationScope.GroupChat => TrustBoundary.Team,
            TeamsConversationScope.Channel => TrustBoundary.Public,
            _ => throw new InvalidOperationException("The Teams activity has an unsupported scope.")
        };
        var hasText = !string.IsNullOrWhiteSpace(activity.Text);
        var contents = new List<AIContent>();
        if (hasText)
            contents.Add(new TextContent(activity.Text));

        var hasAcceptedAttachment = await ProcessInboundAttachmentsAsync(
            activity,
            acl.Audience,
            contents,
            cancellationToken).ConfigureAwait(false);
        if (!hasText && !hasAcceptedAttachment)
            return null;

        return new ChannelInput
        {
        SenderId = new SenderId(activity.Trust.SenderId),
        ChannelId = activity.Trust.ConversationId,
        MessageId = activity.Trust.ActivityId,
        Audience = acl.Audience,
        Boundary = boundary,
        Principal = acl.Principal,
        Provenance = acl.Provenance with { SourceScope = new SourceScope(sourceScope) },
        Contents = contents,
        ReceivedAt = activity.Trust.ReceivedAtUtc,
        ExecutableText = activity.Text
        };
    }

    private async Task<bool> ProcessInboundAttachmentsAsync(
        TeamsInboundActivity activity,
        TrustAudience audience,
        List<AIContent> contents,
        CancellationToken cancellationToken)
    {
        if (activity.Attachments.Length == 0)
            return false;

        if (!_dependencies.Options.AllowAttachments)
        {
            await SendAttachmentRejectionAsync("Attachments are disabled for Microsoft Teams.").ConfigureAwait(false);
            return false;
        }

        if (_dependencies.AttachmentDownloader is null
            || _dependencies.ContentScanner is null
            || _dependencies.AudienceProfiles is null
            || _dependencies.StorageResolver is null)
        {
            await SendAttachmentRejectionAsync("Attachments are temporarily unavailable. Please try again later.").ConfigureAwait(false);
            return false;
        }

        var profile = ToolAudienceProfileDefaults.GetResolvedProfile(_dependencies.AudienceProfiles, audience);
        var policy = profile.ChannelAttachments ?? ChannelAttachmentPolicy.Empty;
        if (activity.Attachments.Length > policy.MaxFilesPerMessage)
        {
            await SendAttachmentRejectionAsync(
                $"I can only accept up to {policy.MaxFilesPerMessage} attachments per message. Please split your upload and try again.")
                .ConfigureAwait(false);
            return false;
        }

        var inlineImages = _dependencies.ModelCapabilities?.InputModalities.HasFlag(ModelModality.Image) == true;
        var storage = _dependencies.StorageResolver.Resolve(_sessionId);
        var inboxDirectory = SessionDirectoryHelper.GetOrCreateInboxDirectory(storage);
        var stagingDirectory = SessionDirectoryHelper.GetOrCreateAttachmentStagingDirectory(storage);
        var acceptedLines = new List<string>(activity.Attachments.Length);
        var inlineContents = new List<DataContent>();
        var rejections = new List<string>();

        var results = new AttachmentIngestOutcome?[activity.Attachments.Length];
        var inboxWriteGate = new object();
        using var deadline = new CancellationTokenSource(TeamsIngressTimeouts.AttachmentBatch, _dependencies.TimeProvider);
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            // The shared non-image ingress assumes one inbox writer. Finish it before parallel image work.
            foreach (var index in Enumerable.Range(0, activity.Attachments.Length))
            {
                if (!TeamsProvisionalInlineImageIngress.IsImage(activity.Attachments[index]))
                    results[index] = await IngestAttachmentAsync(activity.Attachments[index], batch.Token).ConfigureAwait(false);
            }
            await Parallel.ForEachAsync(
                Enumerable.Range(0, activity.Attachments.Length)
                    .Where(index => TeamsProvisionalInlineImageIngress.IsImage(activity.Attachments[index])),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = TeamsIngressTimeouts.ConcurrentAttachments,
                    CancellationToken = batch.Token
                },
                async (index, token) =>
                {
                    results[index] = await IngestAttachmentAsync(activity.Attachments[index], token).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            _log.Warning("attachment_batch_rejected reason=batch-deadline configured_deadline_ms={ConfiguredDeadlineMs}",
                TeamsIngressTimeouts.AttachmentBatch.TotalMilliseconds);
            rejections.Add("Timed out processing some attachments. Please send those images again.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        // Preserve attachment order. Only this continuation assembles the model input and user reply.
        foreach (var result in results)
        {
            switch (result)
            {
                case AttachmentIngestOutcome.Accepted accepted:
                    acceptedLines.Add(accepted.Line);
                    if (accepted.Inline is { } inline)
                        inlineContents.Add(inline);
                    break;
                case AttachmentIngestOutcome.Rejected rejected:
                    rejections.Add(rejected.UserFacingReason);
                    break;
            }
        }

        if (acceptedLines.Count > 0)
        {
            contents.Add(new TextContent(string.Join('\n', acceptedLines)));
            contents.AddRange(inlineContents);
        }

        if (rejections.Count > 0)
        {
            var message = rejections.Count == 1
                ? rejections[0]
                : "Some attachments were not accepted:\n- " + string.Join("\n- ", rejections);
            await SendAttachmentRejectionAsync(message).ConfigureAwait(false);
        }

        return acceptedLines.Count > 0;

        async Task<AttachmentIngestOutcome> IngestAttachmentAsync(TeamsAttachmentMetadata attachment, CancellationToken token)
        {
            if (attachment.Kind == TeamsInboundAttachmentKind.Unknown)
                return new AttachmentIngestOutcome.Rejected($"`{attachment.Name}` is not supported in this Teams conversation.");

            return TeamsProvisionalInlineImageIngress.IsImage(attachment)
                ? await TeamsProvisionalInlineImageIngress.IngestAsync(
                    activity,
                    attachment,
                    audience,
                    policy,
                    inlineImages,
                    inboxDirectory,
                    stagingDirectory,
                    inboxWriteGate,
                    _dependencies.TimeProvider,
                    _dependencies.ContentScanner,
                    _log,
                    _dependencies.AttachmentDownloader,
                    token).ConfigureAwait(false)
                : await AttachmentIngressPipeline.IngestAsync(
                    new AttachmentIngressRequest(
                        attachment.Name,
                        attachment.ContentType ?? "application/octet-stream",
                        attachment.DeclaredSizeBytes ?? 0),
                    audience,
                    policy,
                    inlineImages,
                    inboxDirectory,
                    stagingDirectory,
                    TeamsIngressTimeouts.AttachmentOperation,
                    _dependencies.ContentScanner,
                    _log,
                    (staging, maximumBytes, token) => _dependencies.AttachmentDownloader.DownloadAsync(
                        activity,
                        attachment,
                        staging,
                        maximumBytes,
                        token),
                    token).ConfigureAwait(false);
        }
    }

    private async Task SendAttachmentRejectionAsync(string message)
    {
        if (_destination is null)
            return;

        var result = await DeliverAsync(CreateMessage(_destination, message)).ConfigureAwait(false);
        if (!result.IsSuccess)
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("attachment_rejection_delivery_failed");
    }

    private bool TryGetExpectedSessionId(TeamsInboundActivity activity, out SessionId sessionId)
    {
        if (activity.Trust.Scope == TeamsConversationScope.Personal)
        {
            return TeamsSessionIdentifierCodec.TryCreatePersonal(
                activity.Trust.TenantId,
                activity.Trust.ConversationId,
                out sessionId,
                out _);
        }

        if (activity.Trust.Scope == TeamsConversationScope.GroupChat)
        {
            return TeamsSessionIdentifierCodec.TryCreateGroupChat(
                activity.Trust.TenantId,
                activity.Trust.ConversationId,
                out sessionId,
                out _);
        }

        if (activity.Trust.Scope == TeamsConversationScope.Channel
            && activity.Reply?.RootActivityId is { } rootActivityId)
        {
            return TeamsSessionIdentifierCodec.TryCreateChannel(
                activity.Trust.TenantId,
                activity.Trust.ConversationId,
                rootActivityId,
                out sessionId,
                out _);
        }

        sessionId = default;
        return false;
    }

    private ChannelAclDecision? EvaluateAcl(TeamsInboundActivity activity)
        => TeamsActorAclEvaluator.Evaluate(activity, _dependencies.Options);

    private bool IsAuthorizedMentionOnlyIngress(TeamsBindingIngress ingress)
    {
        if (_isGroupChatBinding)
            return !_dependencies.Options.MentionOnly || ingress.Activity.IsMentioned;

        if (!_isChannelBinding || !_dependencies.Options.MentionOnly || ingress.Activity.IsMentioned)
        {
            return true;
        }

        return ingress.IsEstablishedThreadContinuation;
    }


    private void ApplyReserved(DurableActivityDispatchReserved reserved)
    {
        EnsureValidFingerprint(reserved.ActivityFingerprint);
        if (reserved.EvictedActivityFingerprint is { } evicted)
        {
            EnsureValidFingerprint(evicted);
            if (_processedActivityOrder.Count == 0
                || !string.Equals(_processedActivityOrder.Peek(), evicted, StringComparison.Ordinal)
                || !_processedActivityIds.Remove(evicted))
            {
                throw new InvalidOperationException("The Teams processed activity state has invalid retention ordering.");
            }

            _processedActivityOrder.Dequeue();
        }
        else if (_processedActivityOrder.Count >= ProcessedActivityCapacity)
            throw new InvalidOperationException("The Teams processed activity state exceeds its retention limit.");

        if (!_processedActivityIds.Add(reserved.ActivityFingerprint))
            throw new InvalidOperationException("The Teams processed activity state contains a duplicate reservation.");

        _processedActivityOrder.Enqueue(reserved.ActivityFingerprint);
    }

    private void ApplyReleased(DurableActivityDispatchReleased released)
    {
        EnsureValidFingerprint(released.ActivityFingerprint);
        if (!_processedActivityIds.Remove(released.ActivityFingerprint))
            return;

        var retained = _processedActivityOrder.Where(id => id != released.ActivityFingerprint).ToArray();
        _processedActivityOrder.Clear();
        foreach (var fingerprint in retained)
            _processedActivityOrder.Enqueue(fingerprint);
    }

    private void ApplyDestinationCaptured(TeamsProactiveDestinationCaptured captured)
    {
        TeamsOutboundDestination destination;
        try
        {
            destination = new TeamsOutboundDestination(
                captured.TenantId,
                captured.ConversationId,
                (TeamsConversationScope)captured.Scope,
                captured.ServiceUrl,
                captured.RootActivityId,
                captured.TeamId,
                captured.ChannelId,
                captured.UserId);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("The Teams proactive destination state is invalid.", exception);
        }

        if (!MatchesBinding(destination))
            throw new InvalidOperationException("The Teams proactive destination does not match its binding session.");

        if (captured.Generation < 1)
            throw new InvalidOperationException("The Teams proactive destination generation is invalid.");
        if (_destination is not null && captured.Generation <= _destinationGeneration)
            throw new InvalidOperationException("The Teams proactive destination generation must advance.");

        _destination = destination;
        _destinationGeneration = captured.Generation;
    }

    private void ApplyProactiveDeliveryRecorded(TeamsProactiveDeliveryRecorded recorded)
    {
        if (!IsBoundedDeliveryKey(recorded.DeliveryKey)
            || !Enum.IsDefined((TeamsProactiveDeliveryState)recorded.State)
            || recorded.DestinationGeneration < 1)
        {
            throw new InvalidOperationException("The Teams proactive delivery state is invalid.");
        }
        if (_destinationGeneration > 0 && recorded.DestinationGeneration > _destinationGeneration)
            throw new InvalidOperationException("The Teams proactive delivery references an unknown destination generation.");

        if (recorded.EvictedDeliveryKey is { } evicted)
        {
            if (!IsBoundedDeliveryKey(evicted)
                || _proactiveDeliveryOrder.Count == 0
                || !string.Equals(_proactiveDeliveryOrder.Peek(), evicted, StringComparison.Ordinal)
                || !_proactiveDeliveries.Remove(evicted)
                || !_proactiveDeliveryGenerations.Remove(evicted))
            {
                throw new InvalidOperationException("The Teams proactive delivery retention state is invalid.");
            }

            _proactiveDeliveryOrder.Dequeue();
        }

        if (_proactiveDeliveries.ContainsKey(recorded.DeliveryKey))
        {
            _proactiveDeliveries[recorded.DeliveryKey] = (TeamsProactiveDeliveryState)recorded.State;
            _proactiveDeliveryGenerations[recorded.DeliveryKey] = recorded.DestinationGeneration;
            ApplyDestinationInvalidation(recorded);
            return;
        }

        if (_proactiveDeliveryOrder.Count >= ProactiveDeliveryCapacity)
            throw new InvalidOperationException("The Teams proactive delivery state exceeds its retention limit.");

        _proactiveDeliveries.Add(recorded.DeliveryKey, (TeamsProactiveDeliveryState)recorded.State);
        _proactiveDeliveryGenerations.Add(recorded.DeliveryKey, recorded.DestinationGeneration);
        _proactiveDeliveryOrder.Enqueue(recorded.DeliveryKey);
        ApplyDestinationInvalidation(recorded);
    }

    private bool MatchesBinding(TeamsOutboundDestination destination)
    {
        if (destination.Scope == TeamsConversationScope.Personal)
        {
            return TeamsSessionIdentifierCodec.TryCreatePersonal(
                       destination.TenantId,
                       destination.ConversationId,
                       out var sessionId,
                       out _)
                   && sessionId == _sessionId;
        }

        if (destination.Scope == TeamsConversationScope.GroupChat)
        {
            return TeamsSessionIdentifierCodec.TryCreateGroupChat(
                       destination.TenantId,
                       destination.ConversationId,
                       out var groupChatSessionId,
                       out _)
                   && groupChatSessionId == _sessionId;
        }

        return TeamsSessionIdentifierCodec.TryCreateChannel(
                   destination.TenantId,
                   destination.ConversationId,
                   destination.RootActivityId!,
                   out var channelSessionId,
                   out _)
               && channelSessionId == _sessionId;
    }

    private static bool IsBoundedDeliveryKey(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Encoding.UTF8.GetByteCount(value) <= TeamsSessionIdentifierCodec.MaxRawIdentifierBytes;

    private TeamsDestinationResolution ResolveReminderDestination(string? knownDestinationKey)
    {
        if (!TeamsSessionIdentifierCodec.TryParse(_sessionId, out var identifier, out _))
            return new TeamsDestinationResolution(TeamsDestinationResolutionDisposition.Rejected, ReasonCode: "invalid_binding_session");

        var candidates = _destination is null
            ? Array.Empty<TeamsProactiveDestinationCandidate>()
            : [new TeamsProactiveDestinationCandidate(
                _sessionId,
                identifier.Scope,
                _destinationGeneration,
                IsDestinationActive(_destination))];

        return TeamsProactiveDestinationResolver.Resolve(
            _sessionId,
            identifier.Scope,
            knownDestinationKey,
            candidates);
    }

    private bool IsDestinationActive(TeamsOutboundDestination destination) =>
        _destinationGeneration > 0 && MatchesBinding(destination);

    private static TeamsProactiveDestinationCaptured ToCapturedDestination(TeamsOutboundDestination destination, long generation) => new()
    {
        TenantId = destination.TenantId,
        ConversationId = destination.ConversationId,
        Scope = (int)destination.Scope,
        ServiceUrl = destination.ServiceUrl,
        RootActivityId = destination.RootActivityId,
        TeamId = destination.TeamId,
        ChannelId = destination.ChannelId,
        UserId = destination.UserId,
        Generation = generation
    };

    private void ApplyDestinationInvalidation(TeamsProactiveDeliveryRecorded recorded)
    {
        if (recorded.InvalidatesDestination && _destinationGeneration == recorded.DestinationGeneration)
        {
            _destination = null;
            RecordDestinationTelemetry("proactive_destination_invalidated");
        }
    }

    private void ApplyLegacyPersistence(LegacyChannelPersistenceEnvelope legacy)
    {
        _requiresMigrationSnapshot = true;
        switch (legacy.Manifest)
        {
            case "tapc-v1":
            {
                var value = LegacyProto.TeamsApprovalPendingCreatedProto.Parser.ParseFrom(legacy.Payload);
                ApplyApprovalPendingCreated(new TeamsApprovalPendingCreated
                {
                    CallId = value.CallId,
                    CorrelationId = value.CorrelationId,
                    NonceHash = value.NonceHash,
                    RequesterSenderId = value.HasRequesterSenderId ? value.RequesterSenderId : null,
                    RequesterPrincipal = value.HasRequesterPrincipal ? (PrincipalClassification)value.RequesterPrincipal : null,
                    ExpiresAtUnixMilliseconds = value.ExpiresAtUnixMilliseconds
                });
                break;
            }
            case "tacd-v1":
            {
                var value = LegacyProto.TeamsApprovalCardDeliveredProto.Parser.ParseFrom(legacy.Payload);
                ApplyApprovalCardDelivered(new TeamsApprovalCardDelivered { CorrelationId = value.CorrelationId, PromptId = value.PromptId });
                break;
            }
            case "taco-v1":
            {
                var value = LegacyProto.TeamsApprovalConsumedProto.Parser.ParseFrom(legacy.Payload);
                ApplyApprovalConsumed(new TeamsApprovalConsumed
                {
                    CorrelationId = value.CorrelationId, Decision = value.Decision,
                    ConsumedAtUnixMilliseconds = value.ConsumedAtUnixMilliseconds
                });
                break;
            }
            case "tpdc-v1":
                ApplyLegacyDestination(LegacyProto.TeamsProactiveDestinationCapturedProto.Parser.ParseFrom(legacy.Payload));
                break;
            case "tpdi-v1":
                _destination = null;
                break;
            case "tpdr-v1":
            {
                var value = LegacyProto.TeamsProactiveDeliveryRecordedProto.Parser.ParseFrom(legacy.Payload);
                ApplyProactiveDeliveryRecorded(new TeamsProactiveDeliveryRecorded
                {
                    DeliveryKey = value.DeliveryKey,
                    State = value.State,
                    EvictedDeliveryKey = value.HasEvictedDeliveryKey ? value.EvictedDeliveryKey : null,
                    DestinationGeneration = Math.Max(1, _destinationGeneration)
                });
                break;
            }
            case "dads-v1":
                ApplyLegacySnapshot(LegacyProto.DurableActivityDispatchSnapshotProto.Parser.ParseFrom(legacy.Payload));
                break;
            default:
                throw new InvalidOperationException("The legacy Team persistence manifest is not valid for a binding actor.");
        }
    }

    private void ApplyLegacyDestination(LegacyProto.TeamsProactiveDestinationCapturedProto value) =>
        ApplyDestinationCaptured(new TeamsProactiveDestinationCaptured
        {
            TenantId = value.TenantId,
            ConversationId = value.ConversationId,
            Scope = value.Scope,
            ServiceUrl = value.ServiceUrl,
            RootActivityId = value.HasRootActivityId ? value.RootActivityId : null,
            TeamId = value.HasTeamId ? value.TeamId : null,
            ChannelId = value.HasChannelId ? value.ChannelId : null,
            UserId = value.HasUserId ? value.UserId : null,
            Generation = _destination is null ? 1 : checked(_destinationGeneration + 1)
        });

    private void ApplyLegacySnapshot(LegacyProto.DurableActivityDispatchSnapshotProto value)
    {
        ApplySnapshot(new TeamsBindingSnapshot(value.ActivityFingerprints.ToArray())
        {
            Approvals = value.TeamsApprovals.Select(approval => new TeamsApprovalSnapshotEntry
            {
                CallId = approval.CallId,
                CorrelationId = approval.CorrelationId,
                NonceHash = approval.NonceHash,
                RequesterSenderId = approval.HasRequesterSenderId ? approval.RequesterSenderId : null,
                RequesterPrincipal = approval.HasRequesterPrincipal ? (PrincipalClassification)approval.RequesterPrincipal : null,
                ExpiresAtUnixMilliseconds = approval.ExpiresAtUnixMilliseconds,
                PromptId = approval.HasPromptId ? approval.PromptId : null,
                Decision = approval.HasDecision ? approval.Decision : null
            }).ToArray(),
            Destination = value.TeamsDestination is null ? null : new TeamsProactiveDestinationCaptured
            {
                TenantId = value.TeamsDestination.TenantId,
                ConversationId = value.TeamsDestination.ConversationId,
                Scope = value.TeamsDestination.Scope,
                ServiceUrl = value.TeamsDestination.ServiceUrl,
                RootActivityId = value.TeamsDestination.HasRootActivityId ? value.TeamsDestination.RootActivityId : null,
                TeamId = value.TeamsDestination.HasTeamId ? value.TeamsDestination.TeamId : null,
                ChannelId = value.TeamsDestination.HasChannelId ? value.TeamsDestination.ChannelId : null,
                UserId = value.TeamsDestination.HasUserId ? value.TeamsDestination.UserId : null,
                Generation = 1
            },
            ProactiveDeliveries = value.TeamsProactiveDeliveries.Select(delivery => new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = delivery.DeliveryKey,
                State = delivery.State,
                DestinationGeneration = 1
            }).ToArray()
        });
    }

    private static void RecordDestinationTelemetry(string code) =>
        ChannelTelemetry.For(ChannelType.Teams).RecordExtra(code);

    private void MarkRecoveredDeliveriesUnknown()
    {
        var interrupted = _proactiveDeliveryOrder
            .Where(key => _proactiveDeliveries[key] == TeamsProactiveDeliveryState.Sending)
            .Select(key => new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = key,
                State = (int)TeamsProactiveDeliveryState.DeliveryUnknown,
                DestinationGeneration = _proactiveDeliveryGenerations[key]
            })
            .ToArray();
        if (interrupted.Length > 0)
            PersistAll(interrupted, ApplyProactiveDeliveryRecorded);
    }

    private void CompleteReminderFailure(string deliveryKey, string safeReason)
    {
        Persist(new TeamsProactiveDeliveryRecorded
        {
            DeliveryKey = deliveryKey,
            State = (int)TeamsProactiveDeliveryState.FailedRetryable,
            DestinationGeneration = _proactiveDeliveryGenerations.GetValueOrDefault(deliveryKey, _destinationGeneration)
        }, recorded =>
        {
            ApplyProactiveDeliveryRecorded(recorded);
            if (_reminderDeliveryObservers.Remove(deliveryKey, out var observer))
            {
                observer.Tell(new ReminderDeliveryResult(
                    new ReminderId(deliveryKey),
                    ChannelType.Teams,
                    false,
                    safeReason,
                    _dependencies.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
            }
        });
    }

    private void ApplySnapshot(TeamsBindingSnapshot snapshot)
    {
        if (snapshot.MigrationVersion != TeamsBindingSnapshot.CurrentMigrationVersion)
            throw new InvalidOperationException("The Teams binding snapshot version is not supported.");
        if (snapshot.ActivityFingerprints.Count > ProcessedActivityCapacity)
            throw new InvalidOperationException("The Teams processed activity snapshot exceeds its retention limit.");
        if (snapshot.Approvals.Count > ApprovalCapacity)
            throw new InvalidOperationException("The Teams approval snapshot exceeds its retention limit.");
        if (snapshot.ProactiveDeliveries.Count > ProactiveDeliveryCapacity)
            throw new InvalidOperationException("The Teams proactive delivery snapshot exceeds its retention limit.");

        _processedActivityIds.Clear();
        _processedActivityOrder.Clear();
        _pendingApprovals.Clear();
        _pendingApprovalRequests.Clear();
        _proactiveDeliveries.Clear();
        _proactiveDeliveryGenerations.Clear();
        _proactiveDeliveryOrder.Clear();
        if (snapshot.LastDestinationGeneration < 0)
            throw new InvalidOperationException("The Teams proactive destination snapshot generation is invalid.");
        _destination = null;
        _destinationGeneration = snapshot.LastDestinationGeneration;
        foreach (var fingerprint in snapshot.ActivityFingerprints)
        {
            EnsureValidFingerprint(fingerprint);
            if (!_processedActivityIds.Add(fingerprint))
                throw new InvalidOperationException("The Teams processed activity snapshot contains a duplicate entry.");

            _processedActivityOrder.Enqueue(fingerprint);
        }

        foreach (var approval in snapshot.Approvals)
        {
            var presentationPending = approval.PresentationPending && approval.PromptId is null;
            ApplyApprovalPendingCreated(new TeamsApprovalPendingCreated
            {
                CallId = approval.CallId,
                CorrelationId = approval.CorrelationId,
                NonceHash = approval.NonceHash,
                RequesterSenderId = approval.RequesterSenderId,
                RequesterPrincipal = approval.RequesterPrincipal,
                ExpiresAtUnixMilliseconds = approval.ExpiresAtUnixMilliseconds,
                OfferedOptionKeys = approval.OfferedOptionKeys,
                IsMcpTool = approval.IsMcpTool,
                ToolName = approval.ToolName,
                RequestDisplayText = approval.RequestDisplayText,
                PresentationPending = presentationPending
            });
            if (!presentationPending)
            {
                ApplyApprovalCardDelivered(new TeamsApprovalCardDelivered
                {
                    CorrelationId = approval.CorrelationId,
                    PromptId = approval.PromptId
                });
            }
            if (approval.ForwardingDecision is not null)
            {
                ApplyApprovalForwardingStarted(new TeamsApprovalForwardingStarted
                {
                    CorrelationId = approval.CorrelationId,
                    Decision = approval.ForwardingDecision,
                    SenderId = approval.ForwardingSenderId ?? approval.RequesterSenderId ?? string.Empty,
                    StartedAtUnixMilliseconds = 1
                });
            }
            if (approval.Decision is not null)
            {
                ApplyApprovalConsumed(new TeamsApprovalConsumed
                {
                    CorrelationId = approval.CorrelationId,
                    Decision = approval.Decision
                });
            }
        }

        if (snapshot.Destination is not null)
        {
            if (snapshot.LastDestinationGeneration > 0
                && snapshot.LastDestinationGeneration != snapshot.Destination.Generation)
            {
                throw new InvalidOperationException("The Teams proactive destination snapshot generation is inconsistent.");
            }
            ApplyDestinationCaptured(new TeamsProactiveDestinationCaptured
            {
                TenantId = snapshot.Destination.TenantId,
                ConversationId = snapshot.Destination.ConversationId,
                Scope = snapshot.Destination.Scope,
                ServiceUrl = snapshot.Destination.ServiceUrl,
                RootActivityId = snapshot.Destination.RootActivityId,
                TeamId = snapshot.Destination.TeamId,
                ChannelId = snapshot.Destination.ChannelId,
                UserId = snapshot.Destination.UserId,
                Generation = snapshot.Destination.Generation
            });
        }

        foreach (var delivery in snapshot.ProactiveDeliveries)
        {
            ApplyProactiveDeliveryRecorded(new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = delivery.DeliveryKey,
                State = delivery.State,
                DestinationGeneration = delivery.DestinationGeneration,
                InvalidatesDestination = delivery.InvalidatesDestination
            });
        }
    }

    private void SaveSnapshotWhenDue()
    {
        if (!IsRecovering && LastSequenceNr > 0 && LastSequenceNr % SnapshotInterval == 0)
            SaveSnapshot(CreateSnapshot());
    }

    private void SaveMigrationSnapshot()
    {
        if (IsRecovering || !_requiresMigrationSnapshot || _migrationSnapshotSaveInFlight)
            return;

        _migrationSnapshotSaveInFlight = true;
        SaveSnapshot(CreateSnapshot());
    }

    private TeamsBindingProactiveDiagnostics CreateProactiveDiagnostics()
    {
        var migration = _requiresMigrationSnapshot
            ? _migrationSnapshotFailed
                ? TeamsMigrationHealthState.Failed
                : TeamsMigrationHealthState.Pending
            : _destination is null && _destinationGeneration == 0
                ? TeamsMigrationHealthState.NotRequired
                : TeamsMigrationHealthState.Completed;
        var pending = _proactiveDeliveries.Values.Count(state => state is TeamsProactiveDeliveryState.Pending or TeamsProactiveDeliveryState.Sending);
        var retryable = _proactiveDeliveries.Values.Count(state => state == TeamsProactiveDeliveryState.FailedRetryable);
        var permanent = _proactiveDeliveries.Values.Count(state => state == TeamsProactiveDeliveryState.FailedPermanent);
        var unknown = _proactiveDeliveries.Values.Count(state => state == TeamsProactiveDeliveryState.DeliveryUnknown);
        var terminal = _proactiveDeliveries.Values.Count(state => state is TeamsProactiveDeliveryState.Sent
            or TeamsProactiveDeliveryState.FailedPermanent
            or TeamsProactiveDeliveryState.DeliveryUnknown);
        var invalidated = _destination is null && _destinationGeneration > 0 ? 1 : 0;
        var missingTarget = _destination is null ? 1 : 0;
        var hasCapacityPressure = _proactiveDeliveryOrder.Count >= ProactiveDeliveryCapacity;

        if (!_dependencies.Options.Enabled)
        {
            return new TeamsBindingProactiveDiagnostics(
                TeamsProactiveHealthState.Disabled,
                migration,
                0,
                0,
                pending,
                retryable,
                permanent,
                unknown,
                _proactiveDeliveryOrder.Count,
                hasCapacityPressure,
                "teams_disabled")
            {
                TerminalDeliveryCount = terminal,
                InvalidatedDestinationCount = invalidated,
                MissingTargetCount = missingTarget
            };
        }

        if (hasCapacityPressure)
        {
            return new TeamsBindingProactiveDiagnostics(
                TeamsProactiveHealthState.CapacityPressure,
                migration,
                _destination?.Scope == TeamsConversationScope.Personal ? 1 : 0,
                _destination?.Scope == TeamsConversationScope.Channel ? 1 : 0,
                pending,
                retryable,
                permanent,
                unknown,
                _proactiveDeliveryOrder.Count,
                true,
                "proactive_delivery_capacity_reached")
            {
                TerminalDeliveryCount = terminal,
                InvalidatedDestinationCount = invalidated,
                MissingTargetCount = missingTarget
            };
        }

        if (_destination is null)
        {
            return new TeamsBindingProactiveDiagnostics(
                TeamsProactiveHealthState.Unavailable,
                migration,
                0,
                0,
                pending,
                retryable,
                permanent,
                unknown,
                _proactiveDeliveryOrder.Count,
                false,
                "proactive_destination_missing")
            {
                TerminalDeliveryCount = terminal,
                InvalidatedDestinationCount = invalidated,
                MissingTargetCount = missingTarget
            };
        }

        return new TeamsBindingProactiveDiagnostics(
            TeamsProactiveHealthState.Available,
            migration,
            _destination.Scope == TeamsConversationScope.Personal ? 1 : 0,
            _destination.Scope == TeamsConversationScope.Channel ? 1 : 0,
            pending,
            retryable,
            permanent,
            unknown,
            _proactiveDeliveryOrder.Count,
            false)
        {
            TerminalDeliveryCount = terminal,
            InvalidatedDestinationCount = invalidated,
            MissingTargetCount = missingTarget
        };
    }

    private TeamsBindingSnapshot CreateSnapshot() => new(_processedActivityOrder.ToArray())
    {
        Approvals = _pendingApprovals.Values.Select(pending => new TeamsApprovalSnapshotEntry
        {
            CallId = pending.CallId,
            CorrelationId = pending.CorrelationId,
            NonceHash = pending.NonceHash,
            RequesterSenderId = pending.RequesterSenderId,
            RequesterPrincipal = pending.RequesterPrincipal,
            ExpiresAtUnixMilliseconds = pending.ExpiresAtUnixMilliseconds,
            OfferedOptionKeys = pending.OfferedOptionKeys,
            IsMcpTool = pending.IsMcpTool,
            ToolName = pending.ToolName,
            RequestDisplayText = pending.RequestDisplayText,
            PromptId = pending.PromptId,
            PresentationPending = pending.PresentationPending,
            ForwardingDecision = pending.ForwardingDecision,
            ForwardingSenderId = pending.ForwardingSenderId,
            Decision = pending.Decision
        }).ToArray(),
        Destination = _destination is null ? null : ToCapturedDestination(_destination, _destinationGeneration),
        LastDestinationGeneration = _destinationGeneration,
        ProactiveDeliveries = _proactiveDeliveryOrder.Select(key => new TeamsProactiveDeliveryRecorded
        {
            DeliveryKey = key,
            State = (int)_proactiveDeliveries[key],
            DestinationGeneration = _proactiveDeliveryGenerations[key]
        }).ToArray()
    };

    private async Task HandleOutputAsync(BindingOutput received)
    {
        if (received.Output is TurnCompleted completed)
        {
            await _outputEngine.HandleOutputAsync(completed);
            HandleReminderTurnCompleted(completed);
            _lastCompletedTurnNumber = completed.TurnNumber;
            return;
        }

        if (received.Output is TextOutput { SourceReminderId: null })
        {
            await _outputEngine.HandleOutputAsync(received.Output);
            return;
        }

        if (received.Output is not TextOutput text)
        {
            await _outputEngine.HandleOutputAsync(received.Output);
            return;
        }

        if (_destination is null)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("output_destination_unavailable");
            RecordDeliveryOutcome(new TeamsDeliveryResult(TeamsDeliveryStatus.Unavailable, ReasonCode: "output_destination_unavailable"), 0);
            return;
        }

        var rendered = _dependencies.OutputRenderer.Render(
            text.Text,
            _destination.Scope == TeamsConversationScope.Channel ? _destination.RootActivityId : null);
        if (rendered.Chunks.Count == 0)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordExtra(
                rendered.IsRejectedTooLarge ? "output_rejected_too_large" : "output_ignored_empty");
            return;
        }

        var reminderDeliveryKey = received.Output.SourceReminderId?.Value;
        if (reminderDeliveryKey is not null)
        {
            if (!_proactiveDeliveries.TryGetValue(reminderDeliveryKey, out var reminderState)
                || reminderState != TeamsProactiveDeliveryState.Sending)
            {
                ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("proactive_delivery_completion_ignored");
                return;
            }

            if (_proactiveDeliveryGenerations[reminderDeliveryKey] != _destinationGeneration)
            {
                CompleteReminderFailure(
                    reminderDeliveryKey,
                    "The Teams proactive destination changed before delivery completed.");
                return;
            }
        }

        for (var index = 0; index < rendered.Chunks.Count; index++)
        {
            var result = await DeliverProactiveAsync(CreateMessage(_destination, rendered.Chunks[index]));

            if (!result.IsSuccess)
            {
                ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped(
                    result.Status == TeamsDeliveryStatus.RejectedTooLarge
                        ? "output_rejected_too_large"
                        : "output_delivery_failed");
                if (reminderDeliveryKey is not null)
                    CompleteReminderDelivery(reminderDeliveryKey, result);
                return;
            }
        }

        if (reminderDeliveryKey is not null)
        {
            _reminderTextDelivered.Add(reminderDeliveryKey);
            CompleteReminderDelivery(reminderDeliveryKey, new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered));
        }
    }

    private async Task HandleChannelSpecificOutputAsync(SessionOutput output)
    {
        switch (output)
        {
            case ProcessingStateOutput { IsProcessing: true } when _destination is { } destination:
                await SendTypingAsync(destination);
                break;

            case ProcessingStateOutput { IsProcessing: true }:
                ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("typing_indicator_destination_unavailable");
                break;

            case ToolInteractionRequest approval:
                HandleApprovalRequest(approval);
                break;
        }
    }

    private async Task<bool> PostTextAsync(string text)
    {
        if (_destination is null)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("output_destination_unavailable");
            RecordDeliveryOutcome(new TeamsDeliveryResult(TeamsDeliveryStatus.Unavailable, ReasonCode: "output_destination_unavailable"), 0);
            await NotifyDeliveryFailedAsync(DeliveryFailureKind.TransportFailure, "Teams output destination is unavailable.");
            return false;
        }

        var rendered = _dependencies.OutputRenderer.Render(
            text,
            _destination.Scope == TeamsConversationScope.Channel ? _destination.RootActivityId : null);
        if (rendered.Chunks.Count == 0)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordExtra(
                rendered.IsRejectedTooLarge ? "output_rejected_too_large" : "output_ignored_empty");
            if (rendered.IsRejectedTooLarge)
            {
                await NotifyDeliveryFailedAsync(DeliveryFailureKind.MessageTooLarge, "Teams output exceeded the channel size limit.");
                return false;
            }

            return true;
        }

        foreach (var chunk in rendered.Chunks)
        {
            var result = await DeliverAsync(CreateMessage(_destination, chunk));
            if (result.IsSuccess)
                continue;

            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped(
                result.Status == TeamsDeliveryStatus.RejectedTooLarge
                    ? "output_rejected_too_large"
                    : "output_delivery_failed");
            return false;
        }

        return true;
    }

    private async Task<bool> FailUnsupportedFileOutputAsync(FileOutput file)
    {
        await NotifyDeliveryFailedAsync(
            DeliveryFailureKind.UnsupportedContent,
            $"Teams does not support file output delivery: {file.FileName}");
        return false;
    }

    private void HandleReminderTurnCompleted(TurnCompleted completed)
    {
        if (completed.SourceReminderId is not { } reminderId
            || string.IsNullOrWhiteSpace(reminderId.Value))
            return;

        var deliveryKey = reminderId.Value;
        if (!_proactiveDeliveries.TryGetValue(deliveryKey, out var state))
            return;

        if (state != TeamsProactiveDeliveryState.Sending)
        {
            _reminderTextDelivered.Remove(deliveryKey);
            return;
        }

        if (!_reminderTextDelivered.Remove(deliveryKey))
        {
            CompleteReminderFailure(deliveryKey, "Teams reminder completed without a delivered message.");
            return;
        }

    }

    private void CompleteReminderDelivery(string deliveryKey, TeamsDeliveryResult result)
    {
        var state = result.IsSuccess
            ? TeamsProactiveDeliveryState.Sent
            : result.Status == TeamsDeliveryStatus.InvalidDestination
                ? TeamsProactiveDeliveryState.FailedPermanent
                : TeamsProactiveDeliveryState.FailedRetryable;
        Persist(new TeamsProactiveDeliveryRecorded
        {
            DeliveryKey = deliveryKey,
            State = (int)state,
            DestinationGeneration = _proactiveDeliveryGenerations.GetValueOrDefault(deliveryKey, _destinationGeneration),
            InvalidatesDestination = state == TeamsProactiveDeliveryState.FailedPermanent
        }, recorded =>
        {
            ApplyProactiveDeliveryRecorded(recorded);
            SaveSnapshotWhenDue();

            if (_reminderDeliveryObservers.Remove(deliveryKey, out var observer))
            {
                observer.Tell(new ReminderDeliveryResult(
                    new ReminderId(deliveryKey),
                    ChannelType.Teams,
                    result.IsSuccess,
                    result.IsSuccess ? null : "Teams proactive delivery failed.",
                    _dependencies.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
            }

            ChannelTelemetry.For(ChannelType.Teams).RecordExtra(result.IsSuccess
                ? "proactive_delivery_delivered"
                : state == TeamsProactiveDeliveryState.FailedPermanent
                    ? "proactive_delivery_failed_permanent"
                    : "proactive_delivery_failed_retryable");
        });
    }

    private async Task<TeamsDeliveryResult> DeliverAsync(TeamsOutboundMessage message)
    {
        var result = new TeamsDeliveryResult(TeamsDeliveryStatus.Failed, ReasonCode: "reply_client_failed");
        await _safeTransportCall.InvokeAsync(
            async () =>
            {
                result = await _dependencies.ReplyClient.DeliverAsync(message, CancellationToken.None);
                if (!result.IsSuccess)
                    throw new TeamsDeliveryException(result);
            },
            ex => _log.Warning(ex, "Failed delivering Teams reply for session {0}", _sessionId.Value));

        if (result.Status == TeamsDeliveryStatus.RejectedTooLarge)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordReplyRejected(result.ReasonCode);
        }
        return result;
    }

    private async Task<TeamsDeliveryResult> DeliverProactiveAsync(TeamsOutboundMessage message)
    {
        var startedAt = _dependencies.TimeProvider.GetTimestamp();
        TeamsDeliveryResult result;
        try
        {
            result = await _dependencies.ReplyClient.DeliverAsync(message, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            result = new TeamsDeliveryResult(TeamsDeliveryStatus.Cancelled, ReasonCode: "cancelled");
        }
        catch (Exception)
        {
            result = new TeamsDeliveryResult(TeamsDeliveryStatus.Failed, ReasonCode: "reply_client_failed");
        }

        RecordDeliveryOutcome(result, _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
        return result;
    }

    private async Task NotifyDeliveryFailedAsync(DeliveryFailureKind failureKind, string errorMessage)
    {
        await _dependencies.Pipeline.SendFeedbackAsync(new DeliveryFailed
        {
            SessionId = _sessionId,
            TurnNumber = _lastCompletedTurnNumber,
            ChannelType = ChannelType.Teams,
            FailureKind = failureKind,
            ErrorMessage = errorMessage
        });
    }

    private async Task SendTypingAsync(TeamsOutboundDestination destination)
    {
        try
        {
            var result = await _dependencies.ReplyClient.SendTypingAsync(destination, CancellationToken.None);
            if (!result.IsSuccess)
                ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("typing_indicator_delivery_failed");
        }
        catch (OperationCanceledException)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("typing_indicator_cancelled");
        }
        catch (Exception)
        {
            // Typing is only a presentation enhancement; never let a transport
            // failure prevent the actual response from reaching the user.
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("typing_indicator_delivery_failed");
        }
    }

    private static void RecordDeliveryOutcome(TeamsDeliveryResult result, double durationMs)
    {
        var telemetry = ChannelTelemetry.For(ChannelType.Teams);
        if (result.IsSuccess)
        {
            telemetry.RecordReplyPosted(durationMs);
            return;
        }

        if (result.Status == TeamsDeliveryStatus.RejectedTooLarge)
        {
            telemetry.RecordReplyRejected(result.ReasonCode);
            return;
        }

        telemetry.RecordReplyFailed(durationMs);
    }

    private static bool IsBoundedActivityId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Encoding.UTF8.GetByteCount(value) <= TeamsSessionIdentifierCodec.MaxRawIdentifierBytes;

    private TeamsOutboundDestination CreateDestination(TeamsInboundActivity activity)
    {
        var serviceUrl = activity.Reply?.ServiceUrl;
        if (string.IsNullOrWhiteSpace(serviceUrl))
            throw new InvalidOperationException("The Teams activity lacks an outbound service URL.");

        return new TeamsOutboundDestination(
            activity.Trust.TenantId,
            activity.Trust.ConversationId,
            activity.Trust.Scope,
            serviceUrl,
            activity.Trust.Scope == TeamsConversationScope.Channel ? activity.Reply?.RootActivityId : null,
            activity.Trust.Scope == TeamsConversationScope.Channel ? activity.TeamId : null,
            activity.Trust.Scope == TeamsConversationScope.Channel ? activity.ChannelId : null,
            activity.Trust.Scope == TeamsConversationScope.Personal ? activity.Trust.SenderId : null);
    }

    private TeamsOutboundMessage CreateMessage(
        TeamsOutboundDestination destination,
        string text,
        string? updateActivityId = null,
        TeamsApprovalCard? approvalCard = null) => new(
        destination,
        text,
        ActivityFingerprint.Create(_sessionId.Value + text),
        ActivityFingerprint.Create(_sessionId.Value),
        destination.Scope == TeamsConversationScope.Channel ? destination.RootActivityId : null,
        updateActivityId,
        approvalCard: approvalCard);

    private void HandleApprovalRequest(ToolInteractionRequest request)
    {
        if (_destination is null || !string.Equals(request.Kind, "approval", StringComparison.Ordinal))
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("approval_card_destination_unavailable");
            return;
        }

        if (_pendingApprovals.Count >= ApprovalCapacity)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("approval_state_capacity_exceeded");
            Self.Tell(new DenyTeamsApprovalRequest(request));
            return;
        }

        var correlationId = TeamsApprovalCardRenderer.CreateCorrelationId();
        var nonce = TeamsApprovalCardRenderer.CreateNonce();
        var expiresAt = _dependencies.TimeProvider.GetUtcNow().AddMinutes(15);
        IReadOnlyList<string> offeredOptionKeys;
        try
        {
            offeredOptionKeys = TeamsApprovalCardRenderer.GetOfferedOptionKeys(request);
        }
        catch (ArgumentException)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("approval_card_invalid_contract");
            Self.Tell(new DenyTeamsApprovalRequest(request));
            return;
        }

        Persist(new TeamsApprovalPendingCreated
        {
            CallId = request.CallId.Value,
            CorrelationId = correlationId,
            NonceHash = TeamsApprovalCardRenderer.HashNonce(nonce),
            RequesterSenderId = request.RequesterSenderId?.Value,
            RequesterPrincipal = request.RequesterPrincipal,
            ExpiresAtUnixMilliseconds = expiresAt.ToUnixTimeMilliseconds(),
            OfferedOptionKeys = offeredOptionKeys,
            IsMcpTool = request.ToolName.IsMcp,
            ToolName = ApprovalDisplayTextFormatter.Truncate(request.ToolName.Value, TeamsApprovalCardRenderer.MaxToolNameChars),
            RequestDisplayText = ApprovalDisplayTextFormatter.Truncate(request.DisplayText, TeamsApprovalCardRenderer.MaxRequestDisplayChars)
        }, created =>
        {
            ApplyApprovalPendingCreated(created);
            Self.Tell(new DeliverTeamsApprovalCard(correlationId, nonce));
        });
    }

    private async Task DeliverApprovalCardAsync(DeliverTeamsApprovalCard delivery)
    {
        if (_destination is null
            || !_pendingApprovals.TryGetValue(delivery.CorrelationId, out var pending)
            // A recovery reissue may supersede an already-queued local send.
            // Only the current opaque nonce binding may be presented.
            || !TeamsApprovalCardRenderer.NonceMatches(pending.NonceHash, delivery.Nonce))
            return;

        TeamsApprovalCard card;
        try
        {
            card = TeamsApprovalCardRenderer.CreatePending(
                CreateApprovalRequest(pending),
                delivery.CorrelationId,
                delivery.Nonce);
        }
        catch (ArgumentException)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("approval_card_invalid_contract");
            return;
        }

        var result = await DeliverAsync(CreateMessage(_destination, "Approval required.", approvalCard: card));
        if (!result.IsSuccess)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("approval_card_delivery_failed");
            MarkApprovalPresentationForRecovery(pending);
            return;
        }

        if (!IsBoundedActivityId(result.ActivityId))
        {
            // Microsoft Teams accepted the card, but did not provide an activity ID
            // for an in-place terminal update. Approval actions remain protected by
            // their one-time nonce, sender, tenant, option, and expiry checks.
            ChannelTelemetry.For(ChannelType.Teams).RecordExtra("approval_card_delivery_unbound");
        }

        Persist(new TeamsApprovalCardDelivered
        {
            CorrelationId = pending.CorrelationId,
            PromptId = IsBoundedActivityId(result.ActivityId) ? result.ActivityId : null
        }, ApplyApprovalCardDelivered);
    }

    private async Task DenyApprovalRequestAsync(DenyTeamsApprovalRequest request)
    {
        try
        {
            await _dependencies.Pipeline.SendFeedbackAsync(new ToolInteractionResponse
            {
                SessionId = _sessionId,
                CallId = request.Request.CallId,
                SelectedKey = ApprovalOptionKeys.DenyKey,
                SenderId = request.Request.RequesterSenderId ?? new SenderId(string.Empty)
            });
        }
        catch (Exception)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("approval_capacity_deny_failed");
        }
    }

    private Task HandleApprovalActionAsync(TeamsBindingApprovalAction received)
    {
        if (received.CancellationToken.IsCancellationRequested)
        {
            Sender.Tell(new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Cancelled));
            return Task.CompletedTask;
        }

        var action = received.Action;
        var replyTo = Sender;
        if (!TryGetExpectedSessionId(action, out var expectedSessionId))
        {
            RejectApprovalAction(replyTo, "approval_action_session_identity_invalid");
            return Task.CompletedTask;
        }

        if (expectedSessionId != _sessionId)
        {
            RejectApprovalAction(replyTo, "approval_action_session_mismatch");
            return Task.CompletedTask;
        }

        if (!TryResolveApprovalDestination(action, out var destination))
        {
            RejectApprovalAction(replyTo, "approval_action_destination_invalid");
            return Task.CompletedTask;
        }

        if (!TeamsApprovalAction.IsSupportedAction(action.Action))
        {
            RejectApprovalAction(replyTo, "approval_action_key_invalid");
            return Task.CompletedTask;
        }

        if (!_pendingApprovals.TryGetValue(action.CorrelationId, out var pending))
        {
            RejectApprovalAction(replyTo, "approval_action_correlation_not_found");
            return Task.CompletedTask;
        }

        // Proactive Teams sends can succeed without returning an activity ID.
        // The one-time nonce remains the required card binding in that case; when
        // an ID was retained, continue requiring the action to target that card.
        if (pending.PromptId is not null
            && !string.Equals(pending.PromptId, action.PromptActivityId, StringComparison.Ordinal))
        {
            RejectApprovalAction(replyTo, "approval_action_prompt_locator_mismatch");
            return Task.CompletedTask;
        }

        if (!TeamsApprovalCardRenderer.NonceMatches(pending.NonceHash, action.Nonce))
        {
            RejectApprovalAction(replyTo, "approval_action_nonce_mismatch");
            return Task.CompletedTask;
        }

        _destination = destination;
        if (!ApprovalButtonValueCodec.CanApprove(
                pending.RequesterPrincipal,
                pending.RequesterSenderId,
                action.Trust.SenderId))
        {
            RejectApprovalAction(replyTo, "approval_action_wrong_requester");
            return Task.CompletedTask;
        }

        if (pending.Decision is not null)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("approval_action_duplicate");
            Sender.Tell(new TeamsApprovalActionResult(
                TeamsApprovalActionDisposition.AlreadyProcessed,
                CreateTerminalCard(pending, "This approval was already processed.")));
            return Task.CompletedTask;
        }

        if (pending.OfferedOptionKeys.Count == 0)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("approval_action_legacy_unavailable");
            Sender.Tell(new TeamsApprovalActionResult(
                TeamsApprovalActionDisposition.Unavailable,
                CreateTerminalCard(pending, "This approval is no longer available.")));
            return Task.CompletedTask;
        }

        if (!pending.OfferedOptionKeys.Contains(action.Action, StringComparer.Ordinal))
        {
            RejectApprovalAction(replyTo, "approval_action_option_not_offered");
            return Task.CompletedTask;
        }

        if (_dependencies.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds() >= pending.ExpiresAtUnixMilliseconds)
        {
            var expiredReplyTo = Sender;
            var nonce = TeamsApprovalCardRenderer.CreateNonce();
            var expiresAt = _dependencies.TimeProvider.GetUtcNow().AddMinutes(15);
            Persist(new TeamsApprovalCardReissued
            {
                CorrelationId = pending.CorrelationId,
                NonceHash = TeamsApprovalCardRenderer.HashNonce(nonce),
                ExpiresAtUnixMilliseconds = expiresAt.ToUnixTimeMilliseconds()
            }, reissued =>
            {
                ApplyApprovalCardReissued(reissued);
                // Card expiry is a transport concern. The session approval
                // remains pending, so a fresh opaque binding replaces the
                // expired nonce instead of manufacturing a core denial.
                Self.Tell(new DeliverTeamsApprovalCard(pending.CorrelationId, nonce));
                expiredReplyTo.Tell(new TeamsApprovalActionResult(
                    TeamsApprovalActionDisposition.Expired,
                    TeamsApprovalCardRenderer.CreateExpired(
                        pending.ToolName,
                        pending.RequestDisplayText,
                        DateTimeOffset.FromUnixTimeMilliseconds(pending.ExpiresAtUnixMilliseconds),
                        pending.IsMcpTool)));
            });
            ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("approval_action_expired");
            return Task.CompletedTask;
        }

        if (pending.ForwardingDecision is not null)
        {
            if (!string.Equals(pending.ForwardingDecision, action.Action, StringComparison.Ordinal))
            {
                RejectApprovalAction(replyTo, "approval_action_retry_mismatch");
                return Task.CompletedTask;
            }

            Self.Tell(new ForwardTeamsApprovalDecision(
                pending.CorrelationId,
                pending.ForwardingDecision,
                pending.ForwardingSenderId!,
                action.OperatorDisplayName,
                action.Nonce,
                Sender));
            return Task.CompletedTask;
        }

        Persist(new TeamsApprovalForwardingStarted
        {
            CorrelationId = pending.CorrelationId,
            Decision = action.Action,
            SenderId = action.Trust.SenderId,
            StartedAtUnixMilliseconds = _dependencies.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        }, forwarding =>
        {
            ApplyApprovalForwardingStarted(forwarding);
            Self.Tell(new ForwardTeamsApprovalDecision(
                pending.CorrelationId,
                action.Action,
                action.Trust.SenderId,
                action.OperatorDisplayName,
                action.Nonce,
                replyTo));
        });
        return Task.CompletedTask;
    }

    private void RejectApprovalAction(IActorRef replyTo, string reasonCode)
    {
        ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered(reasonCode);
        _log.Warning("Teams approval action rejected: reason={0}", reasonCode);
        replyTo.Tell(new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Rejected));
    }

    private async Task ForwardApprovalDecisionAsync(ForwardTeamsApprovalDecision decision)
    {
        if (!_pendingApprovals.TryGetValue(decision.CorrelationId, out var pending)
            || pending.Decision is not null
            || !string.Equals(pending.ForwardingDecision, decision.Action, StringComparison.Ordinal))
        {
            decision.ReplyTo.Tell(new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Unavailable));
            return;
        }

        var result = await _approvalFlow.HandleApprovalResponseWithResultAsync(
            new ToolCallId(pending.CallId),
            decision.Action,
            decision.SenderId,
            pending.PromptId is { } promptId ? new TeamsApprovalPromptId(promptId) : null);
        switch (result)
        {
            case ApprovalResponseDisposition.Accepted:
                PersistApprovalConsumed(
                    pending,
                    decision.Action,
                    consumed =>
                    {
                        ChannelTelemetry.For(ChannelType.Teams).RecordExtra("approval_action_accepted");
                        decision.ReplyTo.Tell(new TeamsApprovalActionResult(
                            TeamsApprovalActionDisposition.Accepted,
                            CreateResolvedApprovalCard(
                                pending,
                                decision.Action,
                                DateTimeOffset.FromUnixTimeMilliseconds(consumed.ConsumedAtUnixMilliseconds),
                                ResolveOperatorDisplayName(decision.SenderId, decision.OperatorDisplayName))));
                    });
                return;

            case ApprovalResponseDisposition.WrongRequester:
                RejectApprovalAction(decision.ReplyTo, "approval_action_wrong_requester");
                return;

            case ApprovalResponseDisposition.NoLongerPending:
                PersistApprovalConsumed(
                    pending,
                    decision.Action,
                    _ => decision.ReplyTo.Tell(new TeamsApprovalActionResult(
                        TeamsApprovalActionDisposition.Unavailable,
                        CreateTerminalCard(pending, "This approval is no longer available."))));
                return;

            case ApprovalResponseDisposition.FeedbackFailed:
                ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("approval_feedback_failed");
                ReplyWithRetryableApproval(pending, decision);
                return;

            default:
                ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("approval_action_session_rejected");
                ReplyWithRetryableApproval(pending, decision);
                return;
        }
    }

    private void PersistApprovalConsumed(
        TeamsPendingApproval pending,
        string decision,
        Action<TeamsApprovalConsumed> onPersisted)
    {
        Persist(new TeamsApprovalConsumed
        {
            CorrelationId = pending.CorrelationId,
            Decision = decision,
            ConsumedAtUnixMilliseconds = _dependencies.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        }, consumed =>
        {
            ApplyApprovalConsumed(consumed);
            onPersisted(consumed);
        });
    }

    private void ReplyWithRetryableApproval(
        TeamsPendingApproval pending,
        ForwardTeamsApprovalDecision decision)
    {
        if (decision.Nonce is { } nonce)
        {
            decision.ReplyTo.Tell(new TeamsApprovalActionResult(
                TeamsApprovalActionDisposition.Unavailable,
                TeamsApprovalCardRenderer.CreatePending(CreateApprovalRequest(pending), pending.CorrelationId, nonce)));
            return;
        }

        var replacementNonce = TeamsApprovalCardRenderer.CreateNonce();
        var replacementExpiry = _dependencies.TimeProvider.GetUtcNow().AddMinutes(15);
        Persist(new TeamsApprovalCardReissued
        {
            CorrelationId = pending.CorrelationId,
            NonceHash = TeamsApprovalCardRenderer.HashNonce(replacementNonce),
            ExpiresAtUnixMilliseconds = replacementExpiry.ToUnixTimeMilliseconds()
        }, reissued =>
        {
            ApplyApprovalCardReissued(reissued);
            Self.Tell(new DeliverTeamsApprovalCard(pending.CorrelationId, replacementNonce));
        });
    }

    private void RecoverPendingApprovalForwards()
    {
        foreach (var pending in _pendingApprovals.Values
                     .Where(static pending => pending.Decision is null && pending.ForwardingDecision is not null)
                     .ToArray())
        {
            Self.Tell(new ForwardTeamsApprovalDecision(
                pending.CorrelationId,
                pending.ForwardingDecision!,
                pending.ForwardingSenderId!,
                OperatorDisplayName: null,
                Nonce: null,
                ReplyTo: ActorRefs.Nobody));
        }
    }

    private void RecoverPendingApprovalPresentations()
    {
        foreach (var pending in _pendingApprovals.Values
                     .Where(static pending => pending.Decision is null
                                             && pending.ForwardingDecision is null
                                             && pending.PresentationPending)
                     .ToArray())
        {
            ReissueApprovalCardForPresentation(pending, deliver: true);
        }
    }

    private void MarkApprovalPresentationForRecovery(TeamsPendingApproval pending) =>
        ReissueApprovalCardForPresentation(pending, deliver: false);

    private void ReissueApprovalCardForPresentation(TeamsPendingApproval pending, bool deliver)
    {
        var nonce = TeamsApprovalCardRenderer.CreateNonce();
        var expiry = _dependencies.TimeProvider.GetUtcNow().AddMinutes(15);
        Persist(new TeamsApprovalCardReissued
        {
            CorrelationId = pending.CorrelationId,
            NonceHash = TeamsApprovalCardRenderer.HashNonce(nonce),
            ExpiresAtUnixMilliseconds = expiry.ToUnixTimeMilliseconds()
        }, reissued =>
        {
            ApplyApprovalCardReissued(reissued);
            if (deliver)
                Self.Tell(new DeliverTeamsApprovalCard(pending.CorrelationId, nonce));
        });
    }

    private static TeamsApprovalCard CreateTerminalCard(TeamsPendingApproval pending, string text) =>
        TeamsApprovalCardRenderer.CreateTerminal(
            pending.ToolName,
            pending.RequestDisplayText,
            text,
            pending.IsMcpTool);

    private static TeamsApprovalCard CreateResolvedApprovalCard(
        TeamsPendingApproval pending,
        string selectedKey,
        DateTimeOffset resolvedAt,
        string? operatorDisplayName) =>
        selectedKey == ApprovalOptionKeys.Deny
            ? TeamsApprovalCardRenderer.CreateDenied(
                pending.ToolName,
                pending.RequestDisplayText,
                resolvedAt,
                pending.IsMcpTool,
                operatorDisplayName)
            : TeamsApprovalCardRenderer.CreateGranted(
                pending.ToolName,
                pending.RequestDisplayText,
                selectedKey,
                resolvedAt,
                pending.IsMcpTool,
                operatorDisplayName);

    private string? ResolveOperatorDisplayName(string senderId, string? callbackDisplayName)
        => TeamsApprovalAction.NormalizeOperatorDisplayName(callbackDisplayName)
           ?? _dependencies.CachedOperatorLabel?.Invoke(senderId);

    private ToolInteractionRequest CreateApprovalRequest(TeamsPendingApproval pending) => new()
    {
        SessionId = _sessionId,
        Kind = "approval",
        CallId = new ToolCallId(pending.CallId),
        ToolName = new ToolName(pending.ToolName),
        DisplayText = pending.RequestDisplayText,
        Options = (pending.ForwardingDecision is { } forwardingDecision
                ? new[] { forwardingDecision }
                : pending.OfferedOptionKeys)
            .Select(key => new ToolInteractionOption(
            new ApprovalOptionKey(key),
            ApprovalOptionKeys.LabelFor(key, pending.IsMcpTool))).ToArray(),
        RequesterSenderId = pending.RequesterSenderId is { } senderId ? new SenderId(senderId) : null,
        RequesterPrincipal = pending.RequesterPrincipal
    };

    private bool TryGetExpectedSessionId(TeamsApprovalAction action, out SessionId sessionId)
    {
        if (action.Trust.Scope == TeamsConversationScope.Personal)
        {
            return TeamsSessionIdentifierCodec.TryCreatePersonal(
                action.Trust.TenantId,
                action.Trust.ConversationId,
                out sessionId,
                out _);
        }

        if (action.Trust.Scope == TeamsConversationScope.GroupChat)
        {
            return TeamsSessionIdentifierCodec.TryCreateGroupChat(
                action.Trust.TenantId,
                action.Trust.ConversationId,
                out sessionId,
                out _);
        }

        if (action.Trust.Scope == TeamsConversationScope.Channel
            && action.RootActivityId is { } rootActivityId)
        {
            return TeamsSessionIdentifierCodec.TryCreateChannel(
                action.Trust.TenantId,
                action.Trust.ConversationId,
                rootActivityId,
                out sessionId,
                out _);
        }

        sessionId = default;
        return false;
    }

    private static bool TryCreateDestination(TeamsApprovalAction action, out TeamsOutboundDestination destination)
    {
        try
        {
            destination = new TeamsOutboundDestination(
                action.Trust.TenantId,
                action.Trust.ConversationId,
                action.Trust.Scope,
                action.ServiceUrl,
                action.Trust.Scope == TeamsConversationScope.Channel ? action.RootActivityId : null,
                action.Trust.Scope == TeamsConversationScope.Channel ? action.TeamId : null,
                action.Trust.Scope == TeamsConversationScope.Channel ? action.ChannelId : null,
                action.Trust.Scope == TeamsConversationScope.Personal ? action.Trust.SenderId : null);
            return true;
        }
        catch (ArgumentException)
        {
            destination = null!;
            return false;
        }
    }

    private bool TryResolveApprovalDestination(TeamsApprovalAction action, out TeamsOutboundDestination destination)
    {
        if (action.Trust.Scope == TeamsConversationScope.Personal)
            return TryCreateDestination(action, out destination);

        if (action.Trust.Scope == TeamsConversationScope.GroupChat)
        {
            destination = null!;
            if (_destination is not { } groupChatDestination
                || groupChatDestination.Scope != TeamsConversationScope.GroupChat
                || !TeamsOutboundDestination.IsValidServiceUrl(action.ServiceUrl)
                || !string.Equals(groupChatDestination.TenantId, action.Trust.TenantId, StringComparison.Ordinal)
                || !string.Equals(groupChatDestination.ConversationId, action.Trust.ConversationId, StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                var trustedDestinationActivity = new TeamsInboundActivity(
                    action.Trust,
                    string.Empty,
                    new TeamsReplyMetadata(action.PromptActivityId, null, groupChatDestination.ServiceUrl),
                    isMentioned: true,
                    kind: TeamsIngressActivityKind.AdaptiveCardAction);
                if (TeamsGroupChatAclPolicy.EvaluateStructuralAccess(trustedDestinationActivity, _dependencies.Options).IsAllowed)
                    destination = groupChatDestination;
            }
            catch (ArgumentException)
            {
                return false;
            }

            return destination is not null;
        }

        destination = null!;
        if (action.Trust.Scope != TeamsConversationScope.Channel
            || _destination is not { } persistedDestination
            || persistedDestination.Scope != TeamsConversationScope.Channel
            || !TeamsOutboundDestination.IsValidServiceUrl(action.ServiceUrl)
            || !string.Equals(persistedDestination.TenantId, action.Trust.TenantId, StringComparison.Ordinal)
            || !string.Equals(persistedDestination.ConversationId, action.Trust.ConversationId, StringComparison.Ordinal)
            || !string.Equals(persistedDestination.RootActivityId, action.RootActivityId, StringComparison.Ordinal)
            || ((action.TeamId is null) != (action.ChannelId is null)))
        {
            return false;
        }

        if (action.TeamId is not null
            && (!TryCreateDestination(action, out var callbackDestination)
                || !string.Equals(callbackDestination.TeamId, persistedDestination.TeamId, StringComparison.Ordinal)
                || !string.Equals(callbackDestination.ChannelId, persistedDestination.ChannelId, StringComparison.Ordinal)))
        {
            return false;
        }

        try
        {
            var trustedDestinationActivity = new TeamsInboundActivity(
                action.Trust,
                string.Empty,
                new TeamsReplyMetadata(action.PromptActivityId, action.RootActivityId, persistedDestination.ServiceUrl),
                isMentioned: true,
                kind: TeamsIngressActivityKind.AdaptiveCardAction,
                teamId: persistedDestination.TeamId,
                channelId: persistedDestination.ChannelId);
            var channelPolicy = TeamsPrincipalRequirements.Resolve(trustedDestinationActivity, _dependencies.Options).HasRestriction
                ? TeamsChannelAclPolicy.EvaluateStructuralAccess(trustedDestinationActivity, _dependencies.Options)
                : TeamsChannelAclPolicy.EvaluateAccess(trustedDestinationActivity, _dependencies.Options);
            if (channelPolicy.Disposition != TeamsChannelPolicyDisposition.Allowed)
            {
                return false;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        destination = persistedDestination;
        return true;
    }

    private void ApplyApprovalPendingCreated(TeamsApprovalPendingCreated created)
    {
        if (!TeamsApprovalAction.IsBoundedOpaqueValue(created.CorrelationId, TeamsApprovalAction.MaxCorrelationLength)
            || created.NonceHash.Length != 64
            || created.NonceHash.Any(static character => !char.IsAsciiHexDigit(character))
            || created.ExpiresAtUnixMilliseconds <= 0
            || string.IsNullOrWhiteSpace(created.CallId)
            || !HasValidOfferedOptionKeys(created.OfferedOptionKeys)
            || !HasValidApprovalPresentation(created.ToolName, created.RequestDisplayText))
        {
            throw new InvalidOperationException("The Teams approval state is invalid.");
        }

        if (_pendingApprovals.Count >= ApprovalCapacity && !_pendingApprovals.ContainsKey(created.CorrelationId))
            throw new InvalidOperationException("The Teams approval state exceeds its retention limit.");

        var pending = new TeamsPendingApproval(
            created.CallId,
            created.CorrelationId,
            created.NonceHash,
            created.RequesterSenderId,
            created.RequesterPrincipal,
            created.ExpiresAtUnixMilliseconds,
            created.OfferedOptionKeys.ToArray(),
            created.IsMcpTool,
            created.ToolName,
            created.RequestDisplayText,
            PresentationPending: created.PresentationPending);
        if (!_pendingApprovals.TryAdd(created.CorrelationId, pending))
        {
            throw new InvalidOperationException("The Teams approval state contains a duplicate correlation.");
        }

        _pendingApprovalRequests.Add(new PendingApprovalRequest<TeamsApprovalPromptId>(
            new ToolCallId(pending.CallId),
            pending.RequesterSenderId,
            pending.RequesterPrincipal,
            pending.OfferedOptionKeys,
            promptId: null,
            toolName: pending.ToolName,
            displayText: pending.RequestDisplayText));
    }

    private void ApplyApprovalCardDelivered(TeamsApprovalCardDelivered delivered)
    {
        if (!_pendingApprovals.TryGetValue(delivered.CorrelationId, out var pending)
            || (delivered.PromptId is not null && !IsBoundedActivityId(delivered.PromptId)))
        {
            throw new InvalidOperationException("The Teams approval card locator is invalid.");
        }

        _pendingApprovals[delivered.CorrelationId] = pending with
        {
            PromptId = delivered.PromptId,
            PresentationPending = false
        };
        var shared = _pendingApprovalRequests.FirstOrDefault(item => item.CallId.Value == pending.CallId);
        if (shared is not null && delivered.PromptId is not null)
            shared.PromptId = new TeamsApprovalPromptId(delivered.PromptId);
    }

    private void ApplyApprovalCardReissued(TeamsApprovalCardReissued reissued)
    {
        if (!_pendingApprovals.TryGetValue(reissued.CorrelationId, out var pending)
            || reissued.NonceHash.Length != 64
            || reissued.NonceHash.Any(static character => !char.IsAsciiHexDigit(character))
            || reissued.ExpiresAtUnixMilliseconds <= 0
            || pending.Decision is not null)
        {
            throw new InvalidOperationException("The Teams approval card replacement state is invalid.");
        }

        _pendingApprovals[reissued.CorrelationId] = pending with
        {
            NonceHash = reissued.NonceHash,
            ExpiresAtUnixMilliseconds = reissued.ExpiresAtUnixMilliseconds,
            PromptId = null,
            PresentationPending = true
        };
        var shared = _pendingApprovalRequests.FirstOrDefault(item => item.CallId.Value == pending.CallId);
        if (shared is not null)
            shared.PromptId = null;
    }

    private void ApplyApprovalConsumed(TeamsApprovalConsumed consumed)
    {
        if (!_pendingApprovals.TryGetValue(consumed.CorrelationId, out var pending)
            || !IsValidPersistedDecision(pending, consumed.Decision))
        {
            throw new InvalidOperationException("The Teams approval terminal state is invalid.");
        }

        _pendingApprovals[consumed.CorrelationId] = pending with
        {
            ForwardingDecision = null,
            ForwardingSenderId = null,
            Decision = consumed.Decision
        };
    }

    private void ApplyApprovalForwardingStarted(TeamsApprovalForwardingStarted forwarding)
    {
        if (!_pendingApprovals.TryGetValue(forwarding.CorrelationId, out var pending)
            || pending.Decision is not null
            || pending.ForwardingDecision is not null
            || !IsValidPersistedDecision(pending, forwarding.Decision)
            || string.IsNullOrWhiteSpace(forwarding.SenderId)
            || forwarding.StartedAtUnixMilliseconds <= 0)
        {
            throw new InvalidOperationException("The Teams approval forwarding state is invalid.");
        }

        _pendingApprovals[forwarding.CorrelationId] = pending with
        {
            ForwardingDecision = forwarding.Decision,
            ForwardingSenderId = forwarding.SenderId
        };
    }

    private static bool HasValidOfferedOptionKeys(IReadOnlyList<string> optionKeys)
    {
        if (optionKeys.Count > TeamsApprovalCardRenderer.MaxOptionCount)
            return false;

        var keys = new HashSet<string>(StringComparer.Ordinal);
        return optionKeys.All(key => TeamsApprovalAction.IsSupportedAction(key) && keys.Add(key));
    }

    private static bool HasValidApprovalPresentation(string toolName, string requestDisplayText) =>
        string.IsNullOrEmpty(toolName) && string.IsNullOrEmpty(requestDisplayText)
        || (!string.IsNullOrWhiteSpace(toolName)
            && !string.IsNullOrWhiteSpace(requestDisplayText)
            && toolName.Length <= TeamsApprovalCardRenderer.MaxToolNameChars
            && requestDisplayText.Length <= TeamsApprovalCardRenderer.MaxRequestDisplayChars);

    private static bool IsValidPersistedDecision(TeamsPendingApproval pending, string decision) =>
        decision == "expired"
        || pending.OfferedOptionKeys.Contains(decision, StringComparer.Ordinal)
        || (pending.OfferedOptionKeys.Count == 0 && decision is "approve" or "deny");

    private static void EnsureValidFingerprint(string fingerprint)
    {
        if (fingerprint.Length != 64 || fingerprint.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) || !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidOperationException("The Teams processed activity state contains an invalid fingerprint.");
        }
    }

    private sealed record DispatchReservedActivity(
        TeamsInboundActivity Activity,
        string ActivityFingerprint,
        IActorRef ReplyTo,
        CancellationToken CancellationToken) : INoSerializationVerificationNeeded;

    private sealed record ReleaseReservedActivity(
        DispatchReservedActivity Dispatch,
        TeamsBindingRouteDisposition Disposition) : INoSerializationVerificationNeeded;

    private sealed record BeginTeamsReminderDispatch(
        DeliverTrustedSessionTurn Reminder,
        IActorRef ReplyTo,
        string DeliveryKey) : INoSerializationVerificationNeeded;

    private sealed record DispatchTeamsReminder(
        DeliverTrustedSessionTurn Reminder,
        IActorRef ReplyTo,
        string DeliveryKey) : INoSerializationVerificationNeeded;

    private sealed record CompleteReminderDispatchFailure(
        string DeliveryKey,
        IActorRef ReplyTo,
        string FailureReason,
        string ReplyMessage) : INoSerializationVerificationNeeded;

    private sealed record MarkRecoveredProactiveDeliveriesUnknown : INoSerializationVerificationNeeded;

    private sealed record SaveTeamsMigrationSnapshot : INoSerializationVerificationNeeded;

    private sealed record OutputStreamTerminated(
        int Generation,
        Exception? Cause) : INoSerializationVerificationNeeded;

    private sealed record ReinitializePipeline : INoSerializationVerificationNeeded;

    private sealed record DeliverTeamsApprovalCard(
        string CorrelationId,
        string Nonce) : INoSerializationVerificationNeeded;

    private sealed record DenyTeamsApprovalRequest(ToolInteractionRequest Request) : INoSerializationVerificationNeeded;

    private sealed record ForwardTeamsApprovalDecision(
        string CorrelationId,
        string Action,
        string SenderId,
        string? OperatorDisplayName,
        string? Nonce,
        IActorRef ReplyTo) : INoSerializationVerificationNeeded;

    private sealed record RecoverPendingApprovalForwardsCommand : INoSerializationVerificationNeeded;

    private sealed record RecoverPendingApprovalPresentationsCommand : INoSerializationVerificationNeeded;

    private sealed record TeamsPendingApproval(
        string CallId,
        string CorrelationId,
        string NonceHash,
        string? RequesterSenderId,
        PrincipalClassification? RequesterPrincipal,
        long ExpiresAtUnixMilliseconds,
        IReadOnlyList<string> OfferedOptionKeys,
        bool IsMcpTool,
        string ToolName,
        string RequestDisplayText,
        bool PresentationPending = true,
        string? PromptId = null,
        string? ForwardingDecision = null,
        string? ForwardingSenderId = null,
        string? Decision = null);

    private sealed class TeamsDeliveryException(TeamsDeliveryResult result) : Exception(
        result.ReasonCode ?? $"Teams delivery returned {result.Status}.");

    internal sealed record BindingOutput(SessionOutput Output) : INoSerializationVerificationNeeded;

}

internal static class ActivityFingerprint
{
    public static string Create(string activityId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(activityId)));
}
