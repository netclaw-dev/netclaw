// -----------------------------------------------------------------------
// <copyright file="AttachmentInlineDecision.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Shared inline-vs-path-only decision for channel attachments and local file inspection.
/// </summary>
public static class AttachmentInlineDecision
{
    public static (bool Inlined, string? Note) Resolve(AttachmentCategory category, bool inlineImages)
    {
        return category switch
        {
            AttachmentCategory.Image when inlineImages => (true, null),
            AttachmentCategory.Image => (false, AttachmentNotes.ModelMissingImage),
            AttachmentCategory.Pdf => (false, AttachmentNotes.ModelMissingPdf),
            _ => (false, AttachmentNotes.FormatNotInlineable)
        };
    }
}
