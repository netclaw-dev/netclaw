namespace Netclaw.Security;

/// <summary>
/// Result of scanning file content for security threats.
/// </summary>
public sealed record ContentScanResult(
    bool IsAllowed,
    ContentScanError? Error = null,
    MimeType? DetectedMimeType = null,
    string? Message = null)
{
    public static ContentScanResult Allowed(MimeType? detectedMimeType = null) =>
        new(true, null, detectedMimeType);

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
