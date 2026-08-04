// -----------------------------------------------------------------------
// <copyright file="AttachmentInlineDecisionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Media;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class AttachmentInlineDecisionTests
{
    [Theory]
    [InlineData(ModelModality.Text | ModelModality.Image, false, ImageInputRoute.Direct)]
    [InlineData(ModelModality.Text | ModelModality.Image, true, ImageInputRoute.Direct)]
    [InlineData(ModelModality.Text, true, ImageInputRoute.Proxy)]
    [InlineData(ModelModality.Text, false, ImageInputRoute.None)]
    public void SelectImageRoute_uses_main_capability_before_proxy(
        ModelModality inputModalities,
        bool imageProxyEnabled,
        ImageInputRoute expected)
    {
        Assert.Equal(expected, AttachmentInlineDecision.SelectImageRoute(
            inputModalities,
            imageProxyEnabled));
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    public void Model_input_image_types_inline_when_model_accepts_images(string mimeType)
    {
        var (inlined, note) = AttachmentInlineDecision.Resolve(
            new MimeType(mimeType), AttachmentCategory.Image, inlineImages: true);

        Assert.True(inlined);
        Assert.Null(note);
    }

    [Theory]
    [InlineData("image/bmp")]
    [InlineData("image/tiff")]
    public void Image_types_the_provider_cannot_ingest_are_path_only(string mimeType)
    {
        // bmp/tiff are accepted as images but must NOT be inlined as DataContent,
        // or they would hit the image-only provider serialization guardrail.
        var (inlined, note) = AttachmentInlineDecision.Resolve(
            new MimeType(mimeType), AttachmentCategory.Image, inlineImages: true);

        Assert.False(inlined);
        Assert.NotNull(note);
    }

    [Fact]
    public void Images_are_path_only_when_model_lacks_image_modality()
    {
        var (inlined, note) = AttachmentInlineDecision.Resolve(
            new MimeType("image/png"), AttachmentCategory.Image, inlineImages: false);

        Assert.False(inlined);
        Assert.NotNull(note);
    }

    [Fact]
    public void Proxy_route_accepts_supported_image_types()
    {
        var (inlined, note) = AttachmentInlineDecision.Resolve(
            new MimeType("image/png"),
            AttachmentCategory.Image,
            ImageInputRoute.Proxy);

        Assert.True(inlined);
        Assert.Null(note);
    }

    [Fact]
    public async Task Proxy_projection_marks_the_canonical_attachment_line()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);

            var projection = await AttachmentIngressFormatting.BuildAcceptedProjectionAsync(
                path,
                "photo.png",
                "image/png",
                AttachmentCategory.Image,
                ImageInputRoute.Proxy,
                3,
                TestContext.Current.CancellationToken);

            Assert.True(projection.Inlined);
            Assert.NotNull(projection.InlineContent);
            Assert.Contains("inlined=\"true\" via=\"image-proxy\"", projection.Line, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
