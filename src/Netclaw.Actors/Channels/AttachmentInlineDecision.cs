// -----------------------------------------------------------------------
// <copyright file="AttachmentInlineDecision.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Media;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Shared inline-vs-path-only decision for channel attachments and local file inspection.
/// </summary>
public static class AttachmentInlineDecision
{
    public static (bool Inlined, string? Note) Resolve(MimeType mimeType, AttachmentCategory category, bool inlineImages)
    {
        if (category == AttachmentCategory.Image)
        {
            if (!inlineImages)
                return (false, AttachmentNotes.ModelMissingImage);

            // Only inline image types the provider can actually ingest as model
            // input (png/jpeg/gif/webp). Other image formats (e.g. bmp/tiff) are
            // accepted but delivered path-only, so they never reach the
            // image-only provider serialization path.
            return MimeTypeCatalog.IsModelInputSupported(mimeType)
                ? (true, null)
                : (false, AttachmentNotes.FormatNotInlineable);
        }

        return category switch
        {
            AttachmentCategory.Pdf => (false, AttachmentNotes.ModelMissingPdf),
            _ => (false, AttachmentNotes.FormatNotInlineable)
        };
    }
}
