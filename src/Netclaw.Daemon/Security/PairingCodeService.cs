// -----------------------------------------------------------------------
// <copyright file="PairingCodeService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;

namespace Netclaw.Daemon.Security;

/// <summary>
/// In-memory store for a single pending device pairing code.
///
/// <para>
/// Lifecycle:
/// <list type="bullet">
///   <item>The host CLI creates codes through the local-control endpoint.</item>
///   <item>Only one pending code exists at a time; generating a new code replaces the previous one.</item>
///   <item>Codes expire after 5 minutes and are consumed on first successful exchange.</item>
/// </list>
/// </para>
///
/// <para>Code format: 8 characters drawn from <c>23456789ABCDEFGHJKLMNPQRSTUVWXYZ</c>
/// (no ambiguous 0/O/1/I characters), displayed as <c>XXXX-XXXX</c>.</para>
/// </summary>
internal sealed class PairingCodeService
{
    private static readonly char[] Alphabet =
        "23456789ABCDEFGHJKLMNPQRSTUVWXYZ".ToCharArray();

    private static readonly TimeSpan CodeTtl = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private long _generation;
    private PendingCode? _pending;

    public PairingCodeService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Generates a new pairing code, replacing any previously pending code.
    /// </summary>
    /// <returns>The formatted code (<c>XXXX-XXXX</c>) and its expiration time.</returns>
    internal (string FormattedCode, DateTimeOffset ExpiresAt) GenerateCode()
    {
        // The 32-symbol alphabet divides the byte range evenly, so modulo mapping has no bias.
        var rawBytes = RandomNumberGenerator.GetBytes(8);
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < 8; i++)
            chars[i] = Alphabet[rawBytes[i] % Alphabet.Length];

        var rawCode = new string(chars);
        var formatted = $"{rawCode[..4]}-{rawCode[4..]}";
        var expiresAt = _timeProvider.GetUtcNow().Add(CodeTtl);

        lock (_lock)
        {
            _pending = new PendingCode(rawCode, expiresAt, checked(++_generation));
        }

        return (formatted, expiresAt);
    }

    /// <summary>
    /// Reserves the pending code after one validity check.
    /// </summary>
    /// <param name="presentedCode">Code as presented by the remote client (with or without dash).</param>
    /// <returns>A reservation for the active code generation, or <c>null</c> when validation fails.</returns>
    internal PairingCodeReservation? TryReserve(string presentedCode)
    {
        lock (_lock)
        {
            if (!IsValidLocked(presentedCode))
                return null;

            return new PairingCodeReservation(_pending!.Generation);
        }
    }

    /// <summary>
    /// Consumes the exact code generation from a prior successful validity check.
    /// The coordinator calls this only after the durable device write succeeds.
    /// </summary>
    internal bool TryConsume(PairingCodeReservation reservation)
    {
        lock (_lock)
        {
            if (_pending is null || _pending.Generation != reservation.Generation)
                return false;

            _pending = null;
            return true;
        }
    }

    /// <summary>
    /// Returns the expiration time of the currently pending code,
    /// or <c>null</c> if no code is pending (or the code has expired).
    /// </summary>
    internal DateTimeOffset? GetPendingExpiry()
    {
        lock (_lock)
        {
            if (_pending is null)
                return null;

            var now = _timeProvider.GetUtcNow();
            if (now >= _pending.ExpiresAt)
            {
                _pending = null;
                return null;
            }

            return _pending.ExpiresAt;
        }
    }

    private bool IsValidLocked(string presentedCode)
    {
        if (_pending is null)
            return false;

        if (_timeProvider.GetUtcNow() >= _pending.ExpiresAt)
        {
            _pending = null;
            return false;
        }

        var normalized = presentedCode.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        return string.Equals(normalized, _pending.RawCode, StringComparison.Ordinal);
    }

    private sealed record PendingCode(string RawCode, DateTimeOffset ExpiresAt, long Generation);
}

internal readonly record struct PairingCodeReservation(long Generation);
