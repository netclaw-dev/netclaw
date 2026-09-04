// -----------------------------------------------------------------------
// <copyright file="PairingCodeServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using Netclaw.Daemon.Security;
using Xunit;

namespace Netclaw.Daemon.Tests.Security;

/// <summary>
/// Unit tests for <see cref="PairingCodeService"/>.
/// Verifies code generation, validation, expiry, single-use semantics, and replacement.
/// </summary>
public sealed class PairingCodeServiceTests
{
    private readonly FakeTimeProvider _time;
    private readonly PairingCodeService _service;

    public PairingCodeServiceTests()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero));
        _service = new PairingCodeService(_time);
    }

    // ── GenerateCode ──────────────────────────────────────────────────────────

    [Fact]
    public void GenerateCode_returns_formatted_XXXX_XXXX_code()
    {
        var (formatted, _) = _service.GenerateCode();

        Assert.Matches(@"^[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{4}-[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{4}$", formatted);
    }

    [Fact]
    public void GenerateCode_expiry_is_5_minutes_from_now()
    {
        var (_, expiresAt) = _service.GenerateCode();

        Assert.Equal(_time.GetUtcNow().AddMinutes(5), expiresAt);
    }

    [Fact]
    public void GenerateCode_replaces_previous_pending_code()
    {
        var (first, _) = _service.GenerateCode();
        var (second, _) = _service.GenerateCode();

        Assert.False(TryConsume(first));
        Assert.True(TryConsume(second));
    }

    [Fact]
    public void GenerateCode_produces_codes_only_from_reduced_alphabet()
    {
        const string ForbiddenChars = "01IO";
        for (var i = 0; i < 20; i++)
        {
            var (formatted, _) = _service.GenerateCode();
            var codeOnly = formatted.Replace("-", "");
            foreach (var ch in ForbiddenChars)
                Assert.DoesNotContain(ch, codeOnly);
        }
    }

    // ── TryConsume ────────────────────────────────────────────────────────────

    [Fact]
    public void TryConsume_returns_true_for_valid_code_with_dash()
    {
        var (formatted, _) = _service.GenerateCode();

        Assert.True(TryConsume(formatted));
    }

    [Fact]
    public void TryConsume_returns_true_for_valid_code_without_dash()
    {
        var (formatted, _) = _service.GenerateCode();
        var noDash = formatted.Replace("-", "");

        Assert.True(TryConsume(noDash));
    }

    [Fact]
    public void TryConsume_is_case_insensitive()
    {
        var (formatted, _) = _service.GenerateCode();

        Assert.True(TryConsume(formatted.ToLowerInvariant()));
    }

    [Fact]
    public void TryConsume_is_single_use()
    {
        var (formatted, _) = _service.GenerateCode();

        Assert.True(TryConsume(formatted));
        Assert.False(TryConsume(formatted));
    }

    [Fact]
    public void TryConsume_returns_false_when_no_code_pending()
    {
        Assert.False(TryConsume("ABCD-1234"));
    }

    [Fact]
    public void TryConsume_returns_false_for_wrong_code()
    {
        _service.GenerateCode();

        Assert.False(TryConsume("ZZZZ-ZZZZ"));
    }

    [Fact]
    public void TryConsume_returns_false_after_code_expires()
    {
        var (formatted, _) = _service.GenerateCode();

        _time.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));

        Assert.False(TryConsume(formatted));
    }

    [Fact]
    public void TryConsume_returns_true_just_before_expiry()
    {
        var (formatted, _) = _service.GenerateCode();

        _time.Advance(TimeSpan.FromMinutes(5).Subtract(TimeSpan.FromSeconds(1)));

        Assert.True(TryConsume(formatted));
    }

    [Fact]
    public void TryReserve_returns_false_at_exact_expiry()
    {
        var (formatted, _) = _service.GenerateCode();
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Null(_service.TryReserve(formatted));
    }

    [Fact]
    public void Reservation_remains_consumable_after_expiry()
    {
        var (formatted, _) = _service.GenerateCode();
        var reservation = _service.TryReserve(formatted);
        Assert.NotNull(reservation);

        _time.Advance(TimeSpan.FromMinutes(6));

        Assert.True(_service.TryConsume(reservation.Value));
        Assert.Null(_service.GetPendingExpiry());
    }

    // ── GetPendingExpiry ──────────────────────────────────────────────────────

    [Fact]
    public void GetPendingExpiry_returns_null_when_no_code_pending()
    {
        Assert.Null(_service.GetPendingExpiry());
    }

    [Fact]
    public void GetPendingExpiry_returns_expiry_when_code_pending()
    {
        var (_, expectedExpiry) = _service.GenerateCode();

        var expiry = _service.GetPendingExpiry();

        Assert.Equal(expectedExpiry, expiry);
    }

    [Fact]
    public void GetPendingExpiry_returns_null_after_code_consumed()
    {
        var (formatted, _) = _service.GenerateCode();
        Assert.True(TryConsume(formatted));

        Assert.Null(_service.GetPendingExpiry());
    }

    [Fact]
    public void GetPendingExpiry_returns_null_after_code_expires()
    {
        _service.GenerateCode();
        _time.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));

        Assert.Null(_service.GetPendingExpiry());
    }

    private bool TryConsume(string code)
    {
        var reservation = _service.TryReserve(code);
        return reservation is { } value && _service.TryConsume(value);
    }
}
