using Microsoft.Extensions.AI;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Thin decorator that emits a <c>provider.unreachable</c> alert when the
/// underlying <see cref="IChatClient"/> throws. Used for single-provider setups
/// where no <see cref="FailoverChatClient"/> is in the chain.
///
/// The exception is always re-thrown after emitting the alert — this decorator
/// does not change error-handling behavior, only adds notification.
/// </summary>
public sealed class AlertingChatClientDecorator : IChatClient
{
    private readonly IChatClient _inner;
    private readonly IOperationalNotificationSink _notificationSink;
    private readonly TimeProvider _timeProvider;

    public AlertingChatClientDecorator(
        IChatClient inner,
        IOperationalNotificationSink notificationSink,
        TimeProvider timeProvider)
    {
        _inner = inner;
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
            return await _inner.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            EmitUnreachableAlert(ex);
            throw;
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return StreamWithAlertingAsync(messages, options, cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamWithAlertingAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        IAsyncEnumerable<ChatResponseUpdate> stream;
        try
        {
            stream = _inner.GetStreamingResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            EmitUnreachableAlert(ex);
            throw;
        }

        await foreach (var update in stream)
            yield return update;
    }

    private void EmitUnreachableAlert(Exception ex)
    {
        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "provider.unreachable",
            AlertType.ProviderUnreachable,
            "LLM provider unreachable — no fallback configured",
            AlertSeverity.Critical,
            context: new Dictionary<string, string> { ["error"] = ex.Message }));
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}
