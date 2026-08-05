// -----------------------------------------------------------------------
// <copyright file="ModelInputCompatibility.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Actors.Sessions;

internal sealed record ModelInputCompatibilityResult(
    ModelModality RequiredModalities,
    ModelModality UnsupportedModalities,
    IReadOnlyList<int> UnknownModalityValues)
{
    public bool IsCompatible => UnsupportedModalities == ModelModality.None
        && UnknownModalityValues.Count == 0;
}

internal static class ModelInputCompatibility
{
    public static ModelInputCompatibilityResult Evaluate(
        ModelModality supportedModalities,
        IEnumerable<SerializableChatMessage> history,
        IEnumerable<SerializableMediaReference>? pendingMedia = null)
    {
        var required = ModelModality.None;
        var unknown = new HashSet<int>();

        foreach (var message in history)
            AddRequirements(message.MediaReferences, ref required, unknown);

        if (pendingMedia is not null)
            AddRequirements(pendingMedia, ref required, unknown);

        return new ModelInputCompatibilityResult(
            required,
            required & ~supportedModalities,
            unknown.Order().ToArray());
    }

    public static string BuildErrorMessage(
        ModelCapabilities model,
        ModelInputCompatibilityResult result)
    {
        var required = result.RequiredModalities == ModelModality.None
            ? "none"
            : result.RequiredModalities.ToString();
        var supported = model.InputModalities == ModelModality.None
            ? "none"
            : model.InputModalities.ToString();
        var unsupported = result.UnsupportedModalities == ModelModality.None
            ? "none"
            : result.UnsupportedModalities.ToString();
        var unknown = result.UnknownModalityValues.Count == 0
            ? "none"
            : string.Join(", ", result.UnknownModalityValues);

        return $"The session input is not compatible with model '{model.ModelId}'. "
            + $"Required modalities: {required}. Supported modalities: {supported}. "
            + $"Unsupported modalities: {unsupported}. Unknown modality values: {unknown}. "
            + "Select a model that supports this input, or start a new conversation.";
    }

    private static void AddRequirements(
        IEnumerable<SerializableMediaReference> media,
        ref ModelModality required,
        HashSet<int> unknown)
    {
        foreach (var reference in media)
        {
            switch ((MediaModality)reference.Modality)
            {
                case MediaModality.Image:
                    required |= ModelModality.Image;
                    break;
                case MediaModality.Audio:
                    required |= ModelModality.Audio;
                    break;
                case MediaModality.Video:
                    required |= ModelModality.Video;
                    break;
                default:
                    unknown.Add(reference.Modality);
                    break;
            }
        }
    }
}
