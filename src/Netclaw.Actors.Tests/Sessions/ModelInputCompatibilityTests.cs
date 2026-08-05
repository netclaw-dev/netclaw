// -----------------------------------------------------------------------
// <copyright file="ModelInputCompatibilityTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ModelInputCompatibilityTests
{
    [Fact]
    public void Compatible_modalities_pass()
    {
        var result = ModelInputCompatibility.Evaluate(
            ModelModality.Text | ModelModality.Image,
            [MessageWith(MediaModality.Image)]);

        Assert.True(result.IsCompatible);
        Assert.Equal(ModelModality.Image, result.RequiredModalities);
        Assert.Equal(ModelModality.None, result.UnsupportedModalities);
    }

    [Fact]
    public void Combined_unsupported_modalities_are_reported()
    {
        var result = ModelInputCompatibility.Evaluate(
            ModelModality.Text | ModelModality.Image,
            [MessageWith(MediaModality.Image, MediaModality.Audio, MediaModality.Video)]);

        Assert.False(result.IsCompatible);
        Assert.Equal(ModelModality.Audio | ModelModality.Video, result.UnsupportedModalities);
    }

    [Fact]
    public void Pending_media_and_history_use_one_check()
    {
        var result = ModelInputCompatibility.Evaluate(
            ModelModality.Text | ModelModality.Image,
            [MessageWith(MediaModality.Image)],
            [Media(MediaModality.Audio)]);

        Assert.False(result.IsCompatible);
        Assert.Equal(ModelModality.Image | ModelModality.Audio, result.RequiredModalities);
        Assert.Equal(ModelModality.Audio, result.UnsupportedModalities);
    }

    [Fact]
    public void Tool_message_media_is_checked()
    {
        var result = ModelInputCompatibility.Evaluate(
            ModelModality.Text,
            [new SerializableChatMessage
            {
                Role = ChatRole.Tool,
                MediaReferences = [Media(MediaModality.Image)]
            }]);

        Assert.False(result.IsCompatible);
        Assert.Equal(ModelModality.Image, result.UnsupportedModalities);
    }

    [Fact]
    public void Unknown_modality_fails_closed()
    {
        var unknown = Media(MediaModality.Image) with { Modality = 99 };

        var result = ModelInputCompatibility.Evaluate(
            ModelModality.Text | ModelModality.Image | ModelModality.Audio | ModelModality.Video,
            [new SerializableChatMessage { MediaReferences = [unknown] }]);

        Assert.False(result.IsCompatible);
        Assert.Equal([99], result.UnknownModalityValues);
    }

    [Fact]
    public void Error_message_reports_required_and_supported_modalities()
    {
        var model = new ModelCapabilities
        {
            ModelId = "text-and-image-model",
            InputModalities = ModelModality.Text | ModelModality.Image
        };
        var result = ModelInputCompatibility.Evaluate(
            model.InputModalities,
            [MessageWith(MediaModality.Image, MediaModality.Audio)]);

        var message = ModelInputCompatibility.BuildErrorMessage(model, result);

        Assert.Contains("Required modalities: Image, Audio.", message, StringComparison.Ordinal);
        Assert.Contains("Supported modalities: Text, Image.", message, StringComparison.Ordinal);
        Assert.Contains("Unsupported modalities: Audio.", message, StringComparison.Ordinal);
    }

    private static SerializableChatMessage MessageWith(params MediaModality[] modalities) => new()
    {
        MediaReferences = [.. modalities.Select(Media)]
    };

    private static SerializableMediaReference Media(MediaModality modality) => new()
    {
        RelativePath = $"{modality}.bin",
        MimeType = new Netclaw.Media.MimeType("application/octet-stream"),
        Modality = (int)modality
    };
}
