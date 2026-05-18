// -----------------------------------------------------------------------
// <copyright file="CopilotRequestPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel.Primitives;
using Netclaw.Configuration;

namespace Netclaw.Providers.GitHubCopilot;

/// <summary>
/// Pipeline policy for <c>api.githubcopilot.com</c>. On every outbound
/// request it resolves a fresh Copilot API token via
/// <see cref="CopilotTokenExchanger"/> (cached, with a 2-minute refresh
/// buffer) and writes the Authorization plus the three custom headers the
/// Copilot API rejects requests without.
/// </summary>
internal sealed class CopilotRequestPolicy(CopilotTokenExchanger exchanger, ProviderEntry entry)
    : PipelinePolicy
{
    public override void Process(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        var token = exchanger.GetTokenAsync(entry, message.CancellationToken)
            .GetAwaiter().GetResult();
        Apply(message, token);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        var token = await exchanger.GetTokenAsync(entry, message.CancellationToken);
        Apply(message, token);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private static void Apply(PipelineMessage message, string copilotToken)
    {
        var headers = message.Request.Headers;
        headers.Set("Authorization", $"Bearer {copilotToken}");

        // The Copilot API validates copilot-integration-id against a closed
        // set and rejects unknown values. "vscode-chat" is the closest
        // registered identifier we can use until we register a Netclaw
        // integration with GitHub.
        headers.Set("copilot-integration-id", "vscode-chat");
        headers.Set("editor-version", "Netclaw/1.0");
        headers.Set("openai-intent", "conversation-agent");
    }
}
