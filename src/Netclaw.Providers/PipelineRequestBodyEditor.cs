// -----------------------------------------------------------------------
// <copyright file="PipelineRequestBodyEditor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Netclaw.Providers;

/// <summary>
/// Shared helper for <see cref="PipelinePolicy"/> implementations that need to
/// parse the outbound JSON request body, mutate it, and write it back as
/// <see cref="BinaryContent"/>. Encapsulates the
/// <see cref="MemoryStream"/>/<see cref="JsonNode"/>/<see cref="JsonSerializer"/>
/// dance so per-provider policies can express the mutation as a single
/// <see cref="Action{JsonObject}"/> lambda.
/// </summary>
internal static class PipelineRequestBodyEditor
{
    /// <summary>
    /// Parses <paramref name="message"/>.Request.Content as a JSON object,
    /// invokes <paramref name="edit"/> on the parsed object, then writes the
    /// modified JSON back as the request content.
    /// </summary>
    /// <remarks>
    /// No-ops in two cases — both deliberate, both also no-op in the policies
    /// that previously inlined this pattern: the request has no content (e.g.
    /// the SDK sent a header-only call), or the content is JSON but not a JSON
    /// object (e.g. a top-level array). Callers needing array-rooted edits
    /// should not use this helper.
    /// </remarks>
    public static void EditJsonBody(PipelineMessage message, Action<JsonObject> edit)
    {
        var request = message.Request;
        if (request.Content is null)
            return;

        using var stream = new MemoryStream();
        request.Content.WriteTo(stream, default);
        var bytes = stream.ToArray();

        var node = JsonNode.Parse(bytes);
        if (node is not JsonObject obj)
            return;

        edit(obj);

        var modified = JsonSerializer.SerializeToUtf8Bytes(obj);
        request.Content = BinaryContent.Create(BinaryData.FromBytes(modified));
    }
}
