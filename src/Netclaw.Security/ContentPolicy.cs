using System.Collections.Frozen;

namespace Netclaw.Security;

/// <summary>
/// Configurable content security policy for file uploads. Layered on top of
/// <see cref="MagicByteValidator"/>: the validator advertises which MIME
/// types it knows how to verify, and this policy is the runtime allowlist
/// that operators can use to further restrict what passes.
/// </summary>
public sealed class ContentPolicy
{
    /// <summary>
    /// Default maximum file size: 25 MiB. Tracks
    /// <c>ChannelAttachmentPolicy.DefaultMaxFileBytes</c> so the scanner
    /// ceiling and the channel policy ceiling don't drift.
    /// </summary>
    public const long DefaultMaxFileSizeBytes = 25L * 1024 * 1024;

    /// <summary>
    /// MIME types allowed through the content scanner. Defaults to the full
    /// set of MIME types <see cref="MagicByteValidator"/> knows how to verify
    /// — images, PDF, Office documents, plain/rich text, archives, and
    /// common audio/video containers. Operators can override this to
    /// restrict the allowlist further.
    /// </summary>
    public FrozenSet<string> AllowedMimeTypes { get; init; } = DefaultAllowedMimeTypes;

    /// <summary>
    /// Maximum file size in bytes.
    /// </summary>
    public long MaxFileSizeBytes { get; init; } = DefaultMaxFileSizeBytes;

    public static readonly FrozenSet<string> DefaultAllowedMimeTypes =
        MagicByteValidator.GetSupportedMimeTypes()
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
