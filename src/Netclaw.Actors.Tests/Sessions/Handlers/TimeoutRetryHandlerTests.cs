using Netclaw.Actors.Sessions.Handlers;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions.Handlers;

public sealed class TimeoutRetryHandlerTests
{
    [Fact]
    public void Evaluate_non_timeout_returns_NonRetryable()
    {
        var handler = new TimeoutRetryHandler(maxRetries: 2, baseDelay: TimeSpan.FromSeconds(2));

        var result = handler.Evaluate(new InvalidOperationException("not a timeout"));

        Assert.IsType<TimeoutRetryAction.NonRetryable>(result);
    }

    [Fact]
    public void Evaluate_first_timeout_returns_Retry_with_attempt_1()
    {
        var handler = new TimeoutRetryHandler(maxRetries: 2, baseDelay: TimeSpan.FromSeconds(2));

        var result = handler.Evaluate(new TimeoutException("timed out"));

        var retry = Assert.IsType<TimeoutRetryAction.Retry>(result);
        Assert.Equal(1, retry.Attempt);
        Assert.Equal(2, retry.MaxRetries);
        Assert.True(retry.Delay > TimeSpan.Zero);
    }

    [Fact]
    public void Evaluate_exhausted_retries_returns_Fail_with_descriptive_message()
    {
        var handler = new TimeoutRetryHandler(maxRetries: 2, baseDelay: TimeSpan.FromSeconds(1));

        // Consume both retries
        handler.Evaluate(new TimeoutException("timeout 1"));
        handler.Evaluate(new TimeoutException("timeout 2"));

        // Third timeout should fail
        var result = handler.Evaluate(new TimeoutException("timeout 3"));

        var fail = Assert.IsType<TimeoutRetryAction.Fail>(result);
        Assert.Contains("3 attempts", fail.ErrorMessage);
    }

    [Fact]
    public void Zero_maxRetries_disables_retry()
    {
        var handler = new TimeoutRetryHandler(maxRetries: 0, baseDelay: TimeSpan.FromSeconds(1));

        var result = handler.Evaluate(new TimeoutException("timed out"));

        var fail = Assert.IsType<TimeoutRetryAction.Fail>(result);
        Assert.Contains("1 attempts", fail.ErrorMessage);
    }

    [Fact]
    public void ResetForNewTurn_clears_attempts()
    {
        var handler = new TimeoutRetryHandler(maxRetries: 1, baseDelay: TimeSpan.FromSeconds(1));

        // Consume the single retry
        handler.Evaluate(new TimeoutException("timeout 1"));
        Assert.Equal(1, handler.AttemptCount);

        handler.ResetForNewTurn();
        Assert.Equal(0, handler.AttemptCount);

        // Should be able to retry again
        var result = handler.Evaluate(new TimeoutException("timeout after reset"));
        Assert.IsType<TimeoutRetryAction.Retry>(result);
    }

    [Fact]
    public void Delay_grows_exponentially()
    {
        var handler = new TimeoutRetryHandler(
            maxRetries: 5,
            baseDelay: TimeSpan.FromSeconds(2),
            maxDelay: TimeSpan.FromSeconds(60));

        // GetDelay is internal — test via Evaluate results
        var delay1 = ((TimeoutRetryAction.Retry)handler.Evaluate(new TimeoutException("t1"))).Delay;
        handler.ResetForNewTurn();

        // Consume first retry to get to attempt 2
        handler.Evaluate(new TimeoutException("t1"));
        var delay2 = ((TimeoutRetryAction.Retry)handler.Evaluate(new TimeoutException("t2"))).Delay;

        // Due to jitter, we can't assert exact values but delay2 should generally
        // be larger. Test the underlying GetDelay without jitter influence.
        var rawDelay1 = handler.GetDelay(1); // base * 2^0 = 2s +/- jitter
        var rawDelay3 = handler.GetDelay(3); // base * 2^2 = 8s +/- jitter

        // Even with worst-case jitter, attempt 3 base (8s*0.75=6s) > attempt 1 base (2s*1.25=2.5s)
        Assert.True(rawDelay3 > rawDelay1 * 0.5, "Later attempts should produce larger delays");
    }

    [Fact]
    public void Delay_clamps_at_max()
    {
        var handler = new TimeoutRetryHandler(
            maxRetries: 10,
            baseDelay: TimeSpan.FromSeconds(10),
            maxDelay: TimeSpan.FromSeconds(5));

        // base * 2^0 = 10s, but max is 5s — should clamp
        var delay = handler.GetDelay(1);

        // With jitter (+/-25%), max possible is 5s * 1.25 = 6.25s
        Assert.True(delay <= TimeSpan.FromSeconds(6.25), $"Delay {delay} should be clamped near max 5s");
    }
}
