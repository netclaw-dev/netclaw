// -----------------------------------------------------------------------
// <copyright file="TrustBoundaryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class TrustBoundaryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_rejects_empty_or_whitespace(string value)
    {
        Assert.Throws<ArgumentException>(() => new TrustBoundary(value));
    }

    [Fact]
    public void Constructor_trims_the_value()
    {
        Assert.Equal("boundary:custom", new TrustBoundary("  boundary:custom  ").Value);
    }

    [Fact]
    public void Named_factories_carry_the_canonical_wire_strings()
    {
        Assert.Equal("boundary:public", TrustBoundary.Public.Value);
        Assert.Equal("boundary:team", TrustBoundary.Team.Value);
        Assert.Equal("boundary:personal", TrustBoundary.Personal.Value);
        Assert.Equal("boundary:trusted-instance", TrustBoundary.TrustedInstance.Value);
        Assert.Equal("boundary:legacy-restricted", TrustBoundary.LegacyRestricted.Value);
    }

    [Fact]
    public void Equality_is_value_based()
    {
        Assert.Equal(TrustBoundary.Team, new TrustBoundary("boundary:team"));
        Assert.NotEqual(TrustBoundary.Team, TrustBoundary.Public);
    }

    [Fact]
    public void Json_converter_round_trips_as_a_bare_primitive_string()
    {
        var options = new JsonSerializerOptions { Converters = { new TrustBoundaryJsonConverter() } };

        var json = JsonSerializer.Serialize(TrustBoundary.Personal, options);

        // The on-disk representation MUST be the bare string, not a nested
        // object — the value object is an in-memory gate, not a wire change.
        Assert.Equal("\"boundary:personal\"", json);

        var roundTripped = JsonSerializer.Deserialize<TrustBoundary>(json, options);
        Assert.Equal(TrustBoundary.Personal, roundTripped);
    }
}
