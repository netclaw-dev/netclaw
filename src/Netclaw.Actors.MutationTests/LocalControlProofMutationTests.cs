// -----------------------------------------------------------------------
// <copyright file="LocalControlProofMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Security;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class LocalControlProofMutationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly LocalControlPairingProofProtector _protector = new(new EphemeralDataProtectionProvider());

    [Fact]
    public void State_bound_proof_is_valid_for_its_state()
    {
        var operation = LocalControlOperation.ReadProviderOAuthResult("state-a");
        var proof = _protector.CreateProof(operation, Now);

        Assert.Equal(LocalControlPairingProofValidation.Valid, CreateValidator().ValidateAndConsume(operation, proof));
    }

    [Fact]
    public void State_bound_proof_is_unauthorized_for_another_state()
    {
        // Only the state in the purpose chain separates these two operations.
        var proof = _protector.CreateProof(LocalControlOperation.ReadProviderOAuthResult("state-a"), Now);

        Assert.Equal(
            LocalControlPairingProofValidation.Unauthorized,
            CreateValidator().ValidateAndConsume(LocalControlOperation.ReadProviderOAuthResult("state-b"), proof));
    }

    [Fact]
    public void Matching_purpose_with_another_operation_byte_is_unauthorized()
    {
        // The purpose chain matches, so only the operation byte check can refuse this proof.
        var operation = LocalControlOperation.ReadProviderOAuthResult("state-a");
        var proof = _protector.ProtectPayload(operation, new LocalControlPairingProofPayload(
            LocalControlPairingProofProtector.CurrentVersion,
            LocalControlPairingProofProtector.GeneratePairingCodeOperation,
            Now,
            Convert.ToHexString(new byte[LocalControlPairingProofProtector.NonceSize])));

        Assert.Equal(LocalControlPairingProofValidation.Unauthorized, CreateValidator().ValidateAndConsume(operation, proof));
    }

    [Fact]
    public void Provider_oauth_result_operation_requires_a_state()
    {
        Assert.Throws<ArgumentException>(() => LocalControlOperation.ReadProviderOAuthResult(""));
    }

    private LocalControlPairingProofValidator CreateValidator()
        => new(_protector, new FixedTimeProvider(Now), NullLogger<LocalControlPairingProofValidator>.Instance);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
