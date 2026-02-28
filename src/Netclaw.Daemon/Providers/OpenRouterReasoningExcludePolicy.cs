using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Netclaw.Daemon.Providers;

/// <summary>
/// Pipeline policy that injects <c>"reasoning": {"exclude": true}</c> into
/// outbound OpenRouter requests. Without this, OpenRouter may include
/// <c>reasoning</c> / <c>reasoning_details</c> fields in SSE chunks that
/// the OpenAI .NET SDK cannot deserialize, causing LlmCallFailed errors.
/// </summary>
internal sealed class OpenRouterReasoningExcludePolicy : PipelinePolicy
{
    public override void Process(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        InjectReasoningExclude(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        InjectReasoningExclude(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private static void InjectReasoningExclude(PipelineMessage message)
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

        obj["reasoning"] = new JsonObject
        {
            ["exclude"] = true
        };

        var modified = JsonSerializer.SerializeToUtf8Bytes(obj);
        request.Content = BinaryContent.Create(BinaryData.FromBytes(modified));
    }
}
