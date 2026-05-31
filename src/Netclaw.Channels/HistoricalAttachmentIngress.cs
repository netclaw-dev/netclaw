// -----------------------------------------------------------------------
// <copyright file="HistoricalAttachmentIngress.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;

namespace Netclaw.Channels;

/// <summary>
/// Shared security core for historical (thread-backfill) attachment ingress
/// across Slack, Discord, and Mattermost. The scan → verified-MIME →
/// verified-category gate is identical for every channel and for both the
/// freshly-downloaded and the already-cached file, so it lives here once.
/// Channels keep only what genuinely differs — URL/auth trust and the download
/// mechanism. Centralizing this is what closes the class of channel-specific
/// bug where one path (notably the inbox cache-hit) skips the scan and serves
/// the unverified declared MIME.
/// </summary>
public static class HistoricalAttachmentIngress
{
    /// <summary>
    /// Canonical historical-attachment rejection note. The LLM sees this in
    /// place of the attachment so rejections are never silent.
    /// </summary>
    public static TextContent BuildRejected(string detail)
        => new($"[attachment rejected: {detail}]");

    public abstract record ScanOutcome
    {
        public sealed record Verified(MimeType MimeType, AttachmentCategory Category) : ScanOutcome;

        public sealed record Rejected(TextContent Note) : ScanOutcome;
    }

    /// <summary>
    /// Provisional pre-download gate shared by all three historical fetchers:
    /// classifies the declared MIME (corrected by extension) and rejects on
    /// disallowed category or oversize before spending bandwidth. Returns the
    /// rejection note to emit, or <c>null</c> to proceed to download/scan. The
    /// authoritative category gate still runs on the scanner-verified MIME.
    /// </summary>
    public static TextContent? CheckPreDownload(
        string filename,
        DeclaredMimeType declaredMimeType,
        long size,
        TrustAudience audience,
        ChannelAttachmentPolicy policy,
        ILogger logger)
    {
        var provisionalMimeType = MimeTypeCatalog.NormalizeDeclaredForExtension(
            declaredMimeType.Value, Path.GetExtension(filename));
        var category = MimeTypeCatalog.GetCategory(provisionalMimeType);

        if (!policy.Allows(category))
        {
            logger.LogWarning(
                "Historical attachment {Name} rejected: category {Category} not allowed for {Audience}",
                filename, category, audience);
            return BuildRejected($"historical attachment ({declaredMimeType.Value}) category not allowed in {audience}");
        }

        if (size > policy.MaxFileBytes)
        {
            logger.LogWarning(
                "Historical attachment {Name} rejected: size {Size} exceeds {Limit}",
                filename, size, policy.MaxFileBytes);
            return BuildRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" exceeds the {AttachmentIngressFormatting.FormatBytes(policy.MaxFileBytes)} per-file limit");
        }

        return null;
    }

    /// <summary>
    /// Scans a file already on disk (freshly downloaded staging file or a
    /// previously-cached inbox file) and enforces the verified-MIME and
    /// verified-category gates. Does NOT delete the file — the caller owns the
    /// staging-file lifecycle, since a cache hit must not delete the inbox copy.
    /// </summary>
    public static async Task<ScanOutcome> ScanAndVerifyAsync(
        IContentScanner scanner,
        string filePath,
        string filename,
        DeclaredMimeType declaredMimeType,
        TrustAudience audience,
        ChannelAttachmentPolicy policy,
        TimeSpan scanTimeout,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var verification = await ContentVerification.ResolveAsync(
            scanner, filePath, filename, declaredMimeType, policy, scanTimeout, cancellationToken);

        switch (verification)
        {
            case ContentVerificationResult.Verified verified:
                return new ScanOutcome.Verified(verified.MimeType, verified.Category);

            case ContentVerificationResult.ScanThrew st:
                logger.LogWarning(st.Exception, "Historical attachment scan threw for {Name}", filename);
                return new ScanOutcome.Rejected(BuildRejected(
                    $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" could not be scanned"));

            case ContentVerificationResult.ScanBlocked sb:
                logger.LogWarning(
                    "Historical attachment {Name} rejected by scanner: {Error} {Message}",
                    filename, sb.Error?.ToString(), sb.Message ?? string.Empty);
                return new ScanOutcome.Rejected(BuildRejected(
                    $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" was rejected by content scanning: {AttachmentIngressFormatting.EscapeQuoted(sb.Message ?? sb.Error?.ToString() ?? "unknown error")}"));

            case ContentVerificationResult.MissingVerifiedMime:
                logger.LogWarning(
                    "Historical attachment {Name} rejected: scanner did not return verified MIME",
                    filename);
                return new ScanOutcome.Rejected(BuildRejected(
                    $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" could not be verified by content scanning"));

            case ContentVerificationResult.CategoryNotAllowed notAllowed:
                logger.LogWarning(
                    "Historical attachment {Name} rejected: verified category {Category} not allowed for {Audience}",
                    filename, notAllowed.Category, audience);
                return new ScanOutcome.Rejected(BuildRejected(
                    $"historical attachment ({notAllowed.MimeType.Value}) category not allowed in {audience}"));

            default:
                throw new InvalidOperationException(
                    $"Unhandled content verification result: {verification.GetType().Name}");
        }
    }
}
