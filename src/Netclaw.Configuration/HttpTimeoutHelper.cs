// -----------------------------------------------------------------------
// <copyright file="HttpTimeoutHelper.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Outcome of an HTTP operation executed with a timeout via
/// <see cref="HttpTimeoutHelper.ExecuteWithTimeoutAsync{T}"/>.
/// </summary>
public enum HttpTimeoutOutcome
{
    /// <summary>The operation completed successfully within the timeout.</summary>
    Success,

    /// <summary>The operation was cancelled because the timeout elapsed.</summary>
    TimedOut,

    /// <summary>The operation failed with an <see cref="HttpRequestException"/>.</summary>
    HttpError,
}

/// <summary>
/// Result of an HTTP operation executed with a timeout. Wraps the value
/// (on success) or the exception (on HTTP failure) alongside the outcome.
/// </summary>
public readonly record struct HttpTimeoutResult<T>(
    HttpTimeoutOutcome Outcome,
    T? Value,
    HttpRequestException? Exception)
{
    public bool IsSuccess => Outcome == HttpTimeoutOutcome.Success;
}

/// <summary>
/// Encapsulates the repeated pattern of creating a linked
/// <see cref="CancellationTokenSource"/> with a timeout, executing an async
/// HTTP operation, and distinguishing timeout cancellation from caller
/// cancellation. Keeps the <c>OperationCanceledException</c> semantics
/// correct: if the <em>caller's</em> token is cancelled the exception
/// propagates; if only the timeout fires the result is
/// <see cref="HttpTimeoutOutcome.TimedOut"/>.
/// </summary>
public static class HttpTimeoutHelper
{
    /// <summary>
    /// Executes <paramref name="operation"/> with a linked cancellation token
    /// that will auto-cancel after <paramref name="timeout"/>.
    /// </summary>
    /// <returns>
    /// A result indicating success (with value), timeout, or HTTP error.
    /// If the caller's <paramref name="cancellationToken"/> is cancelled,
    /// the <see cref="OperationCanceledException"/> propagates unhandled.
    /// </returns>
    public static async Task<HttpTimeoutResult<T>> ExecuteWithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            var result = await operation(cts.Token);
            return new HttpTimeoutResult<T>(HttpTimeoutOutcome.Success, result, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HttpTimeoutResult<T>(HttpTimeoutOutcome.TimedOut, default, null);
        }
        catch (HttpRequestException ex)
        {
            return new HttpTimeoutResult<T>(HttpTimeoutOutcome.HttpError, default, ex);
        }
    }
}
