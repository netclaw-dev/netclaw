// -----------------------------------------------------------------------
// <copyright file="MemoryTypedIdTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Memory;

namespace Netclaw.Actors.Tests.Memory;

public class MemoryTypedIdTests
{
    [Theory]
    [InlineData("doc:abc123", MemoryKind.Document, "abc123")]
    [InlineData("rec:xyz789", MemoryKind.Record, "xyz789")]
    [InlineData("DOC:upper", MemoryKind.Document, "upper")]
    [InlineData("REC:UPPER", MemoryKind.Record, "UPPER")]
    [InlineData("doc-abc123", MemoryKind.Document, "abc123")]
    [InlineData("rec-xyz789", MemoryKind.Record, "xyz789")]
    [InlineData("DOC-upper", MemoryKind.Document, "upper")]
    [InlineData("REC-UPPER", MemoryKind.Record, "UPPER")]
    public void Parse_accepts_both_colon_and_dash_prefixes(string raw, MemoryKind expectedKind, string expectedId)
    {
        var parsed = MemoryTypedId.Parse(raw);
        Assert.Equal(expectedKind, parsed.Kind);
        Assert.Equal(expectedId, parsed.Id);
    }

    [Fact]
    public void Parse_rejects_unrecognized_prefixes()
    {
        var parsed = MemoryTypedId.Parse("unknown-abc123");
        Assert.Equal(MemoryKind.Unknown, parsed.Kind);
        Assert.Equal("unknown-abc123", parsed.Id);
    }

    [Theory]
    [InlineData("doc:abc123")]
    [InlineData("rec:xyz789")]
    [InlineData("doc-bd5777c5860146aab6a5304310eb20c5")]
    [InlineData("rec-bd5777c5860146aab6a5304310eb20c5")]
    [InlineData("")]
    [InlineData("no-prefix")]
    public void Parse_unknown_for_invalid_prefixes(string raw)
    {
        var parsed = MemoryTypedId.Parse(raw);
        Assert.Equal(MemoryKind.Unknown, parsed.Kind);
    }

    [Fact]
    public void ToWireValue_returns_colon_format()
    {
        var doc = new MemoryTypedId(MemoryKind.Document, "abc123");
        var rec = new MemoryTypedId(MemoryKind.Record, "xyz789");
        var unknown = new MemoryTypedId(MemoryKind.Unknown, "orphan");

        Assert.Equal("doc:abc123", doc.ToWireValue());
        Assert.Equal("rec:xyz789", rec.ToWireValue());
        Assert.Equal("orphan", unknown.ToWireValue());
    }

    [Fact]
    public void ToString_matches_ToWireValue()
    {
        var id = new MemoryTypedId(MemoryKind.Document, "abc123");
        Assert.Equal("doc:abc123", id.ToString());
    }

    [Fact]
    public void NewDocumentId_returns_dash_format()
    {
        var id = MemoryTypedId.NewDocumentId();
        Assert.StartsWith("doc-", id);
        Assert.Equal(36 + 4, id.Length); // "doc-" + 32-char GUID (with dashes)
    }

    [Fact]
    public void NewRecordId_returns_dash_format()
    {
        var id = MemoryTypedId.NewRecordId();
        Assert.StartsWith("rec-", id);
        Assert.Equal(36 + 4, id.Length);
    }

    [Fact]
    public void Round_trip_dash_to_parse_to_wire()
    {
        // Simulates auto-recall output: agent receives "doc-{guid}"
        var generated = MemoryTypedId.NewDocumentId(); // e.g. "doc-bd5777c5860146aab6a5304310eb20c5"
        var parsed = MemoryTypedId.Parse(generated);
        var wire = parsed.ToWireValue(); // e.g. "doc:bd5777c5860146aab6a5304310eb20c5"

        Assert.Equal(MemoryKind.Document, parsed.Kind);
        Assert.Contains("bd5777c", wire); // ID portion preserved
        Assert.StartsWith("doc:", wire); // wire uses colon
    }

    [Fact]
    public void Round_trip_wire_to_parse_to_string()
    {
        // Simulates find_memories output: agent receives "doc:{guid}"
        var wire = "doc:abc123";
        var parsed = MemoryTypedId.Parse(wire);
        var output = parsed.ToString();

        Assert.Equal(MemoryKind.Document, parsed.Kind);
        Assert.Equal("doc:abc123", output);
    }
}
