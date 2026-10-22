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
        var proof = _protector.CreateProof(LocalControlOperation.GeneratePairingCode, _time.GetUtcNow());

        Assert.Equal(LocalControlPairingProofValidation.Valid, _validator.ValidateAndConsume(LocalControlOperation.GeneratePairingCode, proof));
        Assert.Equal(LocalControlPairingProofValidation.Unauthorized, _validator.ValidateAndConsume(LocalControlOperation.GeneratePairingCode, proof));
    }

    [Fact]
    public void Boundary_proof_replay_is_rejected_then_nonce_expires()
    {
        const string nonce = "00112233445566778899AABBCCDDEEFF";
        var boundaryProof = _protector.ProtectPayload(LocalControlOperation.GeneratePairingCode, new LocalControlPairingProofPayload(
            LocalControlPairingProofProtector.CurrentVersion,
            LocalControlPairingProofProtector.GeneratePairingCodeOperation,
            _time.GetUtcNow().Subtract(LocalControlPairingProofValidator.ProofLifetime),
            nonce));
        var freshProof = _protector.ProtectPayload(LocalControlOperation.GeneratePairingCode, new LocalControlPairingProofPayload(
            LocalControlPairingProofProtector.CurrentVersion,
            LocalControlPairingProofProtector.GeneratePairingCodeOperation,
            _time.GetUtcNow(),
            nonce));

        Assert.Equal(LocalControlPairingProofValidation.Valid, _validator.ValidateAndConsume(LocalControlOperation.GeneratePairingCode, boundaryProof));
        Assert.Equal(LocalControlPairingProofValidation.Unauthorized, _validator.ValidateAndConsume(LocalControlOperation.GeneratePairingCode, boundaryProof));
        Assert.Equal(LocalControlPairingProofValidation.Unauthorized, _validator.ValidateAndConsume(LocalControlOperation.GeneratePairingCode, freshProof));

        _time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(LocalControlPairingProofValidation.Valid, _validator.ValidateAndConsume(LocalControlOperation.GeneratePairingCode, freshProof));
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
        var proof = _protector.CreateProof(LocalControlOperation.GeneratePairingCode, _time.GetUtcNow().AddSeconds(issueOffsetSeconds));

        Assert.Equal((LocalControlPairingProofValidation)expected, _validator.ValidateAndConsume(LocalControlOperation.GeneratePairingCode, proof));
    }

    [Fact]
    public void Unsupported_version_has_distinct_result()
    {
        var proof = _protector.ProtectPayload(LocalControlOperation.GeneratePairingCode, new LocalControlPairingProofPayload(
            Version: 2,
            Operation: LocalControlPairingProofProtector.GeneratePairingCodeOperation,
            IssuedAt: _time.GetUtcNow(),
            Nonce: Convert.ToHexString(new byte[LocalControlPairingProofProtector.NonceSize])));

        Assert.Equal(
            LocalControlPairingProofValidation.UnsupportedVersion,
            _validator.ValidateAndConsume(LocalControlOperation.GeneratePairingCode, proof));
    }

    [Fact]
    public void Wrong_operation_is_unauthorized()
    {
        var proof = _protector.ProtectPayload(LocalControlOperation.GeneratePairingCode, new LocalControlPairingProofPayload(
            Version: LocalControlPairingProofProtector.CurrentVersion,
            Operation: 2,
            IssuedAt: _time.GetUtcNow(),
            Nonce: Convert.ToHexString(new byte[LocalControlPairingProofProtector.NonceSize])));

        Assert.Equal(LocalControlPairingProofValidation.Unauthorized, _validator.ValidateAndConsume(LocalControlOperation.GeneratePairingCode, proof));
    }

    [Fact]
    public void Provider_oauth_result_proof_is_valid_once_for_its_state()
    {
        var operation = LocalControlOperation.ReadProviderOAuthResult("state-a");
        var proof = _protector.CreateProof(operation, _time.GetUtcNow());

        Assert.Equal(LocalControlPairingProofValidation.Valid, _validator.ValidateAndConsume(operation, proof));
        Assert.Equal(LocalControlPairingProofValidation.Unauthorized, _validator.ValidateAndConsume(operation, proof));
    }

    [Fact]
    public void Provider_oauth_result_proof_is_unauthorized_for_another_state()
    {
        var proof = _protector.CreateProof(LocalControlOperation.ReadProviderOAuthResult("state-a"), _time.GetUtcNow());

        Assert.Equal(
            LocalControlPairingProofValidation.Unauthorized,
            _validator.ValidateAndConsume(LocalControlOperation.ReadProviderOAuthResult("state-b"), proof));
    }

    [Fact]
    public void Proof_for_one_operation_is_unauthorized_for_the_other()
    {
        var pairingProof = _protector.CreateProof(LocalControlOperation.GeneratePairingCode, _time.GetUtcNow());
        var oauthOperation = LocalControlOperation.ReadProviderOAuthResult("state-a");
        var oauthProof = _protector.CreateProof(oauthOperation, _time.GetUtcNow());

        Assert.Equal(
            LocalControlPairingProofValidation.Unauthorized,
            _validator.ValidateAndConsume(oauthOperation, pairingProof));
        Assert.Equal(
            LocalControlPairingProofValidation.Unauthorized,
            _validator.ValidateAndConsume(LocalControlOperation.GeneratePairingCode, oauthProof));
    }

    [Fact]
    public void Provider_oauth_result_purpose_with_pairing_operation_byte_is_unauthorized()
    {
        // The purpose chain matches, so only the operation byte check can refuse this proof.
        var operation = LocalControlOperation.ReadProviderOAuthResult("state-a");
        var proof = _protector.ProtectPayload(operation, new LocalControlPairingProofPayload(
            Version: LocalControlPairingProofProtector.CurrentVersion,
            Operation: LocalControlPairingProofProtector.GeneratePairingCodeOperation,
            IssuedAt: _time.GetUtcNow(),
            Nonce: Convert.ToHexString(new byte[LocalControlPairingProofProtector.NonceSize])));

        Assert.Equal(LocalControlPairingProofValidation.Unauthorized, _validator.ValidateAndConsume(operation, proof));
    }

    [Fact]
    public void Provider_oauth_result_operation_requires_a_state()
    {
        Assert.Throws<ArgumentException>(() => LocalControlOperation.ReadProviderOAuthResult(""));
    }

    [Fact]
    public void Proof_from_another_home_is_unauthorized()
    {
        using var otherDir = new DisposableTempDir();
        var otherProvider = SecretsProtection.CreateDataProtectionProvider(new NetclawPaths(otherDir.Path));
        var otherProtector = new LocalControlPairingProofProtector(otherProvider);
        var proof = otherProtector.CreateProof(LocalControlOperation.GeneratePairingCode, _time.GetUtcNow());

        Assert.Equal(LocalControlPairingProofValidation.Unauthorized, _validator.ValidateAndConsume(LocalControlOperation.GeneratePairingCode, proof));
    }

    [Fact]
    public void Full_replay_cache_fails_closed_then_recovers_after_expiry()
    {
        for (var index = 0; index < LocalControlPairingProofValidator.MaximumLiveNonces; index++)
        {
            var proof = _protector.CreateProof(LocalControlOperation.GeneratePairingCode, _time.GetUtcNow());
            Assert.Equal(LocalControlPairingProofValidation.Valid, _validator.ValidateAndConsume(LocalControlOperation.GeneratePairingCode, proof));
        }

        var rejected = _protector.CreateProof(LocalControlOperation.GeneratePairingCode, _time.GetUtcNow());
        Assert.Equal(
            LocalControlPairingProofValidation.CapacityExhausted,
            _validator.ValidateAndConsume(LocalControlOperation.GeneratePairingCode, rejected));

        _time.Advance(
            LocalControlPairingProofValidator.ProofLifetime
            + LocalControlPairingProofValidator.FutureClockSkew
            + TimeSpan.FromMilliseconds(1));
        var fresh = _protector.CreateProof(LocalControlOperation.GeneratePairingCode, _time.GetUtcNow());

        Assert.Equal(LocalControlPairingProofValidation.Valid, _validator.ValidateAndConsume(LocalControlOperation.GeneratePairingCode, fresh));
    }
}
