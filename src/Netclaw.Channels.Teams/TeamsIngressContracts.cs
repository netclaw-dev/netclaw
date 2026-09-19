// -----------------------------------------------------------------------
// <copyright file="TeamsIngressContracts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Configuration;

namespace Netclaw.Channels.Teams;

public enum TeamsIngressActivityKind
{
    Unknown,
    Message,
    AdaptiveCardAction,
    ConversationUpdate,
    MessageUpdate,
    MessageDelete
}

public enum TeamsTranslationDisposition
{
    Accepted,
    Ignored,
    RejectedMalformed,
    RejectedUnsupportedScope,
    RejectedPendingTenantEvidence
}

/// <summary>
/// SDK-free structured mention data. The transport boundary preserves the
/// literal entity text so policy can remove only verified bot spans.
/// </summary>
public sealed record TeamsMention(string Type, string MentionedId, string Text);

/// <summary>
/// SDK-free translation outcome. Reason codes are stable diagnostics only and
/// must never contain activity content, tokens, or platform identifiers.
/// </summary>
public sealed record TeamsTranslationResult(
    TeamsTranslationDisposition Disposition,
    string ReasonCode,
    TeamsIngressActivityKind ActivityKind,
    TeamsInboundActivity? Activity = null,
    TeamsApprovalAction? ApprovalAction = null)
{
    public static TeamsTranslationResult Accepted(TeamsInboundActivity activity, string reasonCode = "accepted")
        => new(TeamsTranslationDisposition.Accepted, reasonCode, activity.Kind, activity);

    public static TeamsTranslationResult Accepted(TeamsApprovalAction action)
        => new(TeamsTranslationDisposition.Accepted, "accepted", TeamsIngressActivityKind.AdaptiveCardAction, ApprovalAction: action);

    public static TeamsTranslationResult Ignored(TeamsIngressActivityKind kind, string reasonCode)
        => new(TeamsTranslationDisposition.Ignored, reasonCode, kind);

    public static TeamsTranslationResult Rejected(
        TeamsTranslationDisposition disposition,
        TeamsIngressActivityKind kind,
        string reasonCode)
        => new(disposition, reasonCode, kind);
}

/// <summary>
/// Immutable, SDK-free trust context that the future Teams translator supplies
/// only after its authenticated HTTP boundary has validated required identity.
/// </summary>
public sealed record TeamsIngressTrustContext
{
    public TeamsIngressTrustContext(
        TrustAudience audience,
        PrincipalClassification principal,
        TrustBoundary boundary,
        SourceProvenance provenance,
        string senderId,
        string tenantId,
        string conversationId,
        TeamsConversationScope scope,
        string activityId,
        DateTimeOffset receivedAtUtc,
        DateTimeOffset? platformTimestampUtc = null)
    {
        Audience = ValidateEnum(audience, nameof(audience));
        Principal = ValidateEnum(principal, nameof(principal));
        Boundary = string.IsNullOrWhiteSpace(boundary.Value)
            ? throw new ArgumentException("Trust boundary is required.", nameof(boundary))
            : boundary;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        SenderId = RequireValue(senderId, nameof(senderId));
        TenantId = RequireValue(tenantId, nameof(tenantId));
        ConversationId = RequireValue(conversationId, nameof(conversationId));
        Scope = ValidateScope(scope);
        ActivityId = RequireValue(activityId, nameof(activityId));
        ReceivedAtUtc = receivedAtUtc == default
            ? throw new ArgumentException("Received timestamp is required.", nameof(receivedAtUtc))
            : receivedAtUtc;
        PlatformTimestampUtc = platformTimestampUtc;
    }

    public TrustAudience Audience { get; }

    public PrincipalClassification Principal { get; }

    public TrustBoundary Boundary { get; }

    public SourceProvenance Provenance { get; }

    public string SenderId { get; }

    public string TenantId { get; }

    public string ConversationId { get; }

    public TeamsConversationScope Scope { get; }

    public string ActivityId { get; }

    public DateTimeOffset ReceivedAtUtc { get; }

    /// <summary>
    /// Optional Teams-supplied event time. Receipt time remains local so the
    /// SDK payload cannot influence daemon ordering or retention decisions.
    /// </summary>
    public DateTimeOffset? PlatformTimestampUtc { get; }

