using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Providers.OpenAi;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class OpenAiCodexRequestPolicyTests
{
    [Fact]
    public void InjectsAccountHeader_AndCodexRequiredBodyFields()
    {
        var policy = new OpenAiCodexRequestPolicy("org-123");
        var body = new JsonObject
        {
            ["input"] = new JsonArray(
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "input_text", ["text"] = "hello" })
                })
        };

        var (message, result) = ProcessSync(policy, body);

        Assert.True(message.Request.Headers.TryGetValue("ChatGPT-Account-Id", out var accountId));
        Assert.Equal("org-123", accountId);
        Assert.NotNull(result);
        Assert.False(result!["store"]!.GetValue<bool>());
        Assert.Equal(string.Empty, result["instructions"]!.GetValue<string>());
        Assert.Single(result["input"]!.AsArray());
    }

    [Fact]
    public void MovesSystemMessages_IntoInstructions_AndStripsNullStrict()
    {
        var policy = new OpenAiCodexRequestPolicy(accountId: null);
        var body = new JsonObject
        {
            ["instructions"] = "existing",
            ["input"] = new JsonArray(
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "input_text", ["text"] = "first system" })
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "input_text", ["text"] = "hello" })
                },
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "input_text", ["text"] = "second system" })
                }),
            ["tools"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "function",
                    ["strict"] = null,
                    ["name"] = "tool-a"
                },
                new JsonObject
                {
                    ["type"] = "function",
                    ["strict"] = true,
                    ["name"] = "tool-b"
                })
        };

        var (_, result) = ProcessSync(policy, body);

        Assert.NotNull(result);
        Assert.Equal("existing\nfirst system\nsecond system", result!["instructions"]!.GetValue<string>());

        var input = result["input"]!.AsArray();
        Assert.Single(input);
        Assert.Equal("user", input[0]!["role"]!.GetValue<string>());

        var tools = result["tools"]!.AsArray();
        Assert.False(tools[0]!.AsObject().ContainsKey("strict"));
        Assert.True(tools[1]!["strict"]!.GetValue<bool>());
    }

    private static (PipelineMessage Message, JsonObject? Body) ProcessSync(OpenAiCodexRequestPolicy policy, JsonObject body)
    {
        var pipeline = new CapturePolicy();
        var message = CreateMessage(body);

        policy.Process(message, [policy, pipeline], 0);

        Assert.True(pipeline.WasCalled, "Policy must call ProcessNext");

        if (message.Request.Content is null)
            return (message, null);

        using var stream = new MemoryStream();
        message.Request.Content.WriteTo(stream, default);
        return (message, JsonSerializer.Deserialize<JsonObject>(stream.ToArray()));
    }

    private static PipelineMessage CreateMessage(JsonObject body)
    {
        var pipeline = ClientPipeline.Create();
        var message = pipeline.CreateMessage();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        message.Request.Content = BinaryContent.Create(BinaryData.FromBytes(bytes));
        return message;
    }

    private sealed class CapturePolicy : PipelinePolicy
    {
        public bool WasCalled { get; private set; }

        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            WasCalled = true;
        }

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            WasCalled = true;
            return default;
        }
    }
}
