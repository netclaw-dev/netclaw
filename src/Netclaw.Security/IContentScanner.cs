namespace Netclaw.Security;

/// <summary>
/// Scans file content for security threats before it enters the pipeline.
/// Called at the channel adapter boundary (e.g., Slack file downloads).
/// </summary>
public interface IContentScanner
{
    Task<ContentScanResult> ScanAsync(
        ReadOnlyMemory<byte> content,
        string filename,
        string declaredMimeType,
        CancellationToken cancellationToken = default);
}