    private static T ValidateEnum<T>(T value, string parameterName)
        where T : struct, Enum
        => Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, value, "Unsupported value.");

    private static TeamsConversationScope ValidateScope(TeamsConversationScope scope)
        => scope is TeamsConversationScope.Personal or TeamsConversationScope.Channel or TeamsConversationScope.GroupChat
            ? scope
            : throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported Teams conversation scope.");

    private static string RequireValue(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A nonblank value is required.", parameterName)
            : value;
}

public enum TeamsInboundAttachmentKind
{
    Unknown,
    InlineImage,
    PersonalFile
}

/// <summary>
/// Sanitized attachment metadata. URLs, authorization data, and SDK objects
/// never enter this contract, actor state, telemetry, or persistence.
/// </summary>
public sealed record TeamsAttachmentMetadata
{
    public TeamsAttachmentMetadata(string name, string? contentType, long? declaredSizeBytes)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Attachment name is required.", nameof(name))
            : name;
        ContentType = contentType;
        DeclaredSizeBytes = declaredSizeBytes is < 0
            ? throw new ArgumentOutOfRangeException(nameof(declaredSizeBytes), "Attachment size cannot be negative.")
            : declaredSizeBytes;
    }

    public string Name { get; }

    public string? ContentType { get; }

    public long? DeclaredSizeBytes { get; }

    public TeamsInboundAttachmentKind Kind { get; init; }

    public int SourceIndex { get; init; } = -1;
}

/// <summary>
/// Downloads a previously captured authenticated SDK attachment. The raw URL
/// and any SDK token remain private to the daemon hosting boundary.
/// </summary>
public interface ITeamsAttachmentDownloader
{
    Task<AttachmentDownloadResult> DownloadAsync(
        TeamsInboundActivity activity,
        TeamsAttachmentMetadata attachment,
        string stagingDirectory,
        long maximumBytes,
        CancellationToken cancellationToken);
}

public sealed record TeamsReplyMetadata(
    string? ReplyToActivityId,
    string? RootActivityId,
    string? ServiceUrl = null);

public sealed record TeamsInboundActivity
{
    public TeamsInboundActivity(
        TeamsIngressTrustContext trust,
        string text,
        TeamsReplyMetadata? reply = null,
        bool isMentioned = false,
        ImmutableArray<TeamsAttachmentMetadata> attachments = default,
        TeamsIngressActivityKind kind = TeamsIngressActivityKind.Message,
        string? teamId = null,
        string? channelId = null,
        ImmutableArray<TeamsMention> mentions = default,
        TeamsIngressAuthorization? authorization = null)
    {
        Trust = trust ?? throw new ArgumentNullException(nameof(trust));
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Reply = reply;
        IsMentioned = isMentioned;
        Attachments = attachments.IsDefault ? [] : attachments;
        Kind = kind is TeamsIngressActivityKind.Message
            or TeamsIngressActivityKind.AdaptiveCardAction
            or TeamsIngressActivityKind.MessageUpdate
            or TeamsIngressActivityKind.MessageDelete
            ? kind
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Teams ingress activity kind.");
        TeamId = teamId;
        ChannelId = channelId;
        Mentions = mentions.IsDefault ? [] : mentions;
        Authorization = authorization;
    }

    public TeamsIngressTrustContext Trust { get; }

    public string Text { get; }

    public TeamsReplyMetadata? Reply { get; }

    public bool IsMentioned { get; }

    public ImmutableArray<TeamsAttachmentMetadata> Attachments { get; }

    public TeamsIngressActivityKind Kind { get; }

    public string? TeamId { get; }

    public string? ChannelId { get; }

    public ImmutableArray<TeamsMention> Mentions { get; }

    /// <summary>
    /// An in-memory ingress authorization handoff. SDK translation never sets
    /// this value; only the local asynchronous routing edge may attach one.
    /// </summary>
    public TeamsIngressAuthorization? Authorization { get; }

    public TeamsInboundActivity WithAuthorization(TeamsIngressAuthorization authorization) =>
        new(
            Trust,
            Text,
            Reply,
            IsMentioned,
            Attachments,
            Kind,
            TeamId,
            ChannelId,
            Mentions,
            authorization);
}

