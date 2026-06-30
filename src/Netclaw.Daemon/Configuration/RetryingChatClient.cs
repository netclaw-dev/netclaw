// -----------------------------------------------------------------------
// <copyright file="RetryingChatClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Decorates an <see cref="IChatClient"/> with retry logic for transient failures.
/// Both the non-streaming and streaming calls are retried. Streaming is retried
/// only <b>before the first chunk is yielded</b> — once any update has been emitted
/// downstream, a later failure propagates unchanged so already-streamed output is
/// never duplicated by a restart.
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
                await BackoffAsync(ex, attempt, cancellationToken);
                attempt++;
            }
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Materialize once so re-initiation on retry re-sends the same prompt.
        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var attempt = 0;

        while (true)
        {
            var yieldedChunk = false;
            Exception? preFirstChunkFailure = null;

            // Initiation can throw before any enumerator is produced.
            IAsyncEnumerable<ChatResponseUpdate> stream;
            try
            {
                stream = base.GetStreamingResponseAsync(messageList, options, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                       && _policy.ShouldRetry(ex, attempt))
            {
                await BackoffAsync(ex, attempt, cancellationToken);
                attempt++;
                continue;
            }

            await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        break;

                    update = enumerator.Current;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                           && !yieldedChunk
                                           && _policy.ShouldRetry(ex, attempt))
                {
                    // Pre-first-chunk failure: safe to restart (nothing emitted yet).
                    preFirstChunkFailure = ex;
                    break;
                }

                // Past this point a chunk has been emitted; a later failure is NOT
                // caught above (yieldedChunk is true) and propagates to the consumer.
                yieldedChunk = true;
                yield return update;
            }

            if (preFirstChunkFailure is null)
                yield break; // clean completion (or a post-first-chunk throw already unwound)

            await BackoffAsync(preFirstChunkFailure, attempt, cancellationToken);
            attempt++;
            // outer loop re-initiates the stream
        }
    }

    private async Task BackoffAsync(Exception ex, int attempt, CancellationToken cancellationToken)
    {
        var delay = _policy.GetDelay(attempt);
        // No SessionId scope is opened here: PipelineChatClientFactory.Compose always
        // wraps this decorator inside LoggingChatClient (guarded by
        // Compose_puts_Logging_outermost), whose streaming scope stays open for the whole
        // enumeration that drives this retry loop — so the warning already inherits the
        // session id. Re-opening an identical scope would be pure duplication.
        //
        // CAVEAT — this inheritance holds ONLY on the streaming path. LoggingChatClient
        // instruments streaming only; its inherited non-streaming GetResponseAsync opens no
        // scope. Netclaw issues only streaming requests today, so the non-streaming retry
        // path above is unreachable — but if that ever changes, these retry warnings would
        // carry no SessionId and the file-logger would route them to daemon.log instead of
        // the owning session.log (and they'd be uncorrelated in Seq). The fix at that point
        // is to open a SessionId scope here from `options` (ChatClientSessionScope.Begin),
        // or to instrument LoggingChatClient.GetResponseAsync. See RetryingChatClientTests.
        _logger.LogWarning(ex,
            "LLM call failed (attempt {Attempt}/{Max}), retrying in {Delay:F1}s",
            attempt + 1, _policy.MaxRetries, delay.TotalSeconds);

        await Task.Delay(delay, _timeProvider, cancellationToken);
    }
}
