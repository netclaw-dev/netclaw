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
    [InlineData("doc-abc123", MemoryKind.Document, "doc-abc123")]
    [InlineData("rec-xyz789", MemoryKind.Record, "rec-xyz789")]
    [InlineData("DOC-upper", MemoryKind.Document, "DOC-upper")]
    [InlineData("REC-UPPER", MemoryKind.Record, "REC-UPPER")]
    public void Parse_accepts_canonical_and_legacy_raw_ids(string raw, MemoryKind expectedKind, string expectedId)
    {
        var parsed = MemoryTypedId.Parse(raw);
        Assert.Equal(expectedKind, parsed.Kind);
        Assert.Equal(expectedId, parsed.Id.Value);
    }

    [Fact]
    public void Parse_rejects_unrecognized_prefixes()
    {
        var parsed = MemoryTypedId.Parse("unknown-abc123");
        Assert.Equal(MemoryKind.Unknown, parsed.Kind);
        Assert.Equal("unknown-abc123", parsed.Id.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-prefix")]
    [InlineData("anchor:netclaw")]
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
        Assert.StartsWith("doc-", id.Value);
        Assert.Equal(36, id.Value.Length);
    }

    [Fact]
    public void NewRecordId_returns_dash_format()
    {
        var id = MemoryTypedId.NewRecordId();
        Assert.StartsWith("rec-", id.Value);
        Assert.Equal(36, id.Value.Length);
    }

    [Fact]
    public void Round_trip_dash_to_parse_to_wire()
    {
        var generated = MemoryTypedId.NewDocumentId();
        var parsed = MemoryTypedId.Parse(generated.Value);
        var wire = parsed.ToWireValue();

        Assert.Equal(MemoryKind.Document, parsed.Kind);
        Assert.Equal($"doc:{generated.Value}", wire);
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

    [Fact]
    public void CandidateStorageIds_include_legacy_prefixed_candidate_for_bare_handle_payload()
    {
        var parsed = MemoryTypedId.Parse("doc:abc123");
        var candidates = parsed.CandidateStorageIds().Select(x => x.Value).ToArray();

        Assert.Equal(["abc123", "doc-abc123"], candidates);
    }

    [Fact]
    public void CandidateStorageIds_preserve_legacy_raw_storage_id()
    {
        var parsed = MemoryTypedId.Parse("doc-abc123");
        var candidates = parsed.CandidateStorageIds().Select(x => x.Value).ToArray();

        Assert.Equal(["doc-abc123"], candidates);
    }
}
