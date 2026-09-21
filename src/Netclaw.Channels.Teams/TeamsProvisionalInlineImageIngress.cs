// -----------------------------------------------------------------------
// <copyright file="TeamsProvisionalInlineImageIngress.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Event;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;

namespace Netclaw.Channels.Teams;

/// <summary>
/// Applies Teams image transfer budgets and normalizes the provisional <c>image/*</c> shape.
/// Concrete MIME declarations retain the shared scanner checks.
/// </summary>
internal static class TeamsProvisionalInlineImageIngress
{
    private const int HeaderReadSize = 64;

    public static bool IsImage(TeamsAttachmentMetadata attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        if (attachment.Kind is not (TeamsInboundAttachmentKind.InlineImage or TeamsInboundAttachmentKind.PersonalFile)
            || string.IsNullOrWhiteSpace(attachment.ContentType))
        {
            return false;
        }

        var mediaType = attachment.ContentType.Split(';', 2)[0].Trim();
        return (attachment.Kind == TeamsInboundAttachmentKind.InlineImage
                && string.Equals(mediaType, "image/*", StringComparison.OrdinalIgnoreCase))
            || MimeTypeCatalog.GetCategory(MimeTypeCatalog.NormalizeDeclaredForExtension(
                attachment.ContentType, Path.GetExtension(attachment.Name))) == AttachmentCategory.Image;
    }

    public static async Task<AttachmentIngestOutcome> IngestAsync(
        TeamsInboundActivity activity,
        TeamsAttachmentMetadata attachment,
        TrustAudience audience,
        ChannelAttachmentPolicy policy,
        bool inlineImages,
        string inboxDirectory,
        string stagingDirectory,
        object inboxWriteGate,
        TimeProvider timeProvider,
        IContentScanner scanner,
        ILoggingAdapter log,
        ITeamsAttachmentDownloader downloader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(inboxWriteGate);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(downloader);

        var declaredMime = new DeclaredMimeType(attachment.ContentType);
        if (!policy.Allows(AttachmentCategory.Image))
        {
            log.Warning(
                "attachment_rejected name={Name} mime={Mime} audience={Audience} category={Category} reason=category-not-allowed",
                attachment.Name, declaredMime.Value, audience, AttachmentCategory.Image);
            return NotAllowed(attachment.Name, AttachmentCategory.Image, audience);
        }

        if (attachment.DeclaredSizeBytes is { } declaredSize && declaredSize > policy.MaxFileBytes)
        {
            log.Warning(
                "attachment_rejected name={Name} mime={Mime} audience={Audience} size={Size} limit={Limit} reason=too-large",
                attachment.Name, declaredMime.Value, audience, declaredSize, policy.MaxFileBytes);
            return Reject($"`{attachment.Name}` ({AttachmentIngressFormatting.FormatBytes(declaredSize)}) exceeds the {AttachmentIngressFormatting.FormatBytes(policy.MaxFileBytes)} per-file limit.");
        }

        var startedAt = timeProvider.GetTimestamp();
        using var deadlineCts = new CancellationTokenSource(TeamsIngressTimeouts.InlineImageDownload, timeProvider);
        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCts.Token);
        AttachmentDownloadResult download;
        try
        {
            download = await downloader.DownloadAsync(
                activity,
                attachment,
                stagingDirectory,
                policy.MaxFileBytes,
                downloadCts.Token).ConfigureAwait(false);
        }
        catch (AttachmentTooLargeException exception)
        {
            log.Warning(
                "attachment_rejected name={Name} mime={Mime} audience={Audience} size={Size} limit={Limit} reason=too-large-during-download",
                attachment.Name, declaredMime.Value, audience, exception.BytesReceived, exception.MaxBytes);
            return Reject($"`{attachment.Name}` ({AttachmentIngressFormatting.FormatBytes(exception.BytesReceived)}) exceeds the {AttachmentIngressFormatting.FormatBytes(exception.MaxBytes)} per-file limit.");
        }
        catch (Exception exception)
        {
            var diagnostic = exception as TeamsAttachmentDownloadException;
            var cancelled = exception is OperationCanceledException || diagnostic?.Cancelled == true;
            var reason = cancelled && cancellationToken.IsCancellationRequested
                ? "ingress-cancelled"
                : cancelled && deadlineCts.IsCancellationRequested
                    ? "download-deadline"
                    : diagnostic?.BodyIdleTimeout == true
                        ? "download-body-idle"
                        : exception is HttpRequestException || diagnostic?.HttpError == true
                            ? "download-http-error"
                            : "download-failed";
            log.Warning(
                $"attachment_rejected reason={reason} host_class={{HostClass}} authenticated={{Authenticated}} elapsed_ms={{ElapsedMs}} configured_deadline_ms={{ConfiguredDeadlineMs}} outer_cancellation_requested={{OuterCancellationRequested}} stage={{Stage}}",
                diagnostic?.HostClass ?? "unknown", diagnostic?.Authenticated ?? false,
                timeProvider.GetElapsedTime(startedAt).TotalMilliseconds, TeamsIngressTimeouts.InlineImageDownload.TotalMilliseconds,
                cancellationToken.IsCancellationRequested, diagnostic?.Stage ?? "request");
            if (reason == "ingress-cancelled")
                throw new OperationCanceledException("The Teams ingress was cancelled.", cancellationToken);
            return reason is "download-deadline" or "download-body-idle"
                ? Reject($"Timed out downloading `{attachment.Name}`. Please try again.")
                : Reject($"Couldn't download `{attachment.Name}` — please try again later.");
        }

