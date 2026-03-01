using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Decorates an <see cref="IChatClient"/> with retry logic for transient failures.
/// Wraps <see cref="IChatClient.GetResponseAsync"/> with a retry loop.
/// Streaming calls are NOT retried (mid-stream restart is deferred to a follow-up).
/// </summary>
public sealed class RetryingChatClient : DelegatingChatClient
{
    private readonly RetryPolicy _policy;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public RetryingChatClient(
        IChatClient innerClient,
        RetryPolicy policy,
        ILogger logger,
        TimeProvider? timeProvider = null)
        : base(innerClient)
    {
        _policy = policy;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await base.GetResponseAsync(messages, options, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                       && _policy.ShouldRetry(ex, attempt))
            {
                var delay = _policy.GetDelay(attempt);
                _logger.LogWarning(ex,
                    "LLM call failed (attempt {Attempt}/{Max}), retrying in {Delay:F1}s",
                    attempt + 1, _policy.MaxRetries, delay.TotalSeconds);

                await Task.Delay(delay, _timeProvider, cancellationToken);
                attempt++;
            }
        }
    }
}
