using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Daemon.Providers;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class OpenRouterReasoningExcludePolicyTests
{
    [Fact]
    public void InjectsReasoningExclude_IntoRequestBody()
    {
        var policy = new OpenRouterReasoningExcludePolicy();
        var body = new JsonObject
        {
            ["model"] = "anthropic/claude-sonnet-4-20250514",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" })
        };

        var result = ProcessSync(policy, body);

        Assert.NotNull(result);
        Assert.True(result!["reasoning"]?["exclude"]?.GetValue<bool>());
    }

    [Fact]
    public void PreservesExistingFields()
    {
        var policy = new OpenRouterReasoningExcludePolicy();
        var body = new JsonObject
        {
            ["model"] = "deepseek/deepseek-r1",
            ["temperature"] = 0.7,
            ["stream"] = true,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "think hard" })
        };

        var result = ProcessSync(policy, body);

        Assert.NotNull(result);
        Assert.Equal("deepseek/deepseek-r1", result!["model"]?.GetValue<string>());
        Assert.Equal(0.7, result["temperature"]?.GetValue<double>());
        Assert.True(result["stream"]?.GetValue<bool>());
        Assert.Single(result["messages"]!.AsArray());
    }

    [Fact]
    public void OverwritesExistingReasoningField()
    {
        var policy = new OpenRouterReasoningExcludePolicy();
        var body = new JsonObject
        {
            ["model"] = "test",
            ["reasoning"] = new JsonObject { ["effort"] = "high" }
        };

        var result = ProcessSync(policy, body);

        Assert.NotNull(result);
        // The policy overwrites any existing reasoning config
        Assert.True(result!["reasoning"]?["exclude"]?.GetValue<bool>());
    }

    [Fact]
    public void NoOps_WhenContentIsNull()
    {
        var policy = new OpenRouterReasoningExcludePolicy();
        // Pipeline wrapper that captures the message without content
        var pipeline = new CapturePolicy();
        var message = CreateMessage(content: null);

        policy.Process(message, [policy, pipeline], 0);

        // Should pass through without crashing
        Assert.True(pipeline.WasCalled);
        Assert.Null(message.Request.Content);
    }

    /// <summary>
    /// Runs the policy synchronously and returns the modified JSON body.
    /// </summary>
    private static JsonObject? ProcessSync(OpenRouterReasoningExcludePolicy policy, JsonObject body)
    {
        var pipeline = new CapturePolicy();
        var message = CreateMessage(body);

        policy.Process(message, [policy, pipeline], 0);

        Assert.True(pipeline.WasCalled, "Policy must call ProcessNext");

        if (message.Request.Content is null)
            return null;

        using var stream = new MemoryStream();
        message.Request.Content.WriteTo(stream, default);
        return JsonSerializer.Deserialize<JsonObject>(stream.ToArray());
    }

    private static PipelineMessage CreateMessage(JsonObject? body)
    {
        var pipeline = ClientPipeline.Create();
        var message = pipeline.CreateMessage();

        if (body is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
            message.Request.Content = BinaryContent.Create(BinaryData.FromBytes(bytes));
        }

        return message;
    }

    private static PipelineMessage CreateMessage(BinaryContent? content)
    {
        var pipeline = ClientPipeline.Create();
        var message = pipeline.CreateMessage();
        if (content is not null)
            message.Request.Content = content;
        return message;
    }

    /// <summary>
    /// Terminal policy that records it was reached.
    /// </summary>
    private sealed class CapturePolicy : PipelinePolicy
    {
        public bool WasCalled { get; private set; }

        public override void Process(
            PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            WasCalled = true;
        }

        public override ValueTask ProcessAsync(
            PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            WasCalled = true;
            return default;
        }
    }
}
