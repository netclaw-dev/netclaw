namespace Netclaw.Channels;

/// <summary>
/// Thrown by <see cref="StreamingAttachmentDownloader"/> when a download
/// exceeds the configured byte ceiling (either the per-policy limit or
/// the hardcoded <see cref="Configuration.ChannelAttachmentPolicy.AbsoluteMaxFileBytes"/>
/// safety valve). Channel actors catch this and convert to a user-visible
/// rejection reply.
/// </summary>
public sealed class AttachmentTooLargeException(long bytesReceived, long maxBytes)
    : InvalidOperationException(
        $"Attachment download exceeded {maxBytes} byte ceiling (received {bytesReceived} bytes)")
{
    public long BytesReceived { get; } = bytesReceived;
    public long MaxBytes { get; } = maxBytes;
}
