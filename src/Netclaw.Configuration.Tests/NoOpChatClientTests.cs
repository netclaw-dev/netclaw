// -----------------------------------------------------------------------
// <copyright file="NoOpChatClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class NoOpChatClientTests
{
    [Fact]
    public async Task GetResponseAsync_LeadsWithFixedPhrase()
    {
        var client = new NoOpChatClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            cancellationToken: ct);

        var text = response.Text;
        Assert.StartsWith(NoOpChatClient.LeadingPhrase, text);
    }

    [Fact]
    public async Task GetResponseAsync_IncludesAllThreeRecoverySteps()
    {
        var client = new NoOpChatClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            cancellationToken: ct);

        var text = response.Text;
        Assert.Contains("netclaw doctor", text);
        Assert.Contains("netclaw init", text);
        Assert.Contains("netclaw.json", text);
    }

    [Fact]
    public async Task GetResponseAsync_AppendsAvailableProvidersLine_WhenProvided()
    {
        var client = new NoOpChatClient(new[] { "openrouter", "anthropic" });
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            cancellationToken: ct);

        Assert.Contains("Available providers:", response.Text);
        Assert.Contains("openrouter", response.Text);
        Assert.Contains("anthropic", response.Text);
        Assert.Contains("netclaw model", response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_OmitsAvailableProvidersLine_WhenEmpty()
    {
        var client = new NoOpChatClient(Array.Empty<string>());
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            cancellationToken: ct);

        Assert.DoesNotContain("Available providers:", response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_ContainsNoToolCalls_EvenWhenToolsRegistered()
    {
        var client = new NoOpChatClient();
        var ct = TestContext.Current.CancellationToken;

        var options = new ChatOptions
        {
            Tools = new List<AITool>
            {
                AIFunctionFactory.Create(() => "ignored", "noop_tool", "should not be called"),
            },
        };

        var response = await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "use the tool") },
            options,
            cancellationToken: ct);

        var toolCalls = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .ToList();

        Assert.Empty(toolCalls);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_EmitsSingleChunk()
    {
        var client = new NoOpChatClient();
        var ct = TestContext.Current.CancellationToken;

        var chunks = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           new[] { new ChatMessage(ChatRole.User, "hi") },
                           cancellationToken: ct))
        {
            chunks.Add(update);
        }

        Assert.Single(chunks);
        Assert.Equal(ChatRole.Assistant, chunks[0].Role);
        Assert.Contains(NoOpChatClient.LeadingPhrase, chunks[0].Text);
    }
}
