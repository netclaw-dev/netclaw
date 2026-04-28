// -----------------------------------------------------------------------
// <copyright file="AttachmentNotes.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Channels;

/// <summary>
/// Canonical strings for the <c>note</c> field on <c>[attachment]</c>
/// announcements emitted by channel ingress adapters. These are normative
/// per <c>netclaw-input-adapters</c> so the agent's dynamic-context hint
/// and eval harness can branch on stable textual prefixes rather than
/// ad-hoc phrasing. Channel adapters SHALL source note text exclusively
/// from this class — never open-code a variant.
/// </summary>
public static class AttachmentNotes
{
    /// <summary>
    /// Model-modality gap note for an image attachment on a model that
    /// does not report <c>ModelModality.Image</c> as an input modality.
    /// MUST begin with <c>"current model has no image modality"</c> per
    /// the spec so the agent's dynamic-context hint can detect this class.
    /// </summary>
    public const string ModelMissingImage =
        "current model has no image modality; file is on disk but not viewable this turn";

    /// <summary>
    /// Model-modality gap note for a PDF attachment on a model that does
    /// not natively accept <c>application/pdf</c> as input. MUST begin
    /// with <c>"current model has no native PDF support"</c> per the spec.
    /// </summary>
    public const string ModelMissingPdf =
        "current model has no native PDF support; use shell_execute (e.g., pdftotext) to extract text";

    /// <summary>
    /// Format-not-inlineable note for categories that no model can render
    /// natively (documents, archives, video, audio, unknown binaries).
    /// MUST begin with <c>"format not inlineable"</c> per the spec.
    /// </summary>
    public const string FormatNotInlineable =
        "format not inlineable; use file_read or shell_execute to process";
}
