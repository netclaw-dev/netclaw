namespace Netclaw.Actors.Sessions.Handlers;

/// <summary>
/// Owns timeout retry decisions for LLM calls. The actor asks "should I retry?"
/// and the handler answers based on accumulated state. Follows the same pattern
/// as <see cref="TurnStateTracker"/> — a plain class with no actor dependencies,
/// independently testable.
/// </summary>
internal sealed class TimeoutRetryHandler
{
    private static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(30);

    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;

    private int _attemptCount;

    public TimeoutRetryHandler(int maxRetries, TimeSpan baseDelay, TimeSpan? maxDelay = null)
    {
        _maxRetries = maxRetries;
        _baseDelay = baseDelay;
        _maxDelay = maxDelay ?? DefaultMaxDelay;
    }

    public int AttemptCount => _attemptCount;

    /// <summary>
    /// Reset retry state for a new user turn.
    /// </summary>
    public void ResetForNewTurn() => _attemptCount = 0;

    /// <summary>
    /// Evaluate whether an LLM call failure should be retried.
    /// Only <see cref="TimeoutException"/> is retryable — all other failures fail fast.
    /// </summary>
    public TimeoutRetryAction Evaluate(Exception cause)
    {
        // Only timeouts are retryable — all other failures should be handled by the caller
        if (cause is not TimeoutException)
            return new TimeoutRetryAction.NonRetryable(cause);

        if (_attemptCount >= _maxRetries)
            return new TimeoutRetryAction.Fail(
                $"The LLM provider timed out after {_attemptCount + 1} attempts. "
                + "Please try sending your message again, or consider simplifying your request.",
                cause);

        _attemptCount++;
        var delay = GetDelay(_attemptCount);
        return new TimeoutRetryAction.Retry(_attemptCount, _maxRetries, delay);
    }

    internal TimeSpan GetDelay(int attempt)
    {
        var raw = _baseDelay * Math.Pow(2, attempt - 1);
        var clamped = raw > _maxDelay ? _maxDelay : raw;
        var jitter = 1.0 + (Random.Shared.NextDouble() - 0.5) * 0.5; // +/-25%
        return clamped * jitter;
    }

}

// ── Result types ──

/// <summary>Result of <see cref="TimeoutRetryHandler.Evaluate"/>.</summary>
internal abstract record TimeoutRetryAction
{
    /// <summary>Retry the LLM call after the specified backoff delay.</summary>
    internal sealed record Retry(int Attempt, int MaxRetries, TimeSpan Delay) : TimeoutRetryAction;

    /// <summary>Fail the turn with the given error message and cause.</summary>
    internal sealed record Fail(string ErrorMessage, Exception Cause) : TimeoutRetryAction;

    /// <summary>Not a timeout — caller should handle with its own error extraction.</summary>
    internal sealed record NonRetryable(Exception Cause) : TimeoutRetryAction;
}
