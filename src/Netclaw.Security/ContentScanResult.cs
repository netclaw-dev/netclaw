// -----------------------------------------------------------------------
// <copyright file="ContentScanResult.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Media;

namespace Netclaw.Security;

/// <summary>
/// Result of scanning file content for security threats.
/// </summary>
public sealed record ContentScanResult(
    bool IsAllowed,
    ContentScanError? Error = null,
    MimeType? DetectedMimeType = null,
    string? Message = null,
    VerifiedMimeType? VerifiedMimeType = null)
{
    public static ContentScanResult Allowed(MimeType detectedMimeType)
    {
        var verifiedMimeType = new VerifiedMimeType(detectedMimeType);
        return new(true, null, detectedMimeType, VerifiedMimeType: verifiedMimeType);
    }

    public static ContentScanResult Allowed(VerifiedMimeType verifiedMimeType) =>
        new(true, null, verifiedMimeType.MimeType, VerifiedMimeType: verifiedMimeType);

    public static ContentScanResult Rejected(
        ContentScanError error,
        string message,
        MimeType? detectedMimeType = null) =>
        new(false, error, detectedMimeType, message);
}

/// <summary>
/// Errors that can occur during content scanning.
/// </summary>
public enum ContentScanError
{
    UnrecognizedFileType,
    MimeTypeMismatch,
    ExecutableContent,
    EmptyContent,
    FileTooLarge,
    AntivirusDetection,
    ScanFailure
}
