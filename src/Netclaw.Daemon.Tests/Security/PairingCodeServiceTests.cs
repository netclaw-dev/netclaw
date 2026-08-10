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

        // First code should no longer be consumable after replacement
        Assert.False(_service.TryConsume(first));
        Assert.True(_service.TryConsume(second));
    }

    [Fact]
    public void GenerateCode_produces_codes_only_from_reduced_alphabet()
    {
        // Generate many codes and verify no ambiguous characters appear
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

        Assert.True(_service.TryConsume(formatted));
    }

    [Fact]
    public void TryConsume_returns_true_for_valid_code_without_dash()
    {
        var (formatted, _) = _service.GenerateCode();
        var noDash = formatted.Replace("-", "");

        Assert.True(_service.TryConsume(noDash));
    }

    [Fact]
    public void TryConsume_is_case_insensitive()
    {
        var (formatted, _) = _service.GenerateCode();

        Assert.True(_service.TryConsume(formatted.ToLowerInvariant()));
    }

    [Fact]
    public void TryConsume_is_single_use()
    {
        var (formatted, _) = _service.GenerateCode();

        Assert.True(_service.TryConsume(formatted));
        Assert.False(_service.TryConsume(formatted));
    }

    [Fact]
    public void TryConsume_returns_false_when_no_code_pending()
    {
        Assert.False(_service.TryConsume("ABCD-1234"));
    }

    [Fact]
    public void TryConsume_returns_false_for_wrong_code()
    {
        _service.GenerateCode();

        // Try an obviously wrong code
        Assert.False(_service.TryConsume("ZZZZ-ZZZZ"));
    }

    [Fact]
    public void TryConsume_returns_false_after_code_expires()
    {
        var (formatted, _) = _service.GenerateCode();

        // Advance past 5-minute TTL
        _time.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));

        Assert.False(_service.TryConsume(formatted));
    }

    [Fact]
    public void TryConsume_returns_true_just_before_expiry()
    {
        var (formatted, _) = _service.GenerateCode();

        // Advance to just inside the TTL (1 second before expiry)
        _time.Advance(TimeSpan.FromMinutes(5).Subtract(TimeSpan.FromSeconds(1)));

        Assert.True(_service.TryConsume(formatted));
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
        _service.TryConsume(formatted);

        Assert.Null(_service.GetPendingExpiry());
    }

    [Fact]
    public void GetPendingExpiry_returns_null_after_code_expires()
    {
        _service.GenerateCode();
        _time.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));

        Assert.Null(_service.GetPendingExpiry());
    }
}
