// -----------------------------------------------------------------------
// <copyright file="ChannelAttachmentPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Media;

namespace Netclaw.Configuration;

/// <summary>
/// Per-audience policy for inbound channel attachments. Channel adapters query
/// this from the resolved <see cref="ToolAudienceProfile"/> before building
/// <c>ChannelInput.Contents</c> for an inbound message. The empty policy
/// (<see cref="Empty"/>) denies every category and is the fail-closed default.
/// </summary>
public sealed class ChannelAttachmentPolicy
{
    public const long DefaultMaxFileBytes = 25L * 1024 * 1024;
    public const int DefaultMaxFilesPerMessage = 10;

    /// <summary>
    /// Categories allowed for this audience. Empty means all attachments are
    /// rejected regardless of category.
    /// </summary>
    public List<AttachmentCategory> AllowedCategories { get; set; } = [];

    /// <summary>
    /// Maximum per-file byte size. Files whose transport-reported size exceeds
    /// this are rejected before download.
    /// </summary>
    public long MaxFileBytes { get; set; } = DefaultMaxFileBytes;

    /// <summary>
    /// Maximum number of attached files accepted on a single inbound message.
    /// Inbound messages exceeding this are rejected with a user-visible reply.
    /// </summary>
    public int MaxFilesPerMessage { get; set; } = DefaultMaxFilesPerMessage;

    /// <summary>
    /// Fail-closed policy: no categories permitted, zero size, zero file
    /// count. Used as the default value for <see cref="ToolAudienceProfile.ChannelAttachments"/>
    /// so an unconfigured profile rejects every attachment until operators
    /// opt in.
    /// </summary>
    public static ChannelAttachmentPolicy Empty => new()
    {
        AllowedCategories = [],
        MaxFileBytes = 0,
        MaxFilesPerMessage = 0
    };

    public bool Allows(AttachmentCategory category) => AllowedCategories.Contains(category);
}
