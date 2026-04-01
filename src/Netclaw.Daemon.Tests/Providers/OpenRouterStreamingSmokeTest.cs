using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Providers.OpenRouter;
using OpenAI;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

/// <summary>
/// Live smoke test for OpenRouter streaming with the reasoning exclude policy.
/// Requires OPENROUTER_API_KEY env var. Skipped in CI.
/// </summary>
public sealed class OpenRouterStreamingSmokeTest
{
    private readonly ITestOutputHelper _output;

    public OpenRouterStreamingSmokeTest(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public async Task StreamingWorksWithReasoningExcludePolicy()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
            return; // No API key — skip in CI

        var endpoint = new Uri("https://openrouter.ai/api/v1");
        var model = "qwen/qwen3.5-35b-a3b";

        var options = new OpenAIClientOptions { Endpoint = endpoint };
        options.AddPolicy(new OpenRouterReasoningExcludePolicy(), PipelinePosition.PerCall);
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);
        var chatClient = client.GetChatClient(model).AsIChatClient();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Say hello in one sentence.")
        };

        var fullText = new System.Text.StringBuilder();

        await foreach (var update in chatClient.GetStreamingResponseAsync(messages))
        {
            foreach (var content in update.Contents)
            {
                if (content is TextContent text)
                {
                    fullText.Append(text.Text);
                    _output.WriteLine($"[delta] {text.Text}");
                }
            }
        }

        _output.WriteLine($"\n[full] {fullText}");
        Assert.NotEmpty(fullText.ToString());
    }
}
