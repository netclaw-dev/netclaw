// -----------------------------------------------------------------------
// <copyright file="TeamsSdkActivityTranslator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Apps.Schema.Entities;
using Netclaw.Actors.Channels;
using Netclaw.Channels.Teams;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Keeps Microsoft SDK activity objects at the HTTP hosting edge. No Teams
/// SDK type is allowed to cross into the channel contracts or actor boundary.
/// </summary>
internal sealed class TeamsSdkActivityTranslator(TeamsChannelOptions options, TimeProvider timeProvider)
{
    private const int MaxAttachmentContentTypeLength = 255;
    private const int MaxAttachmentNameLength = 255;
    private const int MaxAttachmentContentUrlLength = 2_048;
    internal const string PreservedReplyToActivityIdProperty = "netclaw.teams.replyToActivityId";

    public TeamsTranslationResult Translate(TeamsActivity activity, string? authenticatedTenantId)
    {
        ArgumentNullException.ThrowIfNull(activity);

        return activity switch
        {
            MessageUpdateActivity update => TranslateMutation(update, authenticatedTenantId, TeamsIngressActivityKind.MessageUpdate),
            MessageDeleteActivity delete => TranslateMutation(delete, authenticatedTenantId, TeamsIngressActivityKind.MessageDelete),
            ConversationUpdateActivity => TeamsTranslationResult.Ignored(
                TeamsIngressActivityKind.ConversationUpdate,
                "conversation_update_recording_not_implemented"),
            InvokeActivity action => TranslateApprovalAction(action, authenticatedTenantId),
            MessageActivity message => TranslateMessage(message, authenticatedTenantId),
            _ => TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, TeamsIngressActivityKind.Unknown, "unsupported_activity_type")
        };
    }

    /// <summary>
    /// Creates bounded diagnostic facts for rejected attachments. This exists
    /// only to capture tenant-safe structural samples for compatibility work.
    /// It never returns SDK payload values or platform IDs.
    /// </summary>
    internal TeamsRejectedAttachmentDiagnostic? DescribeRejectedAttachment(
        TeamsActivity activity,
        string? authenticatedTenantId,
        TeamsTranslationResult result)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(result);

        if (result.Disposition == TeamsTranslationDisposition.Accepted
            || activity is not MessageActivity message)
            return null;

        var attachments = message.Attachments;
        if (attachments is null || attachments.Count == 0)
            return null;

        var scope = GetScope(message);
        var teamId = message.ChannelData?.Team?.Id;
        var channelId = message.ChannelData?.Channel?.Id;
        var mentions = TranslateMentions(message.Entities);
        var rootActivityValid = scope == TeamsConversationScope.Channel
            && TeamsTenantEvidenceMappings.TryGetCanonicalChannelRootActivityId(message.Conversation?.Id, out _);

        var diagnostics = ImmutableArray.CreateBuilder<TeamsRejectedAttachmentDiagnosticEntry>(attachments.Count);
        for (var index = 0; index < attachments.Count; index++)
        {
            var attachment = attachments[index];
            if (attachment is null)
            {
                diagnostics.Add(TeamsRejectedAttachmentDiagnosticEntry.Malformed(index));
                continue;
            }

            var evidence = CreateAttachmentEvidence(attachment, message.ChannelData is not null);
            var classifier = TeamsTenantEvidenceMappings.ClassifyAttachment(evidence);
            var resolvedKind = TryGetSupportedAttachmentKind(
                evidence.ContentType,
                evidence.HasContentUrl,
                scope ?? TeamsConversationScope.Personal,
                classifier,
                out var kind)
                ? kind.ToString()
                : classifier.Classification == TeamsAttachmentClassification.InlineTextRendering
                    ? "None"
                    : TeamsInboundAttachmentKind.Unknown.ToString();
            diagnostics.Add(new TeamsRejectedAttachmentDiagnosticEntry(
                index,
                DescribeContentType(evidence.ContentType),
                evidence.ContentKind.ToString(),
                evidence.HasContentUrl,
                evidence.HasName,
                evidence.HasThumbnailUrl,
                classifier.Classification.ToString(),
                resolvedKind));
        }

        return new TeamsRejectedAttachmentDiagnostic(
            Scope: DescribeScope(scope),
            TenantMatch: !string.IsNullOrWhiteSpace(authenticatedTenantId)
                         && string.Equals(authenticatedTenantId, options.TenantId, StringComparison.Ordinal),
            TeamMatch: !string.IsNullOrWhiteSpace(teamId)
                       && options.AllowedTeamIds.Contains(teamId, StringComparer.Ordinal),
            ChannelMatch: !string.IsNullOrWhiteSpace(channelId)
                          && options.AllowedChannelIds.Contains(channelId, StringComparer.Ordinal),
            SenderMatch: options.AllowedUserIds.Length == 0
                         || (message.From is { } sender
                             && options.AllowedUserIds.Contains(GetCanonicalSenderId(sender), StringComparer.Ordinal)),
            Mentioned: mentions.Any(mention => IsQualifiedBotMention(mention, message.Recipient?.Id, options.BotId)),
            RootActivityValid: rootActivityValid,
            AudienceValid: HasSingleTeamAudienceOverride(teamId, channelId),
            PolicyReason: result.ReasonCode,
            AttachmentCount: attachments.Count,
            Attachments: diagnostics.ToImmutable(),
            MentionCount: mentions.Length,
            ReplyToIdExists: !string.IsNullOrWhiteSpace(GetReplyToActivityId(message)));
    }

    private TeamsTranslationResult TranslateApprovalAction(
        InvokeActivity activity,
        string? authenticatedTenantId)
    {
        if (!TryValidateCommon(activity, authenticatedTenantId, TeamsIngressActivityKind.AdaptiveCardAction, out var scope, out var failure))
            return failure!;
        var invokeAction = activity.Value?.Deserialize<AdaptiveCardActionValue>()?.Action;
        if (invokeAction is null || !string.Equals(invokeAction.Type, "Action.Execute", StringComparison.Ordinal))
        {
            return TeamsTranslationResult.Rejected(
                TeamsTranslationDisposition.RejectedMalformed,
                TeamsIngressActivityKind.AdaptiveCardAction,
                "unsupported_card_action_type");
        }

        var data = invokeAction.Data;
        if (!TryGetOpaqueValue(data, "correlation", TeamsApprovalAction.MaxCorrelationLength, out var correlation)
            || !TryGetOpaqueValue(data, "nonce", TeamsApprovalAction.MaxNonceLength, out var nonce)
            || !TryGetOpaqueValue(data, "action", TeamsApprovalAction.MaxActionLength, out var action)
            || !TeamsApprovalAction.IsSupportedAction(action)
            || !TeamsApprovalAction.IsBoundedOpaqueValue(correlation, TeamsApprovalAction.MaxCorrelationLength)
            || !TeamsApprovalAction.IsBoundedOpaqueValue(nonce, TeamsApprovalAction.MaxNonceLength))
        {
            return TeamsTranslationResult.Rejected(
                TeamsTranslationDisposition.RejectedMalformed,
                TeamsIngressActivityKind.AdaptiveCardAction,
                "invalid_approval_action_data");
        }

        string? rootActivityId = null;
        if (scope == TeamsConversationScope.Channel
            && !TeamsTenantEvidenceMappings.TryGetCanonicalChannelRootActivityId(activity.Conversation!.Id, out rootActivityId))
        {
            return TeamsTranslationResult.Rejected(
                TeamsTranslationDisposition.RejectedMalformed,
                TeamsIngressActivityKind.AdaptiveCardAction,
                "invalid_channel_root_identity");
        }

        var serviceUrl = activity.ServiceUrl;
        var replyToActivityId = GetReplyToActivityId(activity);
        if (string.IsNullOrWhiteSpace(replyToActivityId)
            || !TeamsSessionIdentifierCodec.IsValidActivityIdentifier(replyToActivityId)
            || serviceUrl is null
            || !TeamsOutboundDestination.IsValidServiceUrl(serviceUrl.ToString()))
        {
            return TeamsTranslationResult.Rejected(
                TeamsTranslationDisposition.RejectedMalformed,
                TeamsIngressActivityKind.AdaptiveCardAction,
                "invalid_approval_action_context");
        }

        var sender = activity.From!;
        var suppliedOperatorDisplayName = sender.Name;
        var operatorDisplayName = TeamsApprovalAction.NormalizeOperatorDisplayName(suppliedOperatorDisplayName);
        if (IsRawIdentifierDisplayName(
                suppliedOperatorDisplayName,
                sender.Id,
                sender.AadObjectId,
                authenticatedTenantId,
                activity.Conversation!.Id,
                activity.Id,
                rootActivityId,
                activity.ChannelData?.Team?.Id,
                activity.ChannelData?.Channel?.Id,
                replyToActivityId,
                correlation,
                nonce))
        {
            operatorDisplayName = null;
        }

        return TeamsTranslationResult.Accepted(new TeamsApprovalAction(
            CreateTrust(activity, authenticatedTenantId!, scope),
            correlation,
            nonce,
            action,
            rootActivityId,
            activity.ChannelData?.Team?.Id,
            activity.ChannelData?.Channel?.Id,
            replyToActivityId,
            serviceUrl.ToString(),
            operatorDisplayName));
    }

    private TeamsTranslationResult TranslateMessage(MessageActivity activity, string? authenticatedTenantId)
    {
        if (string.IsNullOrWhiteSpace(authenticatedTenantId))
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedPendingTenantEvidence, TeamsIngressActivityKind.Message, "missing_authenticated_tenant_id");

        if (string.IsNullOrWhiteSpace(options.TenantId))
        {
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedPendingTenantEvidence, TeamsIngressActivityKind.Message, "missing_configured_tenant_id");
        }

        if (!string.Equals(authenticatedTenantId, options.TenantId, StringComparison.Ordinal))
        {
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedPendingTenantEvidence, TeamsIngressActivityKind.Message, "configured_tenant_mismatch");
        }

        if (activity.Conversation is null || string.IsNullOrWhiteSpace(activity.Conversation.Id))
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, TeamsIngressActivityKind.Message, "missing_conversation_id");

        if (string.IsNullOrWhiteSpace(activity.Id))
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, TeamsIngressActivityKind.Message, "missing_activity_id");

        if (string.IsNullOrWhiteSpace(activity.From?.Id))
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, TeamsIngressActivityKind.Message, "missing_sender_id");

        if (!string.IsNullOrWhiteSpace(activity.Conversation.TenantId)
            && !string.Equals(activity.Conversation.TenantId, authenticatedTenantId, StringComparison.Ordinal))
        {
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, TeamsIngressActivityKind.Message, "tenant_mismatch");
        }

        var scope = activity.Conversation.ConversationType switch
        {
            var type when type == ConversationType.Personal => TeamsConversationScope.Personal,
            var type when type == ConversationType.GroupChat => TeamsConversationScope.GroupChat,
            var type when type == ConversationType.Channel => TeamsConversationScope.Channel,
            _ => (TeamsConversationScope?)null
        };

        if (scope is null)
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedUnsupportedScope, TeamsIngressActivityKind.Message, "unsupported_conversation_scope");

        string? rootActivityId = null;
        if (scope == TeamsConversationScope.Channel
            && !TeamsTenantEvidenceMappings.TryGetCanonicalChannelRootActivityId(activity.Conversation.Id, out rootActivityId))
        {
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, TeamsIngressActivityKind.Message, "invalid_channel_root_identity");
        }

        var attachmentFailure = TranslateInboundAttachments(
            activity.Attachments,
            activity.ChannelData is not null,
            scope.Value,
            out var inboundAttachments);
        if (attachmentFailure is not null)
            return attachmentFailure;

        if (string.IsNullOrWhiteSpace(activity.Text) && inboundAttachments.IsEmpty)
        {
            return TeamsTranslationResult.Rejected(
                TeamsTranslationDisposition.RejectedMalformed,
                TeamsIngressActivityKind.Message,
                "missing_message_text");
        }

        var trust = CreateTrust(activity, authenticatedTenantId, scope.Value);
        var mentions = TranslateMentions(activity.Entities);
        var messageText = activity.Text ?? string.Empty;
        var text = scope is TeamsConversationScope.Channel or TeamsConversationScope.GroupChat
            ? TeamsTenantEvidenceMappings.RemoveQualifiedBotMentions(
                messageText,
                mentions.Select(mention => new TeamsMentionEvidence(mention.Type, mention.MentionedId, mention.Text)),
                activity.Recipient?.Id ?? string.Empty,
                options.BotId ?? string.Empty)
            : messageText;
        var mentioned = scope is TeamsConversationScope.Channel or TeamsConversationScope.GroupChat
            && mentions.Any(mention => IsQualifiedBotMention(mention, activity.Recipient?.Id, options.BotId));

        return TeamsTranslationResult.Accepted(new TeamsInboundActivity(
            trust,
            text,
            new TeamsReplyMetadata(GetReplyToActivityId(activity), rootActivityId, activity.ServiceUrl?.ToString()),
            mentioned,
            attachments: inboundAttachments,
            kind: TeamsIngressActivityKind.Message,
            teamId: activity.ChannelData?.Team?.Id,
            channelId: activity.ChannelData?.Channel?.Id,
            mentions: mentions),
            inboundAttachments.Length > 0
                ? "teams_attachment_received"
                : activity.Attachments is { Count: > 0 }
                ? "teams_text_rendering_wrapper_ignored"
                : "plain_text_accepted");
    }

    private TeamsTranslationResult TranslateMutation(
        TeamsActivity activity,
        string? authenticatedTenantId,
        TeamsIngressActivityKind kind)
    {
        if (!TryValidateCommon(activity, authenticatedTenantId, kind, out var scope, out var failure))
            return failure!;
        if (scope != TeamsConversationScope.Channel)
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedUnsupportedScope, kind, "unsupported_conversation_scope");
        if (!TeamsTenantEvidenceMappings.TryGetCanonicalChannelRootActivityId(activity.Conversation!.Id, out var rootActivityId))
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, kind, "invalid_channel_root_identity");

        return TeamsTranslationResult.Accepted(new TeamsInboundActivity(
            CreateTrust(activity, authenticatedTenantId!, scope),
            string.Empty,
            new TeamsReplyMetadata(GetReplyToActivityId(activity), rootActivityId, activity.ServiceUrl?.ToString()),
            kind: kind,
            teamId: activity.ChannelData?.Team?.Id,
            channelId: activity.ChannelData?.Channel?.Id));
    }

    private TeamsIngressTrustContext CreateTrust(TeamsActivity activity, string authenticatedTenantId, TeamsConversationScope scope) => new(
            TrustAudience.Public,
            PrincipalClassification.UntrustedExternal,
            TrustBoundary.Public,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            GetCanonicalSenderId(activity.From!),
            authenticatedTenantId,
            activity.Conversation!.Id!,
            scope,
            activity.Id!,
            timeProvider.GetUtcNow(),
            ParseActivityTimestamp(activity.Timestamp));

    private static string GetCanonicalSenderId(TeamsChannelAccount sender) =>
        string.IsNullOrWhiteSpace(sender.AadObjectId) ? sender.Id! : sender.AadObjectId;

    private static bool IsRawIdentifierDisplayName(string? displayName, params string?[] identifiers) =>
        !string.IsNullOrWhiteSpace(displayName)
        && identifiers.Any(identifier => !string.IsNullOrWhiteSpace(identifier)
                                        && string.Equals(displayName.Trim(), identifier.Trim(), StringComparison.Ordinal));

    private bool TryValidateCommon(
        TeamsActivity activity,
        string? authenticatedTenantId,
        TeamsIngressActivityKind kind,
        out TeamsConversationScope scope,
        out TeamsTranslationResult? failure)
    {
        scope = default;
        failure = null;
        if (string.IsNullOrWhiteSpace(authenticatedTenantId) || string.IsNullOrWhiteSpace(options.TenantId)
            || !string.Equals(authenticatedTenantId, options.TenantId, StringComparison.Ordinal))
        {
            failure = TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedPendingTenantEvidence, kind, "configured_tenant_mismatch");
            return false;
        }
        if (activity.Conversation is null || string.IsNullOrWhiteSpace(activity.Conversation.Id))
        {
            failure = TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, kind, "missing_conversation_id");
            return false;
        }
        if (string.IsNullOrWhiteSpace(activity.Id))
        {
            failure = TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, kind, "missing_activity_id");
            return false;
        }
        if (string.IsNullOrWhiteSpace(activity.From?.Id))
        {
            failure = TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, kind, "missing_sender_id");
            return false;
        }
        if (!string.IsNullOrWhiteSpace(activity.Conversation.TenantId)
            && !string.Equals(activity.Conversation.TenantId, authenticatedTenantId, StringComparison.Ordinal))
        {
            failure = TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, kind, "tenant_mismatch");
            return false;
        }
        if (activity.Conversation.ConversationType == ConversationType.Channel)
        {
            scope = TeamsConversationScope.Channel;
            return true;
        }
        if (activity.Conversation.ConversationType == ConversationType.GroupChat)
        {
            scope = TeamsConversationScope.GroupChat;
            return true;
        }
        if (activity.Conversation.ConversationType == ConversationType.Personal)
        {
            scope = TeamsConversationScope.Personal;
            return true;
        }

        failure = TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedUnsupportedScope, kind, "unsupported_conversation_scope");
        return false;
    }

    private static TeamsTranslationResult? TranslateInboundAttachments(
        IList<TeamsAttachment>? attachments,
        bool hasChannelData,
        TeamsConversationScope scope,
        out ImmutableArray<TeamsAttachmentMetadata> inboundAttachments)
    {
        inboundAttachments = [];
        if (attachments is null || attachments.Count == 0)
            return null;

        var builder = ImmutableArray.CreateBuilder<TeamsAttachmentMetadata>();
        for (var index = 0; index < attachments.Count; index++)
        {
            var attachment = attachments[index];
            if (attachment is null)
                return RejectMalformedAttachment();

            if (!HasBoundedAttachmentMetadata(attachment.ContentType?.ToString(), MaxAttachmentContentTypeLength)
                || !HasBoundedAttachmentMetadata(attachment.Name, MaxAttachmentNameLength)
                || !HasBoundedContentUrl(attachment.ContentUrl))
            {
                return RejectMalformedAttachment();
            }

            var evidence = CreateAttachmentEvidence(attachment, hasChannelData);
            var classification = TeamsTenantEvidenceMappings.ClassifyAttachment(evidence);
            if (classification.Classification == TeamsAttachmentClassification.InlineTextRendering)
                continue;

            if (TryGetSupportedAttachmentKind(evidence.ContentType, evidence.HasContentUrl, scope, classification, out var kind))
            {
                builder.Add(CreateInboundAttachmentMetadata(attachment, evidence, kind, index));
                continue;
            }

            if (classification.Classification == TeamsAttachmentClassification.GraphBackedUnsupported
                && IsTeamsFileDownloadInfo(evidence.ContentType))
            {
                builder.Add(CreateInboundAttachmentMetadata(
                    attachment,
                    evidence,
                    TeamsInboundAttachmentKind.Unknown,
                    index));
                continue;
            }

            if (classification.Classification == TeamsAttachmentClassification.GraphBackedUnsupported)
            {
                return TeamsTranslationResult.Rejected(
                    TeamsTranslationDisposition.RejectedMalformed,
                    TeamsIngressActivityKind.Message,
                    classification.ReasonCode!);
            }

            if (IsHostileUnknownAttachment(evidence))
                return RejectMalformedAttachment();

            // This attachment is bounded but not a recognized executable
            // download shape. Preserve the containing message, but ensure the
            // actor can only reject it; it cannot download or expose its data.
            builder.Add(CreateInboundAttachmentMetadata(
                attachment,
                evidence,
                TeamsInboundAttachmentKind.Unknown,
                index));
        }

        inboundAttachments = builder.ToImmutable();
        return null;
    }

    private static TeamsTranslationResult RejectMalformedAttachment() =>
        TeamsTranslationResult.Rejected(
            TeamsTranslationDisposition.RejectedMalformed,
            TeamsIngressActivityKind.Message,
            "attachment_malformed_rejected");

    private static TeamsAttachmentMetadata CreateInboundAttachmentMetadata(
        TeamsAttachment attachment,
        TeamsAttachmentEvidence evidence,
        TeamsInboundAttachmentKind kind,
        int index) => new(
            GetBoundedAttachmentMetadata(attachment.Name, MaxAttachmentNameLength) ?? $"attachment-{index + 1}",
            evidence.ContentType,
            declaredSizeBytes: null)
        {
            Kind = kind,
            SourceIndex = index
        };

    private static bool TryGetSupportedAttachmentKind(
        string? contentType,
        bool hasContentUrl,
        TeamsConversationScope scope,
        TeamsAttachmentClassificationResult classification,
        out TeamsInboundAttachmentKind kind)
    {
        kind = TeamsInboundAttachmentKind.Unknown;
        if (!hasContentUrl)
            return false;

        if (classification.Classification != TeamsAttachmentClassification.GraphBackedUnsupported
            && IsImageTransportContentType(contentType))
        {
            kind = TeamsInboundAttachmentKind.InlineImage;
            return true;
        }

        if (scope == TeamsConversationScope.Personal && IsTeamsFileDownloadInfo(contentType))
        {
            kind = TeamsInboundAttachmentKind.PersonalFile;
            return true;
        }

        return false;
    }

    private static bool IsImageTransportContentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var mediaType = value.Split(';', 2)[0].Trim();
        return mediaType.Length > "image/".Length
               && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
               && mediaType["image/".Length..].All(IsMimeTokenCharacter);
    }

    private static bool IsMimeTokenCharacter(char character)
        => char.IsAsciiLetterOrDigit(character)
           || character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    private static bool IsTeamsFileDownloadInfo(string? value)
        => string.Equals(value, "application/vnd.microsoft.teams.file.download.info", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "application/vnd.microsoft.teams.file.download.info+json", StringComparison.OrdinalIgnoreCase);

    private static string? GetBoundedAttachmentMetadata(string? value, int maximumLength)
        => value is { Length: > 0 } && value.Length <= maximumLength ? value : null;

    private static bool HasBoundedAttachmentMetadata(string? value, int maximumLength)
        => value is null || (value.Length > 0 && value.Length <= maximumLength);

    private static bool HasBoundedContentUrl(Uri? value)
        => value is null || (value.IsAbsoluteUri && value.ToString().Length is > 0 and <= MaxAttachmentContentUrlLength);

    private static bool IsHostileUnknownAttachment(TeamsAttachmentEvidence evidence)
        => evidence.ContentKind == TeamsAttachmentContentKind.Structured
           || evidence.HasThumbnailUrl;

    private static TeamsAttachmentEvidence CreateAttachmentEvidence(TeamsAttachment attachment, bool hasChannelData)
    {
        var (contentKind, hasEmbeddedContentReference, hasEmbeddedGraphBackedContentReference, hasHtmlRenderingMarkup, hasParagraphRenderingMarkup, hasImageRenderingMarkup) = GetContentFacts(attachment.Content);
        return new TeamsAttachmentEvidence(
            GetBoundedAttachmentMetadata(attachment.ContentType?.ToString(), MaxAttachmentContentTypeLength),
            attachment.Name is not null,
            GetBoundedAttachmentMetadata(attachment.ContentUrl?.ToString(), MaxAttachmentContentUrlLength),
            attachment.ContentUrl is not null,
            hasEmbeddedContentReference,
            hasEmbeddedGraphBackedContentReference,
            contentKind,
            attachment.ThumbnailUrl is not null,
            hasChannelData,
            hasHtmlRenderingMarkup,
            hasParagraphRenderingMarkup,
            hasImageRenderingMarkup);
    }

    private TeamsConversationScope? GetScope(TeamsActivity activity) => activity.Conversation?.ConversationType switch
    {
        var type when type == ConversationType.Channel => TeamsConversationScope.Channel,
        var type when type == ConversationType.GroupChat => TeamsConversationScope.GroupChat,
        var type when type == ConversationType.Personal => TeamsConversationScope.Personal,
        _ => null
    };

    private bool HasSingleTeamAudienceOverride(string? teamId, string? channelId)
    {
        if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(channelId))
            return false;

        var matches = options.ChannelAudienceOverrides
            .Where(audienceOverride => string.Equals(audienceOverride.TeamId, teamId, StringComparison.Ordinal)
                                       && string.Equals(audienceOverride.ChannelId, channelId, StringComparison.Ordinal))
            .ToArray();
        return matches is [{ Audience: "Team" }];
    }

    private static string DescribeScope(TeamsConversationScope? scope) => scope switch
    {
        TeamsConversationScope.Channel => "channel",
        TeamsConversationScope.GroupChat => "groupchat",
        TeamsConversationScope.Personal => "personal",
        _ => "unsupported"
    };

    private static string DescribeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return "missing";
        if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            return "text_html";
        if (contentType.StartsWith("application/vnd.microsoft.teams.file.download.info", StringComparison.OrdinalIgnoreCase))
            return "teams_file_download_info";
        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return "image";
        return "other";
    }

    private static (TeamsAttachmentContentKind ContentKind, bool HasReference, bool HasGraphBackedReference, bool HasHtmlRenderingMarkup, bool HasParagraphRenderingMarkup, bool HasImageRenderingMarkup) GetContentFacts(object? content)
    {
        if (content is null)
            return (TeamsAttachmentContentKind.Missing, false, false, false, false, false);

        // The Teams SDK declares attachment Content as object. System.Text.Json
        // therefore materializes the normal text/html rendering wrapper as a
        // JSON string element on live inbound activities, not a CLR string.
        // Only that scalar representation is equivalent to text; objects and
        // arrays remain structured attachment evidence and fail closed.
        var text = GetScalarAttachmentText(content);
        if (text is null)
            return (TeamsAttachmentContentKind.Structured, false, false, false, false, false);
        if (string.IsNullOrWhiteSpace(text))
            return (TeamsAttachmentContentKind.EmptyText, false, false, false, false, false);

        var hasReference = text.Contains("http://", StringComparison.OrdinalIgnoreCase)
                           || text.Contains("https://", StringComparison.OrdinalIgnoreCase);
        var hasHtmlRenderingMarkup = HasHtmlRenderingMarkup(text);
        var hasParagraphRenderingMarkup = HasParagraphRenderingMarkup(text);
        var hasImageRenderingMarkup = HasImageRenderingMarkup(text);
        if (!hasReference)
            return (TeamsAttachmentContentKind.NonEmptyText, false, false, hasHtmlRenderingMarkup, hasParagraphRenderingMarkup, hasImageRenderingMarkup);

        var hasGraphBackedReference = text.Contains("graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
                                      || text.Contains(".sharepoint.com", StringComparison.OrdinalIgnoreCase)
                                      || text.Contains(".sharepoint.us", StringComparison.OrdinalIgnoreCase)
                                      || text.Contains(".onedrive.com", StringComparison.OrdinalIgnoreCase)
                                      || text.Contains("onedrive.live.com", StringComparison.OrdinalIgnoreCase);
        return (TeamsAttachmentContentKind.NonEmptyText, true, hasGraphBackedReference, hasHtmlRenderingMarkup, hasParagraphRenderingMarkup, hasImageRenderingMarkup);
    }

    private static string? GetScalarAttachmentText(object? content) => content switch
    {
        string value => value,
        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
        _ => null
    };

    private static bool HasHtmlRenderingMarkup(string text)
    {
        var trimmed = text.AsSpan().Trim();
        return trimmed.StartsWith("<div", StringComparison.OrdinalIgnoreCase)
               && trimmed.EndsWith("</div>", StringComparison.OrdinalIgnoreCase)
               && trimmed.Contains("<a ", StringComparison.OrdinalIgnoreCase)
               && trimmed.Contains("href=", StringComparison.OrdinalIgnoreCase)
               && trimmed.Contains("</a>", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasParagraphRenderingMarkup(string text)
    {
        var trimmed = text.AsSpan().Trim();
        return trimmed.StartsWith("<p", StringComparison.OrdinalIgnoreCase)
               && trimmed.EndsWith("</p>", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasImageRenderingMarkup(string text)
    {
        var trimmed = text.AsSpan().Trim();
        return trimmed.StartsWith("<div", StringComparison.OrdinalIgnoreCase)
               && trimmed.EndsWith("</div>", StringComparison.OrdinalIgnoreCase)
               && trimmed.Contains("<img ", StringComparison.OrdinalIgnoreCase)
               && trimmed.Contains("src=", StringComparison.OrdinalIgnoreCase)
               && CountCharacters(trimmed, '<') == 3;
    }

    private static int CountCharacters(ReadOnlySpan<char> value, char character)
    {
        var count = 0;
        foreach (var current in value)
        {
            if (current == character)
                count++;
        }

        return count;
    }

    private static ImmutableArray<TeamsMention> TranslateMentions(IList<Entity>? entities)
        => entities?.OfType<MentionEntity>()
            .Where(entity => entity.Mentioned is not null && !string.IsNullOrWhiteSpace(entity.Text))
            .Select(entity => new TeamsMention(entity.Type, entity.Mentioned!.Id ?? string.Empty, entity.Text!))
            .ToImmutableArray() ?? [];

    private static bool IsQualifiedBotMention(TeamsMention mention, string? recipientId, string? configuredBotId)
        => string.Equals(mention.Type, "mention", StringComparison.Ordinal)
           && mention.Text.StartsWith("<at>", StringComparison.Ordinal)
           && mention.Text.EndsWith("</at>", StringComparison.Ordinal)
           && mention.Text.Length > "<at></at>".Length
           && !string.IsNullOrWhiteSpace(configuredBotId)
           && !string.IsNullOrWhiteSpace(recipientId)
           && string.Equals(mention.MentionedId, recipientId, StringComparison.Ordinal)
           && string.Equals(mention.MentionedId, $"28:{configuredBotId}", StringComparison.Ordinal);

    private static DateTimeOffset? ParseActivityTimestamp(string? value)
        => DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : null;

    private static string? GetReplyToActivityId(TeamsActivity activity)
    {
        if (!string.IsNullOrWhiteSpace(activity.ReplyToId))
            return activity.ReplyToId;

        return ((Microsoft.Teams.Core.Schema.CoreActivity)activity).Properties
            .Get<string>(PreservedReplyToActivityIdProperty);
    }

    private static bool TryGetOpaqueValue(
        IDictionary<string, object>? data,
        string key,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (data is null || !data.TryGetValue(key, out var candidate))
            return false;

        value = candidate switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? string.Empty,
            _ => string.Empty
        };
        return value.Length <= maximumLength;
    }
}

/// <summary>
/// Contains finite attachment facts for a temporary tenant-safe diagnostic.
/// It cannot carry message content, a URL, a token, or a platform identifier.
/// </summary>
internal sealed record TeamsRejectedAttachmentDiagnostic(
    string Scope,
    bool TenantMatch,
    bool TeamMatch,
    bool ChannelMatch,
    bool SenderMatch,
    bool Mentioned,
    bool RootActivityValid,
    bool AudienceValid,
    string PolicyReason,
    int AttachmentCount,
    ImmutableArray<TeamsRejectedAttachmentDiagnosticEntry> Attachments,
    int MentionCount,
    bool ReplyToIdExists);

/// <summary>
/// Contains one finite, non-content diagnostic summary for one attachment.
/// </summary>
internal sealed record TeamsRejectedAttachmentDiagnosticEntry(
    int Index,
    string ContentType,
    string ContentKind,
    bool HasContentUrl,
    bool HasName,
    bool HasThumbnail,
    string ClassifierDisposition,
    string ResolvedInboundAttachmentKind)
{
    public static TeamsRejectedAttachmentDiagnosticEntry Malformed(int index) => new(
        index,
        "missing",
        "Missing",
        false,
        false,
        false,
        "Malformed",
        "None");
}
