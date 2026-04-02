using System.Collections.Concurrent;
using System.Net;

namespace Netclaw.Daemon.Security;

/// <summary>
/// Fail2ban-style lockout guard for the pairing exchange endpoint.
///
/// <para>
/// Tracks failed exchange attempts per IP address. After <see cref="FailureThreshold"/>
/// failures within any rolling window, the IP is blocked for <see cref="LockoutDuration"/>.
/// This supplements the ASP.NET rate limiter as an additional defense layer when the daemon
/// is exposed to the internet via Cloudflare Tunnel or Tailscale Funnel.
/// </para>
///
/// <para>State is in-memory only — resets on daemon restart, which is acceptable.</para>
/// </summary>
public sealed class PairingExchangeGuard
{
    internal const int FailureThreshold = 10;
    internal static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, IpRecord> _records = new();

    public PairingExchangeGuard(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns <c>true</c> if the given IP is currently blocked due to exceeding the failure threshold.
    /// Returns <c>false</c> for <c>null</c> IPs (e.g., loopback / TestServer where RemoteIpAddress is unset).
    /// </summary>
    public bool IsBlocked(IPAddress? remoteIp)
    {
        if (remoteIp is null)
            return false;

        var key = remoteIp.ToString();
        if (!_records.TryGetValue(key, out var record))
            return false;

        var now = _timeProvider.GetUtcNow();

        if (record.BlockedUntil is { } blockedUntil)
        {
            if (now < blockedUntil)
                return true;

            // Lockout expired — clean up the entry.
            _records.TryRemove(key, out _);
            return false;
        }

        return false;
    }

    /// <summary>
    /// Returns the <c>Retry-After</c> value in seconds for a blocked IP,
    /// or <c>null</c> if the IP is not blocked.
    /// </summary>
    public int? GetRetryAfterSeconds(IPAddress? remoteIp)
    {
        if (remoteIp is null)
            return null;

        var key = remoteIp.ToString();
        if (!_records.TryGetValue(key, out var record))
            return null;

        if (record.BlockedUntil is not { } blockedUntil)
            return null;

        var remaining = blockedUntil - _timeProvider.GetUtcNow();
        return remaining > TimeSpan.Zero ? (int)Math.Ceiling(remaining.TotalSeconds) : null;
    }

    /// <summary>
    /// Records a failed exchange attempt for the given IP.
    /// If the failure count reaches <see cref="FailureThreshold"/>, the IP is blocked
    /// for <see cref="LockoutDuration"/>.
    /// </summary>
    public void RecordFailure(IPAddress? remoteIp)
    {
        if (remoteIp is null)
            return;

        var key = remoteIp.ToString();
        var now = _timeProvider.GetUtcNow();

        _records.AddOrUpdate(
            key,
            _ => new IpRecord(1, null),
            (_, existing) =>
            {
                // If a previous lockout expired, start fresh.
                if (existing.BlockedUntil is { } blockedUntil && now >= blockedUntil)
                    return new IpRecord(1, null);

                var newCount = existing.FailCount + 1;
                var blocked = newCount >= FailureThreshold
                    ? now.Add(LockoutDuration)
                    : existing.BlockedUntil;

                return new IpRecord(newCount, blocked);
            });
    }

    /// <summary>
    /// Internal record holding per-IP failure state.
    /// </summary>
    private sealed record IpRecord(int FailCount, DateTimeOffset? BlockedUntil);
}
