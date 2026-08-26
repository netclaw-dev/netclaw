// -----------------------------------------------------------------------
// <copyright file="MagicByteContentScanner.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security;

/// <summary>
/// Production content scanner that enforces <see cref="ContentPolicy"/>
/// using magic-byte validation.
/// </summary>
public sealed class MagicByteContentScanner(ContentPolicy policy) : IContentScanner
{
    private const int HeaderReadSize = 64;
    private readonly ContentPolicy _policy = policy;

    public Task<ContentScanResult> ScanAsync(
        ReadOnlyMemory<byte> content,
        string filename,
        string declaredMimeType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var result = MagicByteValidator.Validate(content.Span, declaredMimeType, filename, _policy);
            return Task.FromResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(ContentScanResult.Rejected(
                ContentScanError.ScanFailure,
                $"Content scan failed: {ex.Message}"));
        }
    }

    public Task<ContentScanResult> ScanFileAsync(
        string filePath,
        string filename,
        string declaredMimeType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            Span<byte> header = stackalloc byte[HeaderReadSize];
            int bytesRead;
            long fileSize;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fileSize = fs.Length;
                bytesRead = fs.Read(header);
            }

            var result = MagicByteValidator.ValidateFromHeader(
                header[..bytesRead], fileSize, declaredMimeType, filename, _policy);
            return Task.FromResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(ContentScanResult.Rejected(
                ContentScanError.ScanFailure,
                $"Content scan failed: {ex.Message}"));
        }
    }
}
