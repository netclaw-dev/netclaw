// -----------------------------------------------------------------------
// <copyright file="CopilotRequestPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Netclaw.Configuration;
using Netclaw.Providers.GitHubCopilot;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers.GitHubCopilot;

public sealed class CopilotRequestPolicyTests
{
    private static ProviderEntry OAuthEntry(string token = "oauth-1") =>
        new()
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString(token),
        };

    private static CopilotTokenExchanger ExchangerReturning(string copilotToken)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    token = copilotToken,
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
                }),
                Encoding.UTF8,
                "application/json"),
        });
        return new CopilotTokenExchanger(new HttpClient(handler));
    }

    [Fact]
    public async Task ProcessAsync_AppliesAllFourRequiredHeaders()
    {
        var policy = new CopilotRequestPolicy(
            ExchangerReturning("copilot-bearer"), OAuthEntry());

        var clientPipeline = ClientPipeline.Create(new ClientPipelineOptions());
        using var message = clientPipeline.CreateMessage();
        message.Request.Method = "POST";
        message.Request.Uri = new Uri("https://api.githubcopilot.com/chat/completions");

        var captured = false;
        var terminal = new TerminalCapturingPolicy(() => captured = true);
        IReadOnlyList<PipelinePolicy> pipeline = [policy, terminal];

        await policy.ProcessAsync(message, pipeline, 0);

        Assert.True(captured);

        message.Request.Headers.TryGetValue("Authorization", out var auth);
        Assert.Equal("Bearer copilot-bearer", auth);

        message.Request.Headers.TryGetValue("copilot-integration-id", out var integrationId);
        Assert.Equal("vscode-chat", integrationId);

        message.Request.Headers.TryGetValue("editor-version", out var editorVersion);
        Assert.False(string.IsNullOrWhiteSpace(editorVersion),
            "editor-version header must be present");

        message.Request.Headers.TryGetValue("openai-intent", out var intent);
        Assert.Equal("conversation-agent", intent);
    }

    [Fact]
    public async Task ProcessAsync_OverwritesPreviousAuthorizationHeader()
    {
        // The OpenAI SDK populates Authorization from the placeholder ApiKeyCredential
        // we pass in. The policy must overwrite it on every call with the real
        // short-lived Copilot token; a stale placeholder bearer is a 401 in production.
        var policy = new CopilotRequestPolicy(
            ExchangerReturning("copilot-real"), OAuthEntry());

        var clientPipeline = ClientPipeline.Create(new ClientPipelineOptions());
        using var message = clientPipeline.CreateMessage();
        message.Request.Method = "POST";
        message.Request.Uri = new Uri("https://api.githubcopilot.com/chat/completions");
        message.Request.Headers.Set("Authorization", "Bearer placeholder");

        IReadOnlyList<PipelinePolicy> pipeline = [policy, new TerminalCapturingPolicy(() => { })];

        await policy.ProcessAsync(message, pipeline, 0);

        message.Request.Headers.TryGetValue("Authorization", out var auth);
        Assert.Equal("Bearer copilot-real", auth);
    }

    [Fact]
    public void Process_Synchronous_ThrowsNotSupported()
    {
        // The sync pipeline path would require blocking on async token exchange.
        // The OpenAI SDK uses the async pipeline for chat completions; the sync
        // overload is only hit by misconfigured callers and we fail loudly.
        var policy = new CopilotRequestPolicy(
            ExchangerReturning("copilot-bearer"), OAuthEntry());

        var clientPipeline = ClientPipeline.Create(new ClientPipelineOptions());
        using var message = clientPipeline.CreateMessage();
        message.Request.Method = "POST";
        message.Request.Uri = new Uri("https://api.githubcopilot.com/chat/completions");

        IReadOnlyList<PipelinePolicy> pipeline = [policy, new TerminalCapturingPolicy(() => { })];

        var ex = Assert.Throws<NotSupportedException>(() =>
            policy.Process(message, pipeline, 0));
        Assert.Contains("async", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// No-op terminal policy that records whether the previous policy invoked
    /// the chain. Used so ProcessNextAsync has somewhere to land without
    /// pulling in a real HTTP transport.
    /// </summary>
    private sealed class TerminalCapturingPolicy(Action onInvoke) : PipelinePolicy
    {
        public override void Process(
            PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            onInvoke();
        }

        public override ValueTask ProcessAsync(
            PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            onInvoke();
            return ValueTask.CompletedTask;
        }
    }
}
