// -----------------------------------------------------------------------
// <copyright file="ContentVerification.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Media;

namespace Netclaw.Security;

/// <summary>
/// The single content-verification decision shared by every attachment ingress
/// path — live and historical, across all channels. Scans the downloaded/cached
/// bytes, requires a scanner-verified MIME, and re-gates the verified category
/// against audience policy. Lives in the security layer (alongside
/// <see cref="IContentScanner"/> and <see cref="ContentPolicy"/>) so any ingress
/// surface can reuse the gate without depending on the channel layer.
///
/// It makes the decision only — it does not log, format user-facing copy, or
/// touch the staging file. Each caller projects the structured
/// <see cref="ContentVerificationResult"/> into its own logging, rejection
/// wording, and cleanup, because those differ by lifecycle (a live upload
/// replies to the user; a backfilled attachment annotates rehydrated context).
/// Keeping the decision here means a security change cannot land in one path
/// and miss the other.
/// </summary>
public static class ContentVerification
{
    public static async Task<ContentVerificationResult> ResolveAsync(
        IContentScanner scanner,
        string filePath,
        string filename,
        DeclaredMimeType declaredMimeType,
        ChannelAttachmentPolicy policy,
        TimeSpan scanTimeout,
        CancellationToken cancellationToken)
    {
        ContentScanResult scanResult;
        try
        {
            using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            scanCts.CancelAfter(scanTimeout);
            scanResult = await scanner.ScanFileAsync(
                filePath, filename, declaredMimeType.Value, scanCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Outer cancellation (host/session shutdown) — propagate it instead
            // of masking it as a scan failure. A scan timeout fired by the linked
            // CTS (outer token NOT cancelled) still falls through to ScanThrew.
            throw;
        }
        catch (Exception ex)
        {
            return new ContentVerificationResult.ScanThrew(ex);
        }

        if (!scanResult.IsAllowed)
            return new ContentVerificationResult.ScanBlocked(scanResult.Error, scanResult.Message);

        if (scanResult.VerifiedMimeType is not { } verified)
            return new ContentVerificationResult.MissingVerifiedMime();

        var mimeType = verified.MimeType;
        var category = MimeTypeCatalog.GetCategory(mimeType);
        if (!policy.Allows(category))
            return new ContentVerificationResult.CategoryNotAllowed(mimeType, category);

        return new ContentVerificationResult.Verified(mimeType, category);
    }
}

/// <summary>
/// Outcome of <see cref="ContentVerification.ResolveAsync"/>. Carries the
/// structured facts each caller needs to log and to phrase its own rejection.
/// </summary>
public abstract record ContentVerificationResult
{
    public sealed record Verified(MimeType MimeType, AttachmentCategory Category) : ContentVerificationResult;

    public sealed record ScanThrew(Exception Exception) : ContentVerificationResult;

    public sealed record ScanBlocked(ContentScanError? Error, string? Message) : ContentVerificationResult;

    public sealed record MissingVerifiedMime : ContentVerificationResult;

    public sealed record CategoryNotAllowed(MimeType MimeType, AttachmentCategory Category) : ContentVerificationResult;
}
