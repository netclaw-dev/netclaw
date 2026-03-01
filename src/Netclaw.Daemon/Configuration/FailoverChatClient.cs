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
        // Try primary stream creation first.
        IAsyncEnumerable<ChatResponseUpdate> stream;
        Exception? primaryInitiationFailure = null;
        try
        {
            stream = _primary.GetStreamingResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            primaryInitiationFailure = ex;
            stream = _fallback.GetStreamingResponseAsync(messages, options, cancellationToken);
        }

        if (primaryInitiationFailure is not null)
        {
            _logger.LogWarning(primaryInitiationFailure,
                "Primary LLM streaming failed on initiation, failing over to fallback");

            await foreach (var fallbackUpdate in stream)
                yield return fallbackUpdate;

            yield break;
        }

        // Primary stream started. If the first MoveNextAsync fails, fail over.
        // Once any primary chunk is emitted, failures propagate to avoid mixed-output artifacts.
        await using var primaryEnumerator = stream.GetAsyncEnumerator(cancellationToken);
        var yieldedPrimaryChunk = false;
        Exception? primaryPreFirstChunkFailure = null;

        while (true)
        {
            ChatResponseUpdate update;
            try
            {
                if (!await primaryEnumerator.MoveNextAsync())
                    break;

                update = primaryEnumerator.Current;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && !yieldedPrimaryChunk)
            {
                primaryPreFirstChunkFailure = ex;
                break;
            }

            yieldedPrimaryChunk = true;
            yield return update;
        }

        if (primaryPreFirstChunkFailure is null)
            yield break;

        _logger.LogWarning(primaryPreFirstChunkFailure,
            "Primary LLM streaming failed before first chunk, failing over to fallback");

        await foreach (var fallbackUpdate in _fallback.GetStreamingResponseAsync(messages, options, cancellationToken))
            yield return fallbackUpdate;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _primary.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        _primary.Dispose();
        _fallback.Dispose();
    }
}
