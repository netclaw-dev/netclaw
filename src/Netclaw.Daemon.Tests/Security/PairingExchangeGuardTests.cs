// -----------------------------------------------------------------------
// <copyright file="PairingExchangeGuardTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Daemon.Security;
using Xunit;

namespace Netclaw.Daemon.Tests.Security;

/// <summary>
/// Unit tests for <see cref="PairingExchangeGuard"/>.
/// Validates per-IP failure tracking, lockout after threshold, and expiry behavior.
/// </summary>
public sealed class PairingExchangeGuardTests
{
    private readonly FakeTimeProvider _time;
    private readonly PairingExchangeGuard _guard;

    public PairingExchangeGuardTests()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero));
        _guard = new PairingExchangeGuard(_time);
    }

    [Fact]
    public void IsBlocked_ReturnsFalse_WhenNoFailures()
    {
        var ip = IPAddress.Parse("192.168.1.100");
        Assert.False(_guard.IsBlocked(ip));
    }

    [Fact]
    public void IsBlocked_ReturnsFalse_AfterFewFailures()
    {
        var ip = IPAddress.Parse("192.168.1.100");

        for (var i = 0; i < PairingExchangeGuard.FailureThreshold - 1; i++)
            _guard.RecordFailure(ip);

        Assert.False(_guard.IsBlocked(ip));
    }

    [Fact]
    public void IsBlocked_ReturnsTrue_AfterThresholdFailures()
    {
        var ip = IPAddress.Parse("192.168.1.100");

        for (var i = 0; i < PairingExchangeGuard.FailureThreshold; i++)
            _guard.RecordFailure(ip);

        Assert.True(_guard.IsBlocked(ip));
    }

    [Fact]
    public void IsBlocked_ReturnsFalse_AfterLockoutExpires()
    {
        var ip = IPAddress.Parse("192.168.1.100");

        for (var i = 0; i < PairingExchangeGuard.FailureThreshold; i++)
            _guard.RecordFailure(ip);

        Assert.True(_guard.IsBlocked(ip));

        // Advance past the lockout duration.
        _time.Advance(PairingExchangeGuard.LockoutDuration + TimeSpan.FromSeconds(1));

        Assert.False(_guard.IsBlocked(ip));
    }

    [Fact]
    public void RecordFailure_IgnoresNullIp()
    {
        // Should not throw or create any state.
        _guard.RecordFailure(null);
        Assert.False(_guard.IsBlocked(null));
    }

    [Fact]
    public void GetRetryAfterSeconds_ReturnsValue_WhenBlocked()
    {
        var ip = IPAddress.Parse("10.0.0.1");

        for (var i = 0; i < PairingExchangeGuard.FailureThreshold; i++)
            _guard.RecordFailure(ip);

        var retryAfter = _guard.GetRetryAfterSeconds(ip);
        Assert.NotNull(retryAfter);
        Assert.True(retryAfter > 0);
    }

    [Fact]
    public void GetRetryAfterSeconds_ReturnsNull_WhenNotBlocked()
    {
        var ip = IPAddress.Parse("10.0.0.1");
        Assert.Null(_guard.GetRetryAfterSeconds(ip));
    }

    [Fact]
    public void Lockout_IsolatesPerIp()
    {
        var attacker = IPAddress.Parse("10.0.0.1");
        var innocent = IPAddress.Parse("10.0.0.2");

        for (var i = 0; i < PairingExchangeGuard.FailureThreshold; i++)
            _guard.RecordFailure(attacker);

        Assert.True(_guard.IsBlocked(attacker));
        Assert.False(_guard.IsBlocked(innocent));
    }

    [Fact]
    public void RecordFailure_RestartsCount_AfterLockoutExpires()
    {
        var ip = IPAddress.Parse("10.0.0.1");

        // Trigger lockout.
        for (var i = 0; i < PairingExchangeGuard.FailureThreshold; i++)
            _guard.RecordFailure(ip);
        Assert.True(_guard.IsBlocked(ip));

        // Expire the lockout.
        _time.Advance(PairingExchangeGuard.LockoutDuration + TimeSpan.FromSeconds(1));
        Assert.False(_guard.IsBlocked(ip));

        // A single new failure should not re-block.
        _guard.RecordFailure(ip);
        Assert.False(_guard.IsBlocked(ip));
    }

    [Fact]
    public void RecordFailure_OldFailuresOutsideWindow_DoNotCountTowardThreshold()
    {
        var ip = IPAddress.Parse("10.0.0.3");

        for (var i = 0; i < PairingExchangeGuard.FailureThreshold - 1; i++)
            _guard.RecordFailure(ip);

        _time.Advance(PairingExchangeGuard.FailureWindow + TimeSpan.FromSeconds(1));

        _guard.RecordFailure(ip);
        Assert.False(_guard.IsBlocked(ip));
    }

    [Fact]
    public void RecordFailure_HitsThresholdOnlyWithinRollingWindow()
    {
        var ip = IPAddress.Parse("10.0.0.4");

        for (var i = 0; i < PairingExchangeGuard.FailureThreshold - 1; i++)
            _guard.RecordFailure(ip);

        _time.Advance(PairingExchangeGuard.FailureWindow - TimeSpan.FromMinutes(1));
        _guard.RecordFailure(ip);

        Assert.True(_guard.IsBlocked(ip));
    }
}