/// <summary>
/// SDK-free card action. The daemon creates this record only after the Teams
/// SDK authenticates the invoke and validates its bounded action data.
/// </summary>
public sealed record TeamsApprovalAction(
    TeamsIngressTrustContext Trust,
    string CorrelationId,
    string Nonce,
    string Action,
    string? RootActivityId,
    string? TeamId,
    string? ChannelId,
    string? PromptActivityId,
    string ServiceUrl,
    string? OperatorDisplayName = null)
{
    public const int MaxCorrelationLength = 128;
    public const int MaxNonceLength = 128;
    public const int MaxActionLength = 64;
    public const int MaxOperatorDisplayNameLength = 128;

    public bool IsChannel => Trust.Scope == TeamsConversationScope.Channel;

    /// <summary>
    /// Validates the bounded wire shape of a session-supplied approval key.
    /// The binding actor checks membership in its persisted offered-key set.
    /// This contract must not contain an independent approval-policy list.
    /// </summary>
    public static bool IsSupportedAction(string? action) =>
        !string.IsNullOrWhiteSpace(action)
        && action.Length <= MaxActionLength
        && action.All(static character => character is >= 'a' and <= 'z'
                                           || char.IsAsciiDigit(character)
                                           || character is '-' or '_');

    public static bool IsBoundedOpaqueValue(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary>
    /// Normalizes an optional Teams-supplied presenter label for terminal-card
    /// display. It is never an authorization or persistence value.
    /// </summary>
    public static string? NormalizeOperatorDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)
            || displayName.Any(static character => char.IsControl(character)
                                                || char.GetUnicodeCategory(character) == UnicodeCategory.Format))
        {
            return null;
        }

        var normalized = displayName.Trim();
        return string.IsNullOrWhiteSpace(normalized) || Guid.TryParse(normalized, out _)
            ? null
            : ApprovalDisplayTextFormatter.Truncate(normalized, MaxOperatorDisplayNameLength);
    }
}

/// <summary>
/// SDK-free Adaptive Card output. It contains only the opaque approval values
/// needed for an Action.Execute callback and safe display text.
/// </summary>
public sealed record TeamsApprovalCard(
    string Title,
    string Body,
    IReadOnlyList<TeamsApprovalCardAction> Actions,
    TeamsApprovalCardTone Tone = TeamsApprovalCardTone.Default)
{
    public const string Schema = "http://adaptivecards.io/schemas/adaptive-card.json";
    public const string Version = "1.5";
    public const int MaxSerializedBytes = 80 * 1024;

    /// <summary>
    /// Structured request fields for an Adaptive Card host.
    /// The transport uses these fields to keep labels distinct from code values.
    /// </summary>
    public IReadOnlyList<TeamsApprovalCardField> Fields { get; init; } = [];

    /// <summary>
    /// A terminal or contextual summary for an Adaptive Card host.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Fluent icon name for the card header. This is presentation-only.
    /// </summary>
    public string IconName { get; init; } = "Info";

    /// <summary>
    /// Semantic banner text below the card header.
    /// </summary>
    public string? Banner { get; init; }

    /// <summary>
    /// Centered terminal status text. Pending cards leave this value empty.
    /// </summary>
    public string? Footer { get; init; }

    /// <summary>
    /// Screen-reader text that excludes opaque callback material.
    /// </summary>
    public string? Speak { get; init; }
}

/// <summary>
/// A safe display field for a Teams approval card.
/// It has no authorization meaning.
/// </summary>
public sealed record TeamsApprovalCardField(string Label, string Value);

/// <summary>
/// The Adaptive Card title tone. This is presentation-only and has no bearing
/// on which approval actions are allowed.
/// </summary>
public enum TeamsApprovalCardTone
{
    Default,
    Accent,
    Good,
    Warning,
    Attention
}

/// <summary>
/// The Adaptive Card action styles that represent the shared approval choices.
/// The style is display-only. The persisted offered key remains the authority.
/// </summary>
public enum TeamsApprovalActionStyle
{
    Default,
    Positive,
    Destructive
}

public sealed record TeamsApprovalCardAction(
    string Title,
    string Action,
    string CorrelationId,
    string Nonce,
    TeamsApprovalActionStyle Style);

/// <summary>
/// SDK-free destination captured only after a Teams activity passes the future
/// authentication and ACL gates. It deliberately stores no credentials or
/// serialized SDK conversation reference.
/// </summary>
public sealed record TeamsOutboundDestination
{
    public const int MaxServiceUrlLength = 2_048;

