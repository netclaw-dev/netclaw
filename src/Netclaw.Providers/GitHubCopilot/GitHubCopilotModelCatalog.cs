// -----------------------------------------------------------------------
// <copyright file="GitHubCopilotModelCatalog.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Netclaw.Providers.GitHubCopilot;

internal enum GitHubCopilotApiKind
{
    Responses,
    ChatCompletions,
}

/// <summary>Provider-local endpoint capability advertised by GitHub Copilot /models.</summary>
internal sealed record GitHubCopilotModelCapability(
    string ModelId,
    bool SupportsResponses,
    bool SupportsChatCompletions,
    IReadOnlyList<string> SupportedEndpoints)
{
    public GitHubCopilotApiKind? PreferredApi => SupportsResponses
        ? GitHubCopilotApiKind.Responses
        : SupportsChatCompletions ? GitHubCopilotApiKind.ChatCompletions : null;
}

/// <summary>
/// Caches authenticated Copilot model capabilities by the effective API endpoint
/// and the canonical server model id. Tokens are intentionally never retained.
/// </summary>
internal sealed class GitHubCopilotModelCatalog
{
    private readonly ConcurrentDictionary<GitHubCopilotCatalogKey, GitHubCopilotModelCapability> _entries = new();

    public void Store(Uri apiEndpoint, IEnumerable<GitHubCopilotModelCapability> capabilities)
    {
        var endpoint = NormalizeEndpoint(apiEndpoint);
        foreach (var capability in capabilities)
            _entries[new GitHubCopilotCatalogKey(endpoint, NormalizeModelId(capability.ModelId))] = capability;
    }

    public GitHubCopilotModelCapability? Find(Uri apiEndpoint, string modelId)
    {
        var endpoint = NormalizeEndpoint(apiEndpoint);
        return _entries.TryGetValue(
            new GitHubCopilotCatalogKey(endpoint, NormalizeModelId(modelId)), out var capability)
            ? capability
            : null;
    }

    private static string NormalizeEndpoint(Uri endpoint) => endpoint.AbsoluteUri.TrimEnd('/');
    private static string NormalizeModelId(string modelId) => modelId.ToUpperInvariant();

    private readonly record struct GitHubCopilotCatalogKey(string Endpoint, string ModelId);
}

/// <summary>Minimal lazy delegate that selects the Copilot API exactly once.</summary>
internal sealed class GitHubCopilotCapabilityResolvingChatClient(
    Func<Task<IChatClient>> initialize) : IChatClient
{
    private readonly object _initializationLock = new();
    private Task<IChatClient>? _innerTask;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inner = await GetInnerAsync(cancellationToken).ConfigureAwait(false);
        return await inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var inner = await GetInnerAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        _innerTask is { IsCompletedSuccessfully: true }
            ? _innerTask.Result.GetService(serviceType, serviceKey)
            : null;

    public void Dispose()
    {
        if (_innerTask is { IsCompletedSuccessfully: true })
            _innerTask.Result.Dispose();
    }

    private Task<IChatClient> GetInnerAsync(CancellationToken cancellationToken)
    {
        Task<IChatClient> task;
        lock (_initializationLock)
            task = _innerTask ??= initialize();

        return task.WaitAsync(cancellationToken);
    }
}
