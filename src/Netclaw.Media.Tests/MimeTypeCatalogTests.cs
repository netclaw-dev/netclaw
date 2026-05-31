// -----------------------------------------------------------------------
// <copyright file="MimeTypeCatalogTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Media.Tests;

public sealed class MimeTypeCatalogTests
{
    [Fact]
    public void Default_value_objects_are_null_safe()
    {
        // default(struct) bypasses the constructor; .Value must not be null,
        // and catalog lookups on a default value must not throw.
        Assert.Equal(MimeType.DefaultValue, default(MimeType).Value);
        Assert.Equal(MimeType.DefaultValue, default(DeclaredMimeType).Value);
        Assert.Equal(MimeType.DefaultValue, default(VerifiedMimeType).Value);
        Assert.Equal(string.Empty, default(FileExtension).Value);
        Assert.True(default(FileExtension).IsEmpty);

        // No throw — this is the footgun being guarded against.
        Assert.Equal(AttachmentCategory.Other, MimeTypeCatalog.GetCategory(default(MimeType)));
    }

    [Theory]
    [InlineData("image/jpg", MimeTypeCatalog.ImageJpeg)]
    [InlineData("IMAGE/JPG; charset=binary", MimeTypeCatalog.ImageJpeg)]
    [InlineData("application/x-zip-compressed", MimeTypeCatalog.ApplicationZip)]
    [InlineData("text/xml", MimeTypeCatalog.ApplicationXml)]
    [InlineData("audio/x-wav", MimeTypeCatalog.AudioWav)]
    public void Normalize_resolves_known_aliases(string raw, string expected)
    {
        Assert.Equal(expected, MimeTypeCatalog.Normalize(raw));
    }

    [Theory]
    [InlineData("image/png", AttachmentCategory.Image)]
    [InlineData("image/bmp", AttachmentCategory.Image)]
    [InlineData("image/tiff", AttachmentCategory.Image)]
    [InlineData("image/x-unknown", AttachmentCategory.Other)]
    [InlineData("video/mp4", AttachmentCategory.Media)]
    [InlineData("video/x-unknown", AttachmentCategory.Other)]
    [InlineData("audio/mpeg", AttachmentCategory.Media)]
    [InlineData("audio/x-unknown", AttachmentCategory.Other)]
    [InlineData("application/pdf", AttachmentCategory.Pdf)]
    [InlineData("application/json", AttachmentCategory.Document)]
    public void GetCategory_is_catalog_backed_not_prefix_backed(string mimeType, AttachmentCategory expected)
    {
        Assert.Equal(expected, MimeTypeCatalog.GetCategory(mimeType));
    }

    [Theory]
    [InlineData(".png", MimeTypeCatalog.ImagePng)]
    [InlineData("jpg", MimeTypeCatalog.ImageJpeg)]
    [InlineData(".pdf", MimeTypeCatalog.ApplicationPdf)]
    [InlineData(".mp4", MimeTypeCatalog.VideoMp4)]
    [InlineData(".m4a", MimeTypeCatalog.AudioMp4)]
    public void FromExtension_returns_canonical_mime(string extension, string expected)
    {
        Assert.Equal(new MimeType(expected), MimeTypeCatalog.FromExtension(extension));
    }

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("text/plain", true)]
    [InlineData("image/png", false)]
    [InlineData("application/octet-stream", false)]
    public void IsText_uses_catalog_metadata(string mimeType, bool expected)
    {
        Assert.Equal(expected, MimeTypeCatalog.IsText(mimeType));
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("image/bmp", false)]
    [InlineData("image/tiff", false)]
    [InlineData("audio/mpeg", false)]
    [InlineData("application/pdf", false)]
    public void IsModelInputSupported_only_allows_explicit_images(string mimeType, bool expected)
    {
        Assert.Equal(expected, MimeTypeCatalog.IsModelInputSupported(mimeType));
    }

    [Fact]
    public void NormalizeDeclaredForExtension_accepts_octet_stream_when_extension_is_known()
    {
        var normalized = MimeTypeCatalog.NormalizeDeclaredForExtension("application/octet-stream", ".png");

        Assert.Equal(new MimeType(MimeTypeCatalog.ImagePng), normalized);
    }

    [Theory]
    [InlineData(".html", MimeTypeCatalog.TextHtml)]
    [InlineData(".htm", MimeTypeCatalog.TextHtml)]
    [InlineData(".md", MimeTypeCatalog.TextMarkdown)]
    [InlineData(".json", MimeTypeCatalog.ApplicationJson)]
    public void NormalizeDeclaredForExtension_promotes_text_plain_by_extension(string extension, string expected)
    {
        var normalized = MimeTypeCatalog.NormalizeDeclaredForExtension("text/plain", extension);

        Assert.Equal(new MimeType(expected), normalized);
    }
}
