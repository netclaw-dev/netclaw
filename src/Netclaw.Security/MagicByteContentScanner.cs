namespace Netclaw.Security;

/// <summary>
/// Production content scanner that enforces <see cref="ContentPolicy"/>
/// using magic-byte validation.
/// </summary>
public sealed class MagicByteContentScanner(ContentPolicy policy) : IContentScanner
{
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
}
