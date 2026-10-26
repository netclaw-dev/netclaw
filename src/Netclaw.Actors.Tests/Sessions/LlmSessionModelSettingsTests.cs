// -----------------------------------------------------------------------
// <copyright file="LlmSessionModelSettingsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Providers.SelfHosted;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// One session turn through the real <see cref="ProviderPluginFactory"/> stack (task 2.8): the Main
/// definition's output limit and thinking level reach the request body of an OpenAI-compatible endpoint
/// with the ReasoningEffort dialect. The capture handler answers with a short SSE stream.
/// </summary>
public sealed class LlmSessionModelSettingsTests : LlmSessionTestBase
{
    private const string UserText = "hello model settings";

    private const string SseReply =
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"model\":\"qwen3\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":null}]}\n\n"
        + "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"model\":\"qwen3\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";

    private readonly List<string> _bodies = [];

    public LlmSessionModelSettingsTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            lock (_bodies)
                _bodies.Add(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SseReply, Encoding.UTF8, "text/event-stream"),
            };
        });
        var entry = new ProviderEntry { Type = "openai-compatible", Endpoint = "http://wire.test/v1" };
        entry.SetVendorOptions(new JsonObject { ["ThinkingDialect"] = "ReasoningEffort" });
        var plugin = new OpenAiCompatibleProviderPlugin(
            new OpenAiCompatibleDescriptor(new HttpClient()), NullLoggerFactory.Instance)
        {
            HttpHandlerOverride = handler,
        };
        var factory = new ProviderPluginFactory(
            new Dictionary<string, ProviderEntry> { ["local-vllm"] = entry },
            [plugin],
            new FakeCapabilityResolver(),
            NullLoggerFactory.Instance,
            TimeProvider.System);
        var main = factory.Create(new ModelReference
        {
            Provider = "local-vllm",
            ModelId = "qwen3",
            ContextWindow = 131_072,
            MaxOutputTokens = 32_000,
            Thinking = ThinkingLevel.Off,
        });

        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(main));
        services.AddSingleton(new ModelCapabilities { ModelId = "qwen3", ContextWindowTokens = 131_072 });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning { SnapshotInterval = 5, TitleGenerationInterval = 0 },
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        services.AddSingleton<IToolExecutor>(new FakeToolExecutor());
        services.AddSingleton(new ToolRegistry());
    }

    [Fact]
    public async Task Session_turn_request_carries_mapped_model_settings()
    {
        var sessionId = new SessionId("test-channel/model-settings");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("model-settings-sub");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full,
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = UserText,
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync(
            message => message is TurnCompleted,
            max: TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        string turnBody;
        lock (_bodies)
            turnBody = Assert.Single(_bodies, body => body.Contains(UserText, StringComparison.Ordinal));
        using var request = JsonDocument.Parse(turnBody);
        var root = request.RootElement;
        Assert.True(root.TryGetProperty("max_tokens", out var maxTokens), "the request carries no max_tokens");
        Assert.Equal(32_000, maxTokens.GetInt32());
        Assert.True(root.TryGetProperty("reasoning_effort", out var effort), "the request carries no reasoning_effort");
        Assert.Equal("none", effort.GetString());
    }
}
