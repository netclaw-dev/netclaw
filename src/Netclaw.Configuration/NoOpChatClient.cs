// -----------------------------------------------------------------------
// <copyright file="NoOpChatClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace Netclaw.Configuration;

/// <summary>
/// <see cref="IChatClient"/> used when no valid inference provider/model
/// configuration is present. Returns a fixed configuration banner with
/// recovery instructions and never contacts any external service or
/// emits tool calls. See <see cref="ProviderRuntimeValidation"/>.
/// </summary>
public sealed class NoOpChatClient : IChatClient
{
    /// <summary>The exact phrase the banner SHALL begin with — spec-fixed.</summary>
    public const string LeadingPhrase = "No valid model configuration detected.";

    private readonly string _banner;

    public NoOpChatClient(IReadOnlyList<string>? availableProviders = null)
    {
        _banner = BuildBanner(availableProviders);
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var message = new ChatMessage(ChatRole.Assistant, _banner);
        // No tool calls regardless of options.Tools — secure-by-default.
        return Task.FromResult(new ChatResponse(message));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Single chunk — no simulated token streaming.
        yield return new ChatResponseUpdate(ChatRole.Assistant, _banner);
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    public static string BuildBanner(IReadOnlyList<string>? availableProviders)
    {
        var sb = new StringBuilder();
        sb.Append(LeadingPhrase).Append('\n').Append('\n');
        sb.Append("Netclaw is running, but no inference provider/model is configured.\n");
        sb.Append("To get chat working:\n\n");
        sb.Append("  1. Run `netclaw doctor` to see what's missing.\n");
        if (availableProviders is { Count: > 0 })
            sb.Append("  2. Run `netclaw model` to pick one of the configured providers and a model.\n");
        else
            sb.Append("  2. Run `netclaw init` to configure both a provider and model.\n");
        sb.Append("  3. Or repair `netclaw.json` / `secrets.json` manually and restart the daemon.");

        if (availableProviders is { Count: > 0 })
        {
            sb.Append('\n').Append('\n');
            sb.Append("Available providers: ").Append(string.Join(", ", availableProviders));
        }

        return sb.ToString();
    }
}
