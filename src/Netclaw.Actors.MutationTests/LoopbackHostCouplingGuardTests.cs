// -----------------------------------------------------------------------
// <copyright file="LoopbackHostCouplingGuardTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;
using Xunit;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// The Host check must refuse every DNS name except "localhost", whatever the bind
/// validator accepts. These names resolve to loopback today (public DNS or /etc/hosts),
/// so they stand in for a rebinding name at the moment the page fetches. The mutation
/// run starts with these tests, so a widening of the shared predicate fails the gate.
/// </summary>
public sealed class LoopbackHostCouplingGuardTests
{
    [Theory]
    [InlineData("127.0.0.1.nip.io:5199")]
    [InlineData("localtest.me:5199")]
    [InlineData("ip6-localhost:5199")]
    public void A_dns_name_that_resolves_to_loopback_is_still_refused(string host)
        => Assert.NotNull(LoopbackHostOriginPolicy.FindRefusal(new HostString(host), StringValues.Empty));

    [Theory]
    [InlineData("localhost")]
    [InlineData("LocalHost")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]
    [InlineData("127.255.255.254")]
    [InlineData("::1")]
    public void The_strict_predicate_accepts_localhost_and_canonical_loopback_literals(string host)
        => Assert.True(LoopbackHostName.IsStrictLoopback(host));

    [Theory]
    [InlineData("")]
    [InlineData("localhost.")]
    [InlineData(" localhost")]
    [InlineData("localhost ")]
    [InlineData("localtest.me")]
    [InlineData("127.0.0.1.nip.io")]
    [InlineData("127.1")]
    [InlineData("127.000.000.001")]
    [InlineData("0x7f000001")]
    [InlineData("2130706433")]
    [InlineData("0.0.0.0")]
    [InlineData("[::1]")]
    [InlineData("0:0:0:0:0:0:0:1")]
    [InlineData("::1%0")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::")]
    [InlineData("10.0.0.1")]
    public void The_strict_predicate_refuses_names_and_noncanonical_literals(string host)
        => Assert.False(LoopbackHostName.IsStrictLoopback(host));

    [Theory]
    [InlineData("127.1")]
    [InlineData("[::1]")]
    [InlineData("0:0:0:0:0:0:0:1")]
    public void The_bind_validator_keeps_its_legacy_spellings_on_top_of_the_strict_predicate(string host)
    {
        Assert.True(DaemonExposureValidator.IsLoopbackHost(host));
        Assert.False(LoopbackHostName.IsStrictLoopback(host));
    }
}
