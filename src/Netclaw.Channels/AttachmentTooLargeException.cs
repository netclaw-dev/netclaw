namespace Netclaw.Channels;

/// <summary>
/// Thrown by <see cref="StreamingAttachmentDownloader"/> when a download
/// exceeds the operator-configured byte ceiling. Channel actors catch this
/// and convert to a user-visible rejection reply.
/// </summary>
public sealed class AttachmentTooLargeException(long bytesReceived, long maxBytes)
    : InvalidOperationException(
        $"Attachment download exceeded {maxBytes} byte ceiling (received {bytesReceived} bytes)")
{
    public long BytesReceived { get; } = bytesReceived;
    public long MaxBytes { get; } = maxBytes;
}
