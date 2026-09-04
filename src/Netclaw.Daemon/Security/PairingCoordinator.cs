// -----------------------------------------------------------------------
// <copyright file="PairingCoordinator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Security;

/// <summary>
/// Serializes pairing code generation and device exchange state transitions.
/// The coordinator consumes a code only after the device registry accepts the device.
/// </summary>
internal sealed class PairingCoordinator
{
    private readonly PairingCodeService _pairingCodeService;
    private readonly DeviceRegistry _deviceRegistry;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PairingCoordinator> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public PairingCoordinator(
        PairingCodeService pairingCodeService,
        DeviceRegistry deviceRegistry,
        TimeProvider timeProvider,
        ILogger<PairingCoordinator> logger)
    {
        _pairingCodeService = pairingCodeService;
        _deviceRegistry = deviceRegistry;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    internal async Task<PairingCodeResultDto> GenerateCodeAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var (formattedCode, expiresAt) = _pairingCodeService.GenerateCode();
            _logger.LogInformation("Generated a host pairing code with expiration {ExpiresAt:o}.", expiresAt);
            return new PairingCodeResultDto(formattedCode, expiresAt);
        }
        finally
        {
            _lock.Release();
        }
    }

    internal async Task<PairingExchangeResult> ExchangeAsync(
        string presentedCode,
        string deviceName,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_pairingCodeService.GetPendingExpiry() is null)
                return PairingExchangeResult.NoCode();

            var reservation = _pairingCodeService.TryReserve(presentedCode);
            if (reservation is null)
                return PairingExchangeResult.InvalidCode();

            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            var rawToken = Base64Url.EncodeToString(tokenBytes);
            var saltBytes = RandomNumberGenerator.GetBytes(16);
            var saltHex = Convert.ToHexString(saltBytes).ToLowerInvariant();
            var now = _timeProvider.GetUtcNow();
            var device = new PairedDevice
            {
                Name = deviceName.Trim(),
                TokenHash = PairedDevice.ComputeTokenHash(rawToken, saltHex),
                Salt = saltHex,
                CreatedAt = now,
                LastUsedAt = now,
            };

            try
            {
                await _deviceRegistry.AddAsync(device, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return PairingExchangeResult.DuplicateName(ex.Message);
            }

            if (!_pairingCodeService.TryConsume(reservation.Value))
            {
                throw new InvalidOperationException(
                    "The reserved pairing code changed after the device registry write.");
            }

            return PairingExchangeResult.Success(rawToken);
        }
        finally
        {
            _lock.Release();
        }
    }
}

internal sealed record PairingExchangeResult
{
    private PairingExchangeResult(PairingExchangeStatus status, string? token, string? error)
    {
        Status = status;
        Token = token;
        Error = error;
    }

    internal PairingExchangeStatus Status { get; }

    internal string? Token { get; }

    internal string? Error { get; }

    internal static PairingExchangeResult Success(string token) =>
        new(PairingExchangeStatus.Success, token, null);

    internal static PairingExchangeResult NoCode() =>
        new(PairingExchangeStatus.NoCode, null, null);

    internal static PairingExchangeResult InvalidCode() =>
        new(PairingExchangeStatus.InvalidCode, null, null);

    internal static PairingExchangeResult DuplicateName(string error) =>
        new(PairingExchangeStatus.DuplicateName, null, error);
}

internal enum PairingExchangeStatus
{
    Success,
    NoCode,
    InvalidCode,
    DuplicateName,
}
