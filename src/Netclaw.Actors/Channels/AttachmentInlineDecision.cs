// -----------------------------------------------------------------------
// <copyright file="AttachmentInlineDecision.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Media;

namespace Netclaw.Actors.Channels;

public enum ImageInputRoute
{
    None,
    Direct,
    Proxy
}

/// <summary>
/// Shared inline-vs-path-only decision for channel attachments and local file inspection.
/// </summary>
public static class AttachmentInlineDecision
{
    public static ImageInputRoute SelectImageRoute(
        ModelModality inputModalities,
        bool imageProxyEnabled)
        => inputModalities.HasFlag(ModelModality.Image)
            ? ImageInputRoute.Direct
            : imageProxyEnabled
                ? ImageInputRoute.Proxy
                : ImageInputRoute.None;

    public static (bool Inlined, string? Note) Resolve(MimeType mimeType, AttachmentCategory category, bool inlineImages)
        => Resolve(mimeType, category, inlineImages ? ImageInputRoute.Direct : ImageInputRoute.None);

    public static (bool Inlined, string? Note) Resolve(
        MimeType mimeType,
        AttachmentCategory category,
        ImageInputRoute imageRoute)
    {
        if (category == AttachmentCategory.Image)
        {
            if (imageRoute == ImageInputRoute.None)
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
