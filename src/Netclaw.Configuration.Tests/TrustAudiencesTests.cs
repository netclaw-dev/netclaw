// -----------------------------------------------------------------------
// <copyright file="TrustAudiencesTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class TrustAudiencesTests
{
    /// <summary>
    /// The canonical "all audiences" enumeration MUST cover every value
    /// declared on <see cref="TrustAudience"/>. If this assertion ever fires,
    /// it means someone added an audience and the security-relevant
    /// "iterate every audience" call sites (e.g., unscoped revoke) would
    /// silently skip it.
    /// </summary>
    [Fact]
    public void All_covers_every_TrustAudience_enum_value()
    {
        var declared = Enum.GetValues<TrustAudience>();
        Assert.Equal(declared.Length, TrustAudiences.All.Length);
        foreach (var value in declared)
            Assert.Contains(value, TrustAudiences.All);
    }
}
