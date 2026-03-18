using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// Pipeline policy for the OpenAI Codex backend at <c>chatgpt.com/backend-api/codex</c>.
/// Injects the <c>ChatGPT-Account-Id</c> header (when available) and sets
/// <c>"store": false</c> in the request body (required by the Codex backend).
/// </summary>
internal sealed class OpenAiCodexRequestPolicy(string? accountId) : PipelinePolicy
{
    public override void Process(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Modify(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Modify(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void Modify(PipelineMessage message)
    {
        var request = message.Request;

        // Inject ChatGPT-Account-Id header (extracted from JWT by JwtAccountIdExtractor)
        if (accountId is not null)
            request.Headers.Set("ChatGPT-Account-Id", accountId);

        // Inject "store": false into the request body
        if (request.Content is null)
            return;

        using var stream = new MemoryStream();
        request.Content.WriteTo(stream, default);
        var bytes = stream.ToArray();

        var node = JsonNode.Parse(bytes);
        if (node is not JsonObject obj)
            return;

        obj["store"] = false;

        var modified = JsonSerializer.SerializeToUtf8Bytes(obj);
        request.Content = BinaryContent.Create(BinaryData.FromBytes(modified));
    }
}
