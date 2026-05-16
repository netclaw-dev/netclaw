// -----------------------------------------------------------------------
// <copyright file="ClaimsPrincipalMapperTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Claims;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class ClaimsPrincipalMapperTests
{
    private readonly ClaimsPrincipalMapper _mapper = new();

    [Fact]
    public void Map_null_returns_untrusted_external_unknown()
    {
        var result = _mapper.Map(null);

        Assert.Equal(PrincipalClassification.UntrustedExternal, result.Principal);
        Assert.Equal(TransportAuthenticity.Unknown, result.Transport);
        Assert.Equal("unknown", result.SenderId.Value);
    }

    [Fact]
    public void Map_loopback_claims_returns_operator_local_process()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(NetclawClaimTypes.PrincipalClassification, nameof(PrincipalClassification.Operator)),
            new Claim(NetclawClaimTypes.TransportAuthenticity, nameof(TransportAuthenticity.LocalProcess)),
            new Claim(NetclawClaimTypes.DeviceId, "local")
        ], "loopback");
        var principal = new ClaimsPrincipal(identity);

        var result = _mapper.Map(principal);

        Assert.Equal(PrincipalClassification.Operator, result.Principal);
        Assert.Equal(TransportAuthenticity.LocalProcess, result.Transport);
        Assert.Equal("local", result.SenderId.Value);
    }

    [Fact]
    public void Map_bearer_claims_returns_operator_verified_with_device_name()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(NetclawClaimTypes.PrincipalClassification, nameof(PrincipalClassification.Operator)),
            new Claim(NetclawClaimTypes.TransportAuthenticity, nameof(TransportAuthenticity.Verified)),
            new Claim(NetclawClaimTypes.DeviceId, "my-laptop")
        ], "bearer");
        var principal = new ClaimsPrincipal(identity);

        var result = _mapper.Map(principal);

        Assert.Equal(PrincipalClassification.Operator, result.Principal);
        Assert.Equal(TransportAuthenticity.Verified, result.Transport);
        Assert.Equal("my-laptop", result.SenderId.Value);
    }

    [Fact]
    public void Map_missing_claims_falls_back_to_untrusted_external_unknown()
    {
        var identity = new ClaimsIdentity([], "some-scheme");
        var principal = new ClaimsPrincipal(identity);

        var result = _mapper.Map(principal);

        Assert.Equal(PrincipalClassification.UntrustedExternal, result.Principal);
        Assert.Equal(TransportAuthenticity.Unknown, result.Transport);
        Assert.Equal("unknown", result.SenderId.Value);
    }

    [Fact]
    public void Map_unrecognised_claim_values_fall_back_per_claim()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(NetclawClaimTypes.PrincipalClassification, "bogus-principal"),
            new Claim(NetclawClaimTypes.TransportAuthenticity, "bogus-transport"),
            new Claim(NetclawClaimTypes.DeviceId, "my-device")
        ], "some-scheme");
        var principal = new ClaimsPrincipal(identity);

        var result = _mapper.Map(principal);

        Assert.Equal(PrincipalClassification.UntrustedExternal, result.Principal);
        Assert.Equal(TransportAuthenticity.Unknown, result.Transport);
        Assert.Equal("my-device", result.SenderId.Value);
    }
}
