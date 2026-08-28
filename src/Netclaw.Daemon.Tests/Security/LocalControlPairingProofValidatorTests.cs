// -----------------------------------------------------------------------
// <copyright file="LocalControlPairingProofValidatorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Security;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Security;

public sealed class LocalControlPairingProofValidatorTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly FakeTimeProvider _time = new(
        new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly LocalControlPairingProofProtector _protector;
    private readonly LocalControlPairingProofValidator _validator;

    public LocalControlPairingProofValidatorTests()
    {
        var provider = SecretsProtection.CreateDataProtectionProvider(new NetclawPaths(_dir.Path));
        _protector = new LocalControlPairingProofProtector(provider);
        _validator = new LocalControlPairingProofValidator(
            _protector,
            _time,
            NullLogger<LocalControlPairingProofValidator>.Instance);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Fresh_proof_is_valid_once()
    {
        var proof = _protector.CreateProof(_time.GetUtcNow());

        Assert.Equal(LocalControlPairingProofValidation.Valid, _validator.ValidateAndConsume(proof));
        Assert.Equal(LocalControlPairingProofValidation.Unauthorized, _validator.ValidateAndConsume(proof));
    }

    [Theory]
    [InlineData(-31, (int)LocalControlPairingProofValidation.Unauthorized)]
    [InlineData(-30, (int)LocalControlPairingProofValidation.Valid)]
    [InlineData(5, (int)LocalControlPairingProofValidation.Valid)]
    [InlineData(6, (int)LocalControlPairingProofValidation.Unauthorized)]
    public void Time_window_is_deterministic(
        int issueOffsetSeconds,
        int expected)
    {
        var proof = _protector.CreateProof(_time.GetUtcNow().AddSeconds(issueOffsetSeconds));

        Assert.Equal((LocalControlPairingProofValidation)expected, _validator.ValidateAndConsume(proof));
    }

    [Fact]
    public void Unsupported_version_has_distinct_result()
    {
        var proof = _protector.ProtectPayload(new LocalControlPairingProofPayload(
            Version: 2,
            Operation: LocalControlPairingProofProtector.GeneratePairingCodeOperation,
            IssuedAt: _time.GetUtcNow(),
            Nonce: Convert.ToHexString(new byte[LocalControlPairingProofProtector.NonceSize])));

        Assert.Equal(
            LocalControlPairingProofValidation.UnsupportedVersion,
            _validator.ValidateAndConsume(proof));
    }

    [Fact]
    public void Wrong_operation_is_unauthorized()
    {
        var proof = _protector.ProtectPayload(new LocalControlPairingProofPayload(
            Version: LocalControlPairingProofProtector.CurrentVersion,
            Operation: 2,
            IssuedAt: _time.GetUtcNow(),
            Nonce: Convert.ToHexString(new byte[LocalControlPairingProofProtector.NonceSize])));

        Assert.Equal(LocalControlPairingProofValidation.Unauthorized, _validator.ValidateAndConsume(proof));
    }

    [Fact]
    public void Proof_from_another_home_is_unauthorized()
    {
        using var otherDir = new DisposableTempDir();
        var otherProvider = SecretsProtection.CreateDataProtectionProvider(new NetclawPaths(otherDir.Path));
        var otherProtector = new LocalControlPairingProofProtector(otherProvider);
        var proof = otherProtector.CreateProof(_time.GetUtcNow());

        Assert.Equal(LocalControlPairingProofValidation.Unauthorized, _validator.ValidateAndConsume(proof));
    }

    [Fact]
    public void Full_replay_cache_fails_closed_then_recovers_after_expiry()
    {
        for (var index = 0; index < LocalControlPairingProofValidator.MaximumLiveNonces; index++)
        {
            var proof = _protector.CreateProof(_time.GetUtcNow());
            Assert.Equal(LocalControlPairingProofValidation.Valid, _validator.ValidateAndConsume(proof));
        }

        var rejected = _protector.CreateProof(_time.GetUtcNow());
        Assert.Equal(
            LocalControlPairingProofValidation.CapacityExhausted,
            _validator.ValidateAndConsume(rejected));

        _time.Advance(
            LocalControlPairingProofValidator.ProofLifetime
            + LocalControlPairingProofValidator.FutureClockSkew
            + TimeSpan.FromMilliseconds(1));
        var fresh = _protector.CreateProof(_time.GetUtcNow());

        Assert.Equal(LocalControlPairingProofValidation.Valid, _validator.ValidateAndConsume(fresh));
    }
}
