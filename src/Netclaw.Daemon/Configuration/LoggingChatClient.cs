using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Decorates an <see cref="IChatClient"/> with logging for elapsed time,
/// token usage, and errors.
/// </summary>
public sealed class LoggingChatClient : DelegatingChatClient
{
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public LoggingChatClient(
        IChatClient innerClient,
        ILogger logger,
        TimeProvider? timeProvider = null)
        : base(innerClient)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var start = _timeProvider.GetTimestamp();
        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            var elapsed = _timeProvider.GetElapsedTime(start);
            LogCompletion(elapsed, response.Usage);
            return response;
        }
        catch (Exception ex)
        {
            var elapsed = _timeProvider.GetElapsedTime(start);
            _logger.LogError(ex, "LLM call failed after {ElapsedMs:F0}ms", elapsed.TotalMilliseconds);
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var start = _timeProvider.GetTimestamp();
        long inputTokens = 0;
        long outputTokens = 0;

        IAsyncEnumerable<ChatResponseUpdate> stream;
        try
        {
            stream = base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex)
        {
            var elapsed = _timeProvider.GetElapsedTime(start);
            _logger.LogError(ex, "LLM streaming call failed after {ElapsedMs:F0}ms", elapsed.TotalMilliseconds);
            throw;
        }

        await foreach (var update in stream)
        {
            if (update.Contents?.OfType<UsageContent>().FirstOrDefault() is { } usage)
            {
                inputTokens += usage.Details?.InputTokenCount ?? 0;
                outputTokens += usage.Details?.OutputTokenCount ?? 0;
            }

            yield return update;
        }

        var totalElapsed = _timeProvider.GetElapsedTime(start);
        if (inputTokens > 0 || outputTokens > 0)
        {
            _logger.LogInformation(
                "LLM streaming call completed in {ElapsedMs:F0}ms (input: {InputTokens}, output: {OutputTokens})",
                totalElapsed.TotalMilliseconds, inputTokens, outputTokens);
        }
        else
        {
            _logger.LogInformation(
                "LLM streaming call completed in {ElapsedMs:F0}ms",
                totalElapsed.TotalMilliseconds);
        }
    }

    private void LogCompletion(TimeSpan elapsed, UsageDetails? usage)
    {
        if (usage is not null)
        {
            _logger.LogInformation(
                "LLM call completed in {ElapsedMs:F0}ms (input: {InputTokens}, output: {OutputTokens})",
                elapsed.TotalMilliseconds,
                usage.InputTokenCount ?? 0,
                usage.OutputTokenCount ?? 0);
        }
        else
        {
            _logger.LogInformation(
                "LLM call completed in {ElapsedMs:F0}ms",
                elapsed.TotalMilliseconds);
        }
    }
}
