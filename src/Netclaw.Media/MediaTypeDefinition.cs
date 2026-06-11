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
        bool supportsNativeSignatureValidation,
        bool supportsModelInput,
        string defaultExtension,
        string[] extensions)
    {
        MimeType = new MimeType(mimeType);
        Category = category;
        MediaKind = mediaKind;
        ContentKind = contentKind;
        SupportsNativeSignatureValidation = supportsNativeSignatureValidation;
        SupportsModelInput = supportsModelInput;
        DefaultExtension = new FileExtension(defaultExtension);
        Extensions = extensions.Select(static e => new FileExtension(e)).ToArray();
    }

    public MimeType MimeType { get; }

    public AttachmentCategory Category { get; }

    public MediaKind MediaKind { get; }

    public MediaContentKind ContentKind { get; }

    public bool SupportsNativeSignatureValidation { get; }

    public bool SupportsModelInput { get; }

    public FileExtension DefaultExtension { get; }

    public IReadOnlyList<FileExtension> Extensions { get; }
}
