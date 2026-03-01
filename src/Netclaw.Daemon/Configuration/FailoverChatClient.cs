using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// An <see cref="IChatClient"/> that tries a primary client first,
/// then fails over to a fallback if the primary throws after all retries
/// are exhausted. Both primary and fallback should already be wrapped
/// in their own retry/logging decorators.
/// </summary>
public sealed class FailoverChatClient : IChatClient
{
    private readonly IChatClient _primary;
    private readonly IChatClient _fallback;
    private readonly ILogger _logger;

    public FailoverChatClient(
        IChatClient primary,
        IChatClient fallback,
        ILogger logger)
    {
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _primary.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Primary LLM failed, failing over to fallback provider");
            return await _fallback.GetResponseAsync(messages, options, cancellationToken);
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return StreamWithFailoverAsync(messages, options, cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamWithFailoverAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        // Try primary initiation — if it throws, fall back.
        // Mid-stream failures are NOT caught here (C# limitation: no yield in try-catch).
        // The retry decorator on the primary already handles transient errors.
        IAsyncEnumerable<ChatResponseUpdate> stream;
        try
        {
            stream = _primary.GetStreamingResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Primary LLM streaming failed on initiation, failing over to fallback");
            stream = _fallback.GetStreamingResponseAsync(messages, options, cancellationToken);
        }

        await foreach (var update in stream)
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _primary.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        _primary.Dispose();
        _fallback.Dispose();
    }
}
