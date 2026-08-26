// -----------------------------------------------------------------------
// <copyright file="IContentScanner.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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

    /// <summary>
    /// Scans a file already on disk. Default implementation reads the entire
    /// file into memory and delegates to the byte-based overload.
    /// Implementations may override for more efficient handling (e.g.,
    /// reading only the file header for magic-byte validation).
    /// </summary>
    Task<ContentScanResult> ScanFileAsync(
        string filePath,
        string filename,
        string declaredMimeType,
        CancellationToken cancellationToken = default)
    {
        var bytes = File.ReadAllBytes(filePath);
        return ScanAsync(bytes, filename, declaredMimeType, cancellationToken);
    }
}
