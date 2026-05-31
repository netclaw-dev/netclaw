// -----------------------------------------------------------------------
// <copyright file="AttachmentIngressPipeline.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;

namespace Netclaw.Channels;

/// <summary>
/// Outcome of attempting to ingest a single inbound attachment. Shared across
/// Slack, Discord, and Mattermost so the accept/reject contract — and the
/// user-facing copy — stays identical regardless of source channel.
/// </summary>
public abstract record AttachmentIngestOutcome
{
    public sealed record Accepted(string Line, DataContent? Inline) : AttachmentIngestOutcome;

    public sealed record Rejected(string UserFacingReason) : AttachmentIngestOutcome;
}

/// <summary>
/// Channel-reported facts about one inbound attachment, before any download or
/// scan. <see cref="DeclaredMimeType"/> is transport metadata and is treated as
/// untrusted — the canonical category used for the accept gate comes from the
/// content scanner's verified MIME, not this value.
/// </summary>
public readonly record struct AttachmentIngressRequest(string Name, string DeclaredMimeType, long Size);

/// <summary>
/// Downloads the attachment bytes into <paramref name="stagingDir"/>, honoring
/// the per-policy byte ceiling <paramref name="maxBytes"/>. Channel-specific:
/// each platform supplies its own URL/auth handling. May throw
/// <see cref="AttachmentTooLargeException"/> when the stream exceeds the limit.
/// </summary>
public delegate Task<AttachmentDownloadResult> AttachmentStagingDownload(
    string stagingDir, long maxBytes, CancellationToken cancellationToken);

