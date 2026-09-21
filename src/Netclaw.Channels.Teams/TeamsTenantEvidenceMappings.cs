// -----------------------------------------------------------------------
// <copyright file="TeamsTenantEvidenceMappings.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels.Teams;

/// <summary>
/// Pure mappings recorded by the opt-in tenant transport spike. The daemon
/// uses the attachment classifier before it routes an activity to an actor.
/// </summary>
public static class TeamsTenantEvidenceMappings
{
    private const string ThreadMessageIdSeparator = ";messageid=";

    public static bool TryGetCanonicalChannelRootActivityId(string? conversationId, out string rootActivityId)
    {
        rootActivityId = string.Empty;
        if (string.IsNullOrWhiteSpace(conversationId))
            return false;

        var separatorIndex = conversationId.LastIndexOf(ThreadMessageIdSeparator, StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex != conversationId.IndexOf(ThreadMessageIdSeparator, StringComparison.Ordinal))
            return false;

        var candidate = conversationId[(separatorIndex + ThreadMessageIdSeparator.Length)..];
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Contains(';', StringComparison.Ordinal)
            || candidate.Contains("messageid=", StringComparison.Ordinal)
            || !TeamsSessionIdentifierCodec.IsValidActivityIdentifier(candidate))
            return false;

        rootActivityId = candidate;
        return true;
    }

    /// <summary>
    /// Removes only mention spans whose qualified identity matches both the
    /// activity recipient and the configured bot. Display names are not used.
    /// </summary>
    public static string RemoveQualifiedBotMentions(
        string text,
        IEnumerable<TeamsMentionEvidence> entities,
        string recipientId,
        string configuredBotId)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(entities);

        var qualifiedBotId = $"28:{configuredBotId}";
        var result = text;
        foreach (var entity in entities)
        {
            if (!string.Equals(entity.Type, "mention", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(entity.Text)
                || !IsWellFormedMentionSpan(entity.Text)
                || !string.Equals(entity.MentionedId, recipientId, StringComparison.Ordinal)
                || !string.Equals(entity.MentionedId, qualifiedBotId, StringComparison.Ordinal))
            {
                continue;
            }

            var spanIndex = result.IndexOf(entity.Text, StringComparison.Ordinal);
            if (spanIndex >= 0)
                result = result.Remove(spanIndex, entity.Text.Length);
        }

        return result;
    }

    private static bool IsWellFormedMentionSpan(string text) =>
        text.StartsWith("<at>", StringComparison.Ordinal)
        && text.EndsWith("</at>", StringComparison.Ordinal)
        && text.Length > "<at></at>".Length;

    /// <summary>
    /// Teams can include a non-empty HTML rendering of ordinary formatted text
    /// beside the canonical activity text. The tenant upload fixture has an
    /// empty HTML payload, so only this distinct bounded SDK shape is metadata.
    /// The daemon never exposes the rendering markup to the model.
    /// </summary>
    public static bool IsInlineTextRendering(TeamsAttachmentEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        return IsTextHtmlRenderingContentType(evidence.ContentType)
               && !evidence.HasName
               && !evidence.HasContentUrl
               && !evidence.HasThumbnailUrl
               && string.IsNullOrWhiteSpace(evidence.ContentUrl)
               // Teams can place an ordinary navigation reference inside its
               // scalar HTML rendering wrapper. It is not an attachment URL:
               // the wrapper remains safe only when there is no attachment
               // metadata and no known Graph-backed provider reference. The
               // A channel-data object describes the conversation. It does
               // not describe an attachment. A scalar HTML wrapper remains
               // rendering metadata when it contains an HTML envelope.
               // A bare reference remains an unsupported upload shape.
               && !evidence.HasEmbeddedGraphBackedContentReference
               && (!evidence.HasEmbeddedContentReference
                   || evidence.HasHtmlRenderingMarkup
                   || evidence.HasParagraphRenderingMarkup
                   || evidence.HasImageRenderingMarkup)
               && evidence.ContentKind == TeamsAttachmentContentKind.NonEmptyText;
    }

    /// <summary>
    /// Classifies only bounded, transport-neutral attachment facts. The daemon
    /// resolves downloadable attachment kinds at its SDK boundary. Every other
    /// attachment remains non-executable until that boundary resolves it.
    /// </summary>
    public static TeamsAttachmentClassificationResult ClassifyAttachment(TeamsAttachmentEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (evidence.HasEmbeddedGraphBackedContentReference
            || IsGraphBackedContentReference(evidence.ContentUrl)
            || IsKnownGraphBackedContentType(evidence.ContentType))
        {
            return TeamsAttachmentClassificationResult.GraphBackedUnsupported();
        }

        if (IsInlineTextRendering(evidence))
            return TeamsAttachmentClassificationResult.InlineTextRendering();

        if (IsKnownGraphBackedUploadShell(evidence))
            return TeamsAttachmentClassificationResult.GraphBackedUnsupported();

        return TeamsAttachmentClassificationResult.UnsupportedAttachmentShape();
    }

    private static bool IsGraphBackedContentReference(string? contentUrl)
    {
        if (string.IsNullOrWhiteSpace(contentUrl))
            return false;

        if (!Uri.TryCreate(contentUrl, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".sharepoint.us", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".onedrive.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("onedrive.live.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownGraphBackedContentType(string? contentType)
        => string.Equals(contentType, "application/vnd.microsoft.teams.file.download.info", StringComparison.OrdinalIgnoreCase)
           || string.Equals(contentType, "application/vnd.microsoft.teams.file.download.info+json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Teams can add the standard UTF-8 charset parameter to its ordinary HTML
    /// rendering metadata. Only that parameter is accepted; the caller still
    /// requires scalar nonempty content with no name, URL, or content reference.
    /// </summary>
    private static bool IsTextHtmlRenderingContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        var parts = contentType.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts is [var mediaType, .. var parameters]
               && string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase)
               && parameters.All(IsUtf8CharsetParameter);
    }

    private static bool IsUtf8CharsetParameter(string parameter)
    {
        var separatorIndex = parameter.IndexOf('=', StringComparison.Ordinal);
        return separatorIndex > 0
               && string.Equals(parameter[..separatorIndex].Trim(), "charset", StringComparison.OrdinalIgnoreCase)
               && string.Equals(parameter[(separatorIndex + 1)..].Trim().Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownGraphBackedUploadShell(TeamsAttachmentEvidence evidence)
        => IsTextHtmlRenderingContentType(evidence.ContentType)
           && !evidence.HasName
           && !evidence.HasContentUrl
           && !evidence.HasEmbeddedContentReference
           && evidence.ContentKind == TeamsAttachmentContentKind.EmptyText;
}

public enum TeamsAttachmentClassification
{
    InlineTextRendering,
    GraphBackedUnsupported,
    UnsupportedAttachmentShape
}

/// <summary>
/// Bounded content facts copied from the SDK attachment. The descriptor has no
/// markup, file bytes, URI, or provider metadata.
/// </summary>
public enum TeamsAttachmentContentKind
{
    Missing,
    EmptyText,
    NonEmptyText,
    Structured
}

public sealed record TeamsAttachmentClassificationResult(TeamsAttachmentClassification Classification, string? ReasonCode = null)
{
    public static TeamsAttachmentClassificationResult InlineTextRendering()
        => new(TeamsAttachmentClassification.InlineTextRendering);

    public static TeamsAttachmentClassificationResult GraphBackedUnsupported()
        => new(TeamsAttachmentClassification.GraphBackedUnsupported, "graph_backed_attachment_unsupported");

    public static TeamsAttachmentClassificationResult UnsupportedAttachmentShape()
        => new(TeamsAttachmentClassification.UnsupportedAttachmentShape, "unsupported_attachment_shape");
}

/// <summary>
/// Attachment facts copied by the daemon from the SDK object. This descriptor
/// never enters actor state, telemetry, persistence, or a model request. The
/// channel-data and HTML-envelope flags are structural facts. They contain no
/// identity, markup, URL, or file data.
/// </summary>
public sealed record TeamsAttachmentEvidence(
    string? ContentType,
    bool HasName,
    string? ContentUrl,
    bool HasContentUrl,
    bool HasEmbeddedContentReference = false,
    bool HasEmbeddedGraphBackedContentReference = false,
    TeamsAttachmentContentKind ContentKind = TeamsAttachmentContentKind.Missing,
    bool HasThumbnailUrl = false,
    bool HasChannelData = false,
    bool HasHtmlRenderingMarkup = false,
    bool HasParagraphRenderingMarkup = false,
    bool HasImageRenderingMarkup = false);

public sealed record TeamsMentionEvidence(string Type, string MentionedId, string Text);