    public TeamsOutboundDestination(
        string tenantId,
        string conversationId,
        TeamsConversationScope scope,
        string serviceUrl,
        string? rootActivityId = null,
        string? teamId = null,
        string? channelId = null,
        string? userId = null)
    {
        TenantId = RequireIdentifier(tenantId, nameof(tenantId));
        ConversationId = RequireIdentifier(conversationId, nameof(conversationId));
        Scope = scope is TeamsConversationScope.Personal or TeamsConversationScope.Channel or TeamsConversationScope.GroupChat
            ? scope
            : throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported Teams conversation scope.");
        if (scope == TeamsConversationScope.GroupChat
            && (rootActivityId is not null || teamId is not null || channelId is not null || userId is not null))
        {
            throw new ArgumentException("A group chat destination cannot contain channel or personal routing values.", nameof(rootActivityId));
        }
        ServiceUrl = RequireServiceUrl(serviceUrl);
        RootActivityId = scope == TeamsConversationScope.Channel
            ? RequireIdentifier(rootActivityId!, nameof(rootActivityId))
            : null;
        TeamId = scope == TeamsConversationScope.Channel
            ? RequireIdentifier(teamId!, nameof(teamId))
            : null;
        ChannelId = scope == TeamsConversationScope.Channel
            ? RequireIdentifier(channelId!, nameof(channelId))
            : null;
        UserId = scope == TeamsConversationScope.Personal
            ? RequireIdentifier(userId!, nameof(userId))
            : null;
    }

    public string TenantId { get; }

    public string ConversationId { get; }

    public TeamsConversationScope Scope { get; }

    /// <summary>
    /// The authenticated Teams service endpoint. This is runtime address data,
    /// not a credential. Actors never log it or retain an SDK client.
    /// </summary>
    public string ServiceUrl { get; }

    public string? RootActivityId { get; }

    public string? TeamId { get; }

    public string? ChannelId { get; }

    public string? UserId { get; }

    public static bool IsValidServiceUrl(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && Encoding.UTF8.GetByteCount(value) <= MaxServiceUrlLength
           && Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && string.IsNullOrEmpty(uri.UserInfo)
           && string.IsNullOrEmpty(uri.Query)
           && string.IsNullOrEmpty(uri.Fragment);

    private static string RequireIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Encoding.UTF8.GetByteCount(value) > TeamsSessionIdentifierCodec.MaxRawIdentifierBytes)
        {
            throw new ArgumentException("A bounded nonblank value is required.", parameterName);
        }

        return value;
    }

    private static string RequireServiceUrl(string value)
    {
        if (!IsValidServiceUrl(value))
        {
            throw new ArgumentException("A bounded HTTPS service URL is required.", nameof(value));
        }

        return new Uri(value, UriKind.Absolute).AbsoluteUri;
    }
}

public sealed record TeamsOutboundMessage
{
    public TeamsOutboundMessage(
        TeamsOutboundDestination destination,
        string text,
        string idempotencyKey,
        string correlationId,
        string? replyToActivityId = null,
        string? updateActivityId = null,
        ImmutableArray<TeamsAttachmentMetadata> attachments = default,
        TeamsApprovalCard? approvalCard = null)
    {
        Destination = destination ?? throw new ArgumentNullException(nameof(destination));
        Text = string.IsNullOrWhiteSpace(text) ? throw new ArgumentException("Text is required.", nameof(text)) : text;
        ReplyToActivityId = RequireOptionalIdentifier(replyToActivityId, nameof(replyToActivityId));
        UpdateActivityId = RequireOptionalIdentifier(updateActivityId, nameof(updateActivityId));
        if (Destination.Scope == TeamsConversationScope.Channel
            && !string.Equals(ReplyToActivityId, Destination.RootActivityId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Channel output must target the canonical root activity.", nameof(replyToActivityId));
        }
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey)) : idempotencyKey;
        CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? throw new ArgumentException("Correlation ID is required.", nameof(correlationId)) : correlationId;
        Attachments = attachments.IsDefault ? [] : attachments;
        ApprovalCard = approvalCard;
    }

    public TeamsOutboundDestination Destination { get; }

    public string Text { get; }

    public string? ReplyToActivityId { get; }

    public string? UpdateActivityId { get; }

    public string IdempotencyKey { get; }

    public string CorrelationId { get; }

    public ImmutableArray<TeamsAttachmentMetadata> Attachments { get; }

    public TeamsApprovalCard? ApprovalCard { get; }

    private static string? RequireOptionalIdentifier(string? value, string parameterName)
    {
        if (value is not null
            && (string.IsNullOrWhiteSpace(value)
                || Encoding.UTF8.GetByteCount(value) > TeamsSessionIdentifierCodec.MaxRawIdentifierBytes))
        {
            throw new ArgumentException("An optional identifier must be bounded when present.", parameterName);
        }

        return value;
    }
}
