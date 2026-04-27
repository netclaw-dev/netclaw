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
            var info = new FileInfo(filePath);
            Span<byte> header = stackalloc byte[HeaderReadSize];
            int bytesRead;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bytesRead = fs.Read(header);
            }

            var result = MagicByteValidator.ValidateFromHeader(
                header[..bytesRead], info.Length, declaredMimeType, filename, _policy);
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
