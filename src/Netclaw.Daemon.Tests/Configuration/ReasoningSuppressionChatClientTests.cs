// -----------------------------------------------------------------------
// <copyright file="ReasoningSuppressionChatClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// Covers <c>ReasoningSuppressionChatClient</c> — the decorator that maps the
/// <see cref="NetclawChatOptionKeys.SuppressReasoning"/> intent key to the wire dialect a
/// provider plugin declares (<see cref="ReasoningSuppressionDialect"/>), or strips it with no
/// replacement. <see cref="FakeChatClient"/> (defined in RetryingChatClientTests.cs) captures
/// exactly the <see cref="ChatOptions"/> that reaches the inner client.
/// </summary>
public sealed class ReasoningSuppressionChatClientTests
{
    [Theory]
    [InlineData(ReasoningSuppressionDialect.None)]
    [InlineData(ReasoningSuppressionDialect.ChatTemplateKwargs)]
    [InlineData(ReasoningSuppressionDialect.OllamaThink)]
    public async Task IntentKey_IsAlwaysRemoved_RegardlessOfDialect(ReasoningSuppressionDialect dialect)
    {
        ChatOptions? forwarded = null;
        var fake = new FakeChatClient((_, opts, _) =>
        {
            forwarded = opts;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        });
        var client = new ReasoningSuppressionChatClient(fake, dialect);
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [NetclawChatOptionKeys.SuppressReasoning] = true
            }
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], options,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(forwarded);
        Assert.False(
            forwarded!.AdditionalProperties?.ContainsKey(NetclawChatOptionKeys.SuppressReasoning) ?? false,
            "intent key must never reach the inner client");
    }

    [Fact]
    public async Task ChatTemplateKwargsDialect_EmitsMappedEntry()
    {
        ChatOptions? forwarded = null;
        var fake = new FakeChatClient((_, opts, _) =>
        {
            forwarded = opts;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        });
        var client = new ReasoningSuppressionChatClient(fake, ReasoningSuppressionDialect.ChatTemplateKwargs);
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [NetclawChatOptionKeys.SuppressReasoning] = true
            }
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], options,
            cancellationToken: TestContext.Current.CancellationToken);

        var kwargs = Assert.IsType<Dictionary<string, object?>>(
            forwarded!.AdditionalProperties!["chat_template_kwargs"]);
        Assert.Equal(false, kwargs["enable_thinking"]);
    }

    [Fact]
    public async Task OllamaThinkDialect_EmitsThinkFalse()
    {
        ChatOptions? forwarded = null;
        var fake = new FakeChatClient((_, opts, _) =>
        {
            forwarded = opts;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        });
        var client = new ReasoningSuppressionChatClient(fake, ReasoningSuppressionDialect.OllamaThink);
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [NetclawChatOptionKeys.SuppressReasoning] = true
            }
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], options,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(false, forwarded!.AdditionalProperties?["think"]);
    }

    [Fact]
    public async Task NoneDialect_LeavesNoTrace()
    {
        ChatOptions? forwarded = null;
        var fake = new FakeChatClient((_, opts, _) =>
        {
            forwarded = opts;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        });
        var client = new ReasoningSuppressionChatClient(fake, ReasoningSuppressionDialect.None);
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [NetclawChatOptionKeys.SuppressReasoning] = true
            }
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], options,
            cancellationToken: TestContext.Current.CancellationToken);

        // The intent key was the only entry — stripping it with no dialect to emit
        // should leave an empty (not null) AdditionalProperties dictionary.
        Assert.Empty(forwarded!.AdditionalProperties!);
    }

    [Fact]
    public async Task SuppressReasoning_False_StripsButDoesNotApplyDialect()
    {
        ChatOptions? forwarded = null;
        var fake = new FakeChatClient((_, opts, _) =>
        {
            forwarded = opts;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        });
        var client = new ReasoningSuppressionChatClient(fake, ReasoningSuppressionDialect.ChatTemplateKwargs);
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [NetclawChatOptionKeys.SuppressReasoning] = false
            }
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], options,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(forwarded!.AdditionalProperties!.ContainsKey(NetclawChatOptionKeys.SuppressReasoning));
        Assert.False(forwarded.AdditionalProperties.ContainsKey("chat_template_kwargs"));
    }

    [Fact]
    public async Task OptionsWithoutIntentKey_PassThroughUntouched()
    {
        ChatOptions? forwarded = null;
        var fake = new FakeChatClient((_, opts, _) =>
        {
            forwarded = opts;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        });
        var client = new ReasoningSuppressionChatClient(fake, ReasoningSuppressionDialect.ChatTemplateKwargs);
        var options = new ChatOptions
        {
            Temperature = 0.5f,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["unrelated"] = "value"
            }
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], options,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(options, forwarded);
        Assert.Equal(0.5f, forwarded!.Temperature);
        var properties = forwarded.AdditionalProperties;
        Assert.NotNull(properties);
        Assert.Single(properties);
        Assert.Equal("value", properties["unrelated"]);
        Assert.False(properties.ContainsKey("chat_template_kwargs"));
    }

    [Fact]
    public async Task NullOptions_DoesNotThrow()
    {
        var fake = new FakeChatClient();
        var client = new ReasoningSuppressionChatClient(fake, ReasoningSuppressionDialect.ChatTemplateKwargs);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(response);
    }

    [Fact]
    public async Task Streaming_MapsDialectSameAsNonStreaming()
    {
        ChatOptions? forwarded = null;
        var fake = new FakeChatClient(streamHandler: (_, opts, _) =>
        {
            forwarded = opts;
            return StreamOne();
        });
        var client = new ReasoningSuppressionChatClient(fake, ReasoningSuppressionDialect.OllamaThink);
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [NetclawChatOptionKeys.SuppressReasoning] = true
            }
        };

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], options,
            cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        Assert.False(forwarded!.AdditionalProperties!.ContainsKey(NetclawChatOptionKeys.SuppressReasoning));
        Assert.Equal(false, forwarded.AdditionalProperties["think"]);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamOne()
    {
        await Task.CompletedTask;
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] };
    }
}
