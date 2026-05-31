// -----------------------------------------------------------------------
// <copyright file="AttachmentCategory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Media;

/// <summary>
/// Coarse policy classes for inbound file attachments.
/// </summary>
public enum AttachmentCategory
{
    Image,
    Pdf,
    Document,
    Archive,
    Media,
    Other
}
