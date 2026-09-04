// -----------------------------------------------------------------------
// <copyright file="LocalControlPairingProofValidator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Daemon.Security;

/// <summary>
/// Validates one local-control proof and records its nonce before code generation.
/// The bounded process-local cache prevents proof replay.
/// </summary>
internal sealed class LocalControlPairingProofValidator
{
    internal static readonly TimeSpan ProofLifetime = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan FutureClockSkew = TimeSpan.FromSeconds(5);
    internal const int MaximumLiveNonces = 1_024;

    private readonly LocalControlPairingProofProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LocalControlPairingProofValidator> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _liveNonces = new(StringComparer.Ordinal);

    public LocalControlPairingProofValidator(
        LocalControlPairingProofProtector protector,
        TimeProvider timeProvider,
        ILogger<LocalControlPairingProofValidator> logger)
    {
        _protector = protector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    internal LocalControlPairingProofValidation ValidateAndConsume(string proof)
    {
        LocalControlPairingProofPayload payload;
        try
        {
            payload = _protector.Unprotect(proof);
        }
        catch (CryptographicException)
        {
            _logger.LogDebug("Rejected local-control proof: category=cryptographic-validation.");
            return LocalControlPairingProofValidation.Unauthorized;
        }

        if (payload.Version != LocalControlPairingProofProtector.CurrentVersion)
        {
            _logger.LogWarning("Rejected local-control proof: category=unsupported-version.");
            return LocalControlPairingProofValidation.UnsupportedVersion;
        }

        if (payload.Operation != LocalControlPairingProofProtector.GeneratePairingCodeOperation)
        {
            _logger.LogDebug("Rejected local-control proof: category=wrong-operation.");
            return LocalControlPairingProofValidation.Unauthorized;
        }

        var now = _timeProvider.GetUtcNow();
        if (payload.IssuedAt < now.Subtract(ProofLifetime)
            || payload.IssuedAt > now.Add(FutureClockSkew))
        {
            _logger.LogDebug("Rejected local-control proof: category=time-window.");
            return LocalControlPairingProofValidation.Unauthorized;
        }

        lock (_lock)
        {
            foreach (var expiredNonce in _liveNonces
                         .Where(entry => entry.Value < now)
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                _liveNonces.Remove(expiredNonce);
            }

            if (_liveNonces.ContainsKey(payload.Nonce))
            {
                _logger.LogWarning("Rejected local-control proof: category=replay.");
                return LocalControlPairingProofValidation.Unauthorized;
            }

            if (_liveNonces.Count >= MaximumLiveNonces)
            {
                _logger.LogError("Rejected local-control proof: category=replay-cache-capacity.");
                return LocalControlPairingProofValidation.CapacityExhausted;
            }

            _liveNonces.Add(payload.Nonce, payload.IssuedAt.Add(ProofLifetime));
        }

        return LocalControlPairingProofValidation.Valid;
    }
}

internal enum LocalControlPairingProofValidation
{
    Valid,
    Unauthorized,
    UnsupportedVersion,
    CapacityExhausted,
}
