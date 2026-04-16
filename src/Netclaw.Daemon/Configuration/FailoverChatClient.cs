using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// An <see cref="IChatClient"/> that tries a primary client first,
/// then fails over to a fallback if the primary throws after all retries
/// are exhausted. Both primary and fallback should already be wrapped
/// in their own retry/logging decorators.
///
/// Emits <c>provider.failover</c> alerts when the primary fails and we
/// switch to the fallback, and <c>provider.unreachable</c> alerts when
/// the fallback also fails.
/// </summary>
public sealed class FailoverChatClient : IChatClient
{
    private readonly IChatClient _primary;
    private readonly IChatClient _fallback;
    private readonly ILogger _logger;
    private readonly IOperationalNotificationSink _notificationSink;
    private readonly TimeProvider _timeProvider;

    public FailoverChatClient(
        IChatClient primary,
        IChatClient fallback,
        ILogger logger,
        IOperationalNotificationSink notificationSink,
        TimeProvider timeProvider)
    {
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
        _notificationSink = notificationSink;
        _timeProvider = timeProvider;
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
            EmitFailoverAlert(ex);

            try
            {
                return await _fallback.GetResponseAsync(messages, options, cancellationToken);
            }
            catch (Exception fallbackEx) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(fallbackEx,
                    "Fallback LLM also failed — all providers unreachable");
                EmitUnreachableAlert(fallbackEx);
                throw;
            }
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
            EmitFailoverAlert(ex);

            IAsyncEnumerable<ChatResponseUpdate> fallbackStream;
            try
            {
                fallbackStream = _fallback.GetStreamingResponseAsync(messages, options, cancellationToken);
            }
            catch (Exception fallbackEx) when (!cancellationToken.IsCancellationRequested)
            {
                EmitUnreachableAlert(fallbackEx);
                throw;
            }

            stream = fallbackStream;
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
        EmitFailoverAlert(primaryPreFirstChunkFailure);

        IAsyncEnumerable<ChatResponseUpdate> preChunkFallbackStream;
        try
        {
            preChunkFallbackStream = _fallback.GetStreamingResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception fallbackEx) when (!cancellationToken.IsCancellationRequested)
        {
            EmitUnreachableAlert(fallbackEx);
            throw;
        }

        await foreach (var fallbackUpdate in preChunkFallbackStream)
            yield return fallbackUpdate;
    }

    private void EmitFailoverAlert(Exception ex)
    {
        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "provider.failover",
            AlertType.ProviderFailover,
            "Primary LLM provider failed, failing over to fallback",
            AlertSeverity.Warning,
            context: new Dictionary<string, string> { ["error"] = ex.Message }));
    }

    private void EmitUnreachableAlert(Exception ex)
    {
        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "provider.unreachable",
            AlertType.ProviderUnreachable,
            "All LLM providers failed — primary and fallback both unreachable",
            AlertSeverity.Critical,
            context: new Dictionary<string, string> { ["error"] = ex.Message }));
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _primary.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        _primary.Dispose();
        _fallback.Dispose();
    }
}
