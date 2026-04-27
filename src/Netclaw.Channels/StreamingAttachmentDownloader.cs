namespace Netclaw.Channels;

/// <summary>
/// Streams HTTP file downloads directly to disk with a fixed-size buffer,
/// enforcing a byte ceiling during the copy. Channel adapters use this
/// instead of <c>HttpClient.GetByteArrayAsync</c> to avoid holding entire
/// attachments in managed memory.
/// </summary>
public static class StreamingAttachmentDownloader
{
    public const int DefaultBufferSize = 81_920;

    /// <summary>
    /// Downloads the resource at <paramref name="url"/> directly into a temp
    /// file in <paramref name="targetDirectory"/>. Returns the temp file path
    /// and actual bytes written on success. Deletes the temp file on any failure.
    /// </summary>
    /// <param name="httpClient">Pre-configured HttpClient (e.g., from IHttpClientFactory).</param>
    /// <param name="url">Remote file URL.</param>
    /// <param name="configureRequest">Optional callback to set auth headers (e.g., Bearer token for Slack).</param>
    /// <param name="targetDirectory">Directory to write the temp file into (typically the session inbox).</param>
    /// <param name="maxBytes">Operator-configured per-policy byte limit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<AttachmentDownloadResult> DownloadToFileAsync(
        HttpClient httpClient,
        string url,
        Action<HttpRequestMessage>? configureRequest,
        string targetDirectory,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        configureRequest?.Invoke(request);

        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maxBytes)
        {
            throw new AttachmentTooLargeException(contentLength, maxBytes);
        }

        var tempPath = Path.Combine(targetDirectory, $".download.{Guid.NewGuid():N}.tmp");
        try
        {
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: DefaultBufferSize, useAsync: true);

            var buffer = new byte[DefaultBufferSize];
            long totalBytesWritten = 0;
            int bytesRead;

            while ((bytesRead = await responseStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                totalBytesWritten += bytesRead;
                if (totalBytesWritten > maxBytes)
                {
                    throw new AttachmentTooLargeException(totalBytesWritten, maxBytes);
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }

            await fileStream.FlushAsync(cancellationToken);
            return new AttachmentDownloadResult(tempPath, totalBytesWritten);
        }
        catch
        {
            TryDeleteTemp(tempPath);
            throw;
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
            // best-effort cleanup; do not mask the original exception
        }
    }
}
