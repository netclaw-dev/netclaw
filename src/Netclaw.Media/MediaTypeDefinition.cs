// -----------------------------------------------------------------------
// <copyright file="MediaTypeDefinition.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Media;

public sealed record MediaTypeDefinition
{
    internal MediaTypeDefinition(
        string mimeType,
        AttachmentCategory category,
        MediaKind mediaKind,
        MediaContentKind contentKind,
        SignatureFamily signatureFamily,
        bool supportsModelInput,
        string defaultExtension,
        string[] extensions)
    {
        MimeType = new MimeType(mimeType);
        Category = category;
        MediaKind = mediaKind;
        ContentKind = contentKind;
        SignatureFamily = signatureFamily;
        SupportsModelInput = supportsModelInput;
        DefaultExtension = new FileExtension(defaultExtension);
        Extensions = extensions.Select(static e => new FileExtension(e)).ToArray();
    }

    public MimeType MimeType { get; }

    public AttachmentCategory Category { get; }

    public MediaKind MediaKind { get; }

    public MediaContentKind ContentKind { get; }

    /// <summary>
    /// Byte-signature family. The scanner derives its MIME → matcher table from
    /// this value, so the catalog is the single source of truth.
    /// </summary>
    public SignatureFamily SignatureFamily { get; }

    /// <summary>
    /// True when the scanner has a byte-signature matcher for this type. A type
    /// with no family (<see cref="SignatureFamily.None"/>) is not scannable.
    /// </summary>
    public bool SupportsNativeSignatureValidation => SignatureFamily != SignatureFamily.None;

    public bool SupportsModelInput { get; }

    public FileExtension DefaultExtension { get; }

    public IReadOnlyList<FileExtension> Extensions { get; }
}
