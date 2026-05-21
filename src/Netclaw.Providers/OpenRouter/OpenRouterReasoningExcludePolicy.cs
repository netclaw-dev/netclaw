// -----------------------------------------------------------------------
// <copyright file="OpenRouterReasoningExcludePolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;

namespace Netclaw.Providers.OpenRouter;

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
        PipelineRequestBodyEditor.EditJsonBody(message, InjectReasoningExclude);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        PipelineRequestBodyEditor.EditJsonBody(message, InjectReasoningExclude);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private static void InjectReasoningExclude(JsonObject body)
        => body["reasoning"] = new JsonObject { ["exclude"] = true };
}
