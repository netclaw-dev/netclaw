// -----------------------------------------------------------------------
// <copyright file="NetclawUserAgentTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class NetclawUserAgentTests
{
    [Fact]
    public void Value_matches_BuildInfo_reconstruction()
    {
        // Pin the exact format so a refactor that drops the sha segment,
        // hardcodes the version, or wires up the wrong assembly fails here
        // instead of silently shipping a malformed UA. Shape-only assertions
        // (StartsWith("Netclaw/"), Contains("sha=")) pass even when the
        // version segment is testhost's "17.x" or "unknown", which is the
        // exact regression class this test exists to catch.
        var expected = $"Netclaw/{BuildInfo.Version} (+https://netclaw.dev; sha={BuildInfo.CommitHash})";
        Assert.Equal(expected, NetclawUserAgent.Value);
    }

    [Fact]
    public void Component_header_name_is_X_Netclaw_Component()
    {
        // Pinned so server-side allowlists / rate-limit rules keyed on the
        // exact string do not silently break under a rename.
        Assert.Equal("X-Netclaw-Component", NetclawUserAgent.ComponentHeader);
    }
}