/// <summary>
/// Shared inbound-attachment ingress pipeline for chat channels. Owns the
/// security-sensitive orchestration that is identical across platforms:
/// provisional audience gate, size gate, download error handling, content
/// scan, verified-MIME requirement, verified-category re-gate, inbox write,
/// and capability-gated inlining. Platform code supplies only the parts that
/// genuinely differ — URL/auth trust (via <c>preDownloadGate</c>) and the
/// download mechanism (via <see cref="AttachmentStagingDownload"/>).
/// </summary>
public static class AttachmentIngressPipeline
{
    public static async Task<AttachmentIngestOutcome> IngestAsync(
        AttachmentIngressRequest request,
        TrustAudience audience,
        ChannelAttachmentPolicy policy,
        bool inlineImages,
        string inboxDir,
        string stagingDir,
        TimeSpan operationTimeout,
        IContentScanner scanner,
        ILoggingAdapter log,
        AttachmentStagingDownload download,
        CancellationToken cancellationToken,
        Func<string?>? preDownloadGate = null)
    {
        var name = request.Name;
        var declaredMimeType = new DeclaredMimeType(request.DeclaredMimeType);

        // Provisional pre-download gate: declared MIME is corrected by extension
        // only to avoid burning bandwidth on files that obviously can't be
        // accepted. The authoritative gate runs on the scanner's verified MIME
        // after download.
        var provisionalMimeType = MimeTypeCatalog.NormalizeDeclaredForExtension(
            declaredMimeType.Value, Path.GetExtension(name));
        var category = MimeTypeCatalog.GetCategory(provisionalMimeType);

        if (!policy.Allows(category))
        {
            log.Warning(
                "attachment_rejected name={Name} mime={Mime} audience={Audience} category={Category} reason=category-not-allowed",
                name, declaredMimeType.Value, audience, category);
            return NotAllowed(name, category, audience);
        }

        if (request.Size > policy.MaxFileBytes)
        {
            log.Warning(
                "attachment_rejected name={Name} mime={Mime} audience={Audience} size={Size} limit={Limit} reason=too-large",
                name, declaredMimeType.Value, audience, request.Size, policy.MaxFileBytes);
            return Reject($"`{name}` ({FormatBytes(request.Size)}) exceeds the {FormatBytes(policy.MaxFileBytes)} per-file limit.");
        }

        // Platform-specific URL/auth trust. The gate logs its own structured
        // warning and returns the user-facing rejection copy.
        if (preDownloadGate?.Invoke() is { } gateRejection)
            return Reject(gateRejection);

        AttachmentDownloadResult downloadResult;
        try
        {
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadCts.CancelAfter(operationTimeout);
            downloadResult = await download(stagingDir, policy.MaxFileBytes, downloadCts.Token);
        }
        catch (AttachmentTooLargeException ex)
        {
            log.Warning(
                "attachment_rejected name={Name} mime={Mime} audience={Audience} size={Size} limit={Limit} reason=too-large-during-download",
                name, declaredMimeType.Value, audience, ex.BytesReceived, ex.MaxBytes);
            return Reject($"`{name}` ({FormatBytes(ex.BytesReceived)}) exceeds the {FormatBytes(ex.MaxBytes)} per-file limit.");
        }
        catch (OperationCanceledException ex)
        {
            log.Warning(ex,
                "attachment_rejected name={Name} mime={Mime} reason=download-timeout",
                name, declaredMimeType.Value);
            return Reject($"Timed out downloading `{name}`. Please try again.");
        }
        catch (Exception ex)
        {
            log.Warning(ex,
                "attachment_rejected name={Name} mime={Mime} reason=download-failed",
                name, declaredMimeType.Value);
            return Reject($"Couldn't download `{name}` — please try again later.");
        }

        if (downloadResult.BytesWritten == 0)
        {
            log.Warning(
                "attachment_rejected name={Name} mime={Mime} reason=empty-download",
                name, declaredMimeType.Value);
            TryDeleteTemp(log, downloadResult.FilePath);
            return Reject($"`{name}` downloaded as zero bytes.");
        }

        var verification = await ContentVerification.ResolveAsync(
            scanner, downloadResult.FilePath, name, declaredMimeType, policy, operationTimeout, cancellationToken);

        if (verification is not ContentVerificationResult.Verified verified)
        {
            TryDeleteTemp(log, downloadResult.FilePath);
            switch (verification)
            {
                case ContentVerificationResult.ScanThrew st:
                    log.Warning(st.Exception,
                        "attachment_rejected name={Name} mime={Mime} reason=scan-exception",
                        name, declaredMimeType.Value);
                    return Reject($"Couldn't scan `{name}` — please try again later.");

                case ContentVerificationResult.ScanBlocked sb:
                    log.Warning(
                        "attachment_rejected name={Name} mime={Mime} reason=scan-blocked error={ScanError} message={ScanMessage}",
                        name, declaredMimeType.Value, sb.Error?.ToString(), sb.Message ?? sb.Error?.ToString());
                    return sb.Error == ContentScanError.ScanFailure
                        ? Reject($"Couldn't scan `{name}` — please try again later.")
                        : Reject($"Content scanner rejected `{name}`: {sb.Message ?? sb.Error?.ToString() ?? "unknown error"}.");

                case ContentVerificationResult.MissingVerifiedMime:
                    log.Warning(
                        "attachment_rejected name={Name} declaredMime={DeclaredMime} reason=missing-verified-mime",
                        name, declaredMimeType.Value);
                    return Reject($"Content scanner did not verify `{name}`. Please try again later.");

                case ContentVerificationResult.CategoryNotAllowed notAllowed:
                    log.Warning(
                        "attachment_rejected name={Name} declaredMime={DeclaredMime} verifiedMime={VerifiedMime} audience={Audience} category={Category} reason=verified-category-not-allowed",
                        name, declaredMimeType.Value, notAllowed.MimeType.Value, audience, notAllowed.Category);
                    return NotAllowed(name, notAllowed.Category, audience);

                default:
                    throw new InvalidOperationException(
                        $"Unhandled content verification result: {verification.GetType().Name}");
            }
        }

        var verifiedMime = verified.MimeType;
        var verifiedCategory = verified.Category;

        string inboxPath;
        try
        {
            inboxPath = InboxWriter.SanitizeReserveAndMove(inboxDir, name, downloadResult.FilePath);
        }
        catch (InboxWriter.CollisionExhaustedException ex)
        {
            log.Warning(ex,
                "attachment_rejected name={Name} reason=collision-exhausted",
                name);
            TryDeleteTemp(log, downloadResult.FilePath);
            return Reject($"Too many attachments named `{name}` in this session — please rename and try again.");
        }
        catch (Exception ex)
        {
            log.Error(ex,
                "attachment_rejected name={Name} reason=inbox-write-failed",
                name);
            TryDeleteTemp(log, downloadResult.FilePath);
            return Reject($"Couldn't save `{name}` — please try again later.");
        }

        var projection = await AttachmentIngressFormatting.BuildAcceptedProjectionAsync(
            inboxPath,
            name,
            verifiedMime.Value,
            verifiedCategory,
            inlineImages,
            downloadResult.BytesWritten,
            cancellationToken);

        log.Info(
            "attachment_accepted name={Name} declaredMime={DeclaredMime} verifiedMime={VerifiedMime} size={Size} category={Category} inlined={Inlined}",
            name, declaredMimeType.Value, verifiedMime.Value, downloadResult.BytesWritten, verifiedCategory, projection.Inlined);

        return new AttachmentIngestOutcome.Accepted(projection.Line, projection.InlineContent);
    }

    private static AttachmentIngestOutcome.Rejected Reject(string reason) => new(reason);

    private static AttachmentIngestOutcome.Rejected NotAllowed(
        string name, AttachmentCategory category, TrustAudience audience) =>
        new($"`{name}` ({category}) isn't allowed in {audience} channels. " +
            "Please DM me if you want to share this class of file.");

    private static string FormatBytes(long size) => AttachmentIngressFormatting.FormatBytes(size);

    private static void TryDeleteTemp(ILoggingAdapter log, string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to clean up staged attachment file {Path}", tempPath);
        }
    }
}
