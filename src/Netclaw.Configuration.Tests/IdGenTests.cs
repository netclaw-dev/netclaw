using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class IdGenTests
{
    [Fact]
    public void AlertId_returns_12_hex_characters()
    {
        var id = IdGen.AlertId();
        Assert.Equal(12, id.Length);
        Assert.Matches("^[0-9a-f]{12}$", id);
    }

    [Fact]
    public void ShortId_returns_8_hex_characters()
    {
        var id = IdGen.ShortId();
        Assert.Equal(8, id.Length);
        Assert.Matches("^[0-9a-f]{8}$", id);
    }

    [Fact]
    public void Suffix_returns_6_hex_characters()
    {
        var id = IdGen.Suffix();
        Assert.Equal(6, id.Length);
        Assert.Matches("^[0-9a-f]{6}$", id);
    }

    [Fact]
    public void Full_returns_32_hex_characters()
    {
        var id = IdGen.Full();
        Assert.Equal(32, id.Length);
        Assert.Matches("^[0-9a-f]{32}$", id);
    }

    [Fact]
    public void Successive_calls_produce_unique_values()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => IdGen.AlertId()).ToHashSet();
        Assert.Equal(100, ids.Count);
    }
}