        log.Info(
            "attachment_download_completed elapsed_ms={ElapsedMs} configured_deadline_ms={ConfiguredDeadlineMs} size={Size}",
            timeProvider.GetElapsedTime(startedAt).TotalMilliseconds,
            TeamsIngressTimeouts.InlineImageDownload.TotalMilliseconds, download.BytesWritten);

        if (download.BytesWritten == 0)
        {
            log.Warning(
                "attachment_rejected name={Name} mime={Mime} reason=empty-download",
                attachment.Name, declaredMime.Value);
            TryDeleteTemp(log, download.FilePath);
            return Reject($"`{attachment.Name}` downloaded as zero bytes.");
        }

        MimeType? detectedMime;
        try
        {
            detectedMime = await DetectMimeAsync(download.FilePath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteTemp(log, download.FilePath);
            throw;
        }
        if (detectedMime is null)
        {
            log.Warning(
                "attachment_rejected name={Name} mime={Mime} reason=provisional-image-signature-unrecognized",
                attachment.Name, declaredMime.Value);
            TryDeleteTemp(log, download.FilePath);
            return Reject($"Content scanner rejected `{attachment.Name}`: a verified image signature is required.");
        }

        var provisional = string.Equals(declaredMime.Value.Split(';', 2)[0].Trim(), "image/*", StringComparison.OrdinalIgnoreCase);
        var verifiedName = provisional ? CreateVerifiedName(attachment, detectedMime.Value) : FilenameSanitizer.Sanitize(attachment.Name);
        ContentVerificationResult verification;
        try
        {
            verification = await ContentVerification.ResolveAsync(
                scanner,
                download.FilePath,
                verifiedName,
                provisional ? new DeclaredMimeType(detectedMime.Value.Value) : declaredMime,
                policy,
                TeamsIngressTimeouts.AttachmentOperation,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteTemp(log, download.FilePath);
            throw;
        }

        if (verification is not ContentVerificationResult.Verified verified
            || verified.Category != AttachmentCategory.Image
            || !string.Equals(verified.MimeType.Value, detectedMime.Value.Value, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteTemp(log, download.FilePath);
            return RejectVerification(verification, attachment.Name, declaredMime.Value, audience, log);
        }

        string inboxPath;
        try
        {
            // The shared writer assumes serialized reservation and rename. This gate belongs to the Teams batch.
            lock (inboxWriteGate)
                inboxPath = InboxWriter.SanitizeReserveAndMove(inboxDirectory, verifiedName, download.FilePath);
        }
        catch (InboxWriter.CollisionExhaustedException exception)
        {
            log.Warning(exception,
                "attachment_rejected name={Name} reason=collision-exhausted",
                verifiedName);
            TryDeleteTemp(log, download.FilePath);
            return Reject($"Too many attachments named `{verifiedName}` in this session — please rename and try again.");
        }
        catch (Exception exception)
        {
            log.Error(exception,
                "attachment_rejected name={Name} reason=inbox-write-failed",
                verifiedName);
            TryDeleteTemp(log, download.FilePath);
            return Reject($"Couldn't save `{verifiedName}` — please try again later.");
        }

        var projection = await AttachmentIngressFormatting.BuildAcceptedProjectionAsync(
            inboxPath,
            verifiedName,
            verified.MimeType.Value,
            verified.Category,
            inlineImages,
            download.BytesWritten,
            cancellationToken).ConfigureAwait(false);

        log.Info(
            "attachment_accepted name={Name} declaredMime={DeclaredMime} verifiedMime={VerifiedMime} size={Size} category={Category} inlined={Inlined}",
            verifiedName, declaredMime.Value, verified.MimeType.Value, download.BytesWritten, verified.Category, projection.Inlined);

        return new AttachmentIngestOutcome.Accepted(projection.Line, projection.InlineContent);
    }

    private static async Task<MimeType?> DetectMimeAsync(string filePath, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderReadSize];
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            HeaderReadSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        var detected = MagicByteValidator.DetectMimeType(header.AsSpan(0, read));
        if (detected is null)
            return null;

        return new MimeType(detected);
    }

    private static string CreateVerifiedName(TeamsAttachmentMetadata attachment, MimeType mimeType)
    {
        var safeName = FilenameSanitizer.Sanitize(attachment.Name);
        var stem = Path.GetFileNameWithoutExtension(safeName);
        if (string.IsNullOrWhiteSpace(stem))
            stem = $"attachment-{attachment.SourceIndex + 1}";

        return FilenameSanitizer.Sanitize(stem + MimeTypeCatalog.ExtensionFor(mimeType));
    }

    private static AttachmentIngestOutcome.Rejected RejectVerification(
        ContentVerificationResult verification,
        string name,
        string declaredMime,
        TrustAudience audience,
        ILoggingAdapter log)
    {
        switch (verification)
        {
            case ContentVerificationResult.ScanThrew scanThrew:
                log.Warning(scanThrew.Exception,
                    "attachment_rejected name={Name} mime={Mime} reason=scan-exception",
                    name, declaredMime);
                return Reject($"Couldn't scan `{name}` — please try again later.");

            case ContentVerificationResult.ScanBlocked scanBlocked:
                log.Warning(
                    "attachment_rejected name={Name} mime={Mime} reason=scan-blocked error={ScanError} message={ScanMessage}",
                    name, declaredMime, scanBlocked.Error?.ToString(), scanBlocked.Message ?? scanBlocked.Error?.ToString());
                return scanBlocked.Error == ContentScanError.ScanFailure
                    ? Reject($"Couldn't scan `{name}` — please try again later.")
                    : Reject($"Content scanner rejected `{name}`: {scanBlocked.Message ?? scanBlocked.Error?.ToString() ?? "unknown error"}.");

            case ContentVerificationResult.MissingVerifiedMime:
                log.Warning(
                    "attachment_rejected name={Name} declaredMime={DeclaredMime} reason=missing-verified-mime",
                    name, declaredMime);
                return Reject($"Content scanner did not verify `{name}`. Please try again later.");

            case ContentVerificationResult.CategoryNotAllowed notAllowed:
                log.Warning(
                    "attachment_rejected name={Name} declaredMime={DeclaredMime} verifiedMime={VerifiedMime} audience={Audience} category={Category} reason=verified-category-not-allowed",
                    name, declaredMime, notAllowed.MimeType.Value, audience, notAllowed.Category);
                return NotAllowed(name, notAllowed.Category, audience);

            case ContentVerificationResult.Verified verified:
                log.Warning(
                    "attachment_rejected name={Name} declaredMime={DeclaredMime} verifiedMime={VerifiedMime} reason=provisional-image-verification-mismatch",
                    name, declaredMime, verified.MimeType.Value);
                return Reject($"Content scanner rejected `{name}`: a verified image signature is required.");

            default:
                throw new InvalidOperationException($"Unhandled content verification result: {verification.GetType().Name}");
        }
    }

    private static AttachmentIngestOutcome.Rejected NotAllowed(
        string name,
        AttachmentCategory category,
        TrustAudience audience) =>
        new($"`{name}` ({category}) isn't allowed in {audience} channels. " +
            "Please DM me if you want to share this class of file.");

    private static AttachmentIngestOutcome.Rejected Reject(string reason) => new(reason);

    private static void TryDeleteTemp(ILoggingAdapter log, string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (Exception exception)
        {
            log.Error(exception, "Failed to clean up staged attachment file {Path}", tempPath);
        }
    }
}
