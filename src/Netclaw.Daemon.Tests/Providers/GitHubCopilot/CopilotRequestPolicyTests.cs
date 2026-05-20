// -----------------------------------------------------------------------
// <copyright file="CopilotRequestPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel;
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
    public async Task ProcessAsync_AppliesThreeCopilotHeaders()
    {
        var policy = new CopilotRequestPolicy(
            ExchangerReturning("copilot-bearer"), OAuthEntry(), new ApiKeyCredential("placeholder"));

        var clientPipeline = ClientPipeline.Create(new ClientPipelineOptions());
        using var message = clientPipeline.CreateMessage();
        message.Request.Method = "POST";
        message.Request.Uri = new Uri("https://api.githubcopilot.com/chat/completions");

        var captured = false;
        var terminal = new TerminalCapturingPolicy(() => captured = true);
        IReadOnlyList<PipelinePolicy> pipeline = [policy, terminal];

        await policy.ProcessAsync(message, pipeline, 0);

        Assert.True(captured);

        // The policy does NOT set Authorization — the SDK's credential auth
        // policy owns that header (see CopilotRequestPolicy remarks). The policy
        // sets only the three Copilot-required custom headers.
        message.Request.Headers.TryGetValue("copilot-integration-id", out var integrationId);
        Assert.Equal("vscode-chat", integrationId);

        message.Request.Headers.TryGetValue("editor-version", out var editorVersion);
        Assert.False(string.IsNullOrWhiteSpace(editorVersion),
            "editor-version header must be present");

        message.Request.Headers.TryGetValue("openai-intent", out var intent);
        Assert.Equal("conversation-agent", intent);
    }

    [Fact]
    public async Task ProcessAsync_UpdatesCredentialWithExchangedToken()
    {
        // The policy cannot win a header race against the SDK's own credential
        // auth policy, so instead it feeds the fresh Copilot token into the
        // shared credential that policy reads downstream. Updating the
        // credential is what makes the real token reach the wire (verified
        // end-to-end in GitHubCopilotProviderPluginTests).
        var credential = new ApiKeyCredential("placeholder");
        var policy = new CopilotRequestPolicy(
            ExchangerReturning("copilot-real"), OAuthEntry(), credential);

        var clientPipeline = ClientPipeline.Create(new ClientPipelineOptions());
        using var message = clientPipeline.CreateMessage();
        message.Request.Method = "POST";
        message.Request.Uri = new Uri("https://api.githubcopilot.com/chat/completions");

        IReadOnlyList<PipelinePolicy> pipeline = [policy, new TerminalCapturingPolicy(() => { })];

        await policy.ProcessAsync(message, pipeline, 0);

        credential.Deconstruct(out var key);
        Assert.Equal("copilot-real", key);
    }

    [Fact]
    public void Process_Synchronous_ThrowsNotSupported()
    {
        // The sync pipeline path would require blocking on async token exchange.
        // The OpenAI SDK uses the async pipeline for chat completions; the sync
        // overload is only hit by misconfigured callers and we fail loudly.
        var policy = new CopilotRequestPolicy(
            ExchangerReturning("copilot-bearer"), OAuthEntry(), new ApiKeyCredential("placeholder"));

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
