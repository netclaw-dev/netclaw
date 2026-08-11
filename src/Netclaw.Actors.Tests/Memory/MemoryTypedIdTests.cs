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
    public void ToString_returns_storage_id_verbatim()
    {
        var id = new MemoryTypedId(MemoryKind.Document, "doc-abc123");
        Assert.Equal("doc-abc123", id.ToString());
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
    public void Generated_storage_id_round_trips_to_the_same_key()
    {
        // The id we surface to the model is the storage id verbatim; parsing what the model
        // sends back must yield the exact same primary key.
        var generated = MemoryTypedId.NewDocumentId();
        var parsed = MemoryTypedId.Parse(generated.Value);

        Assert.Equal(MemoryKind.Document, parsed.Kind);
        Assert.Equal(generated.Value, parsed.Id.Value);
    }

    [Fact]
    public void Legacy_colon_envelope_resolves_to_the_same_key_as_the_dash_id()
    {
        // Both the bare storage id and a legacy "doc:{storageId}" envelope must map to the
        // one real key — this is what makes the single-lookup resolver unambiguous.
        var dash = MemoryTypedId.Parse("doc-abc123");
        var enveloped = MemoryTypedId.Parse("doc:doc-abc123");

        Assert.Equal("doc-abc123", dash.Id.Value);
        Assert.Equal("doc-abc123", enveloped.Id.Value);
        Assert.Equal(dash.Id.Value, enveloped.Id.Value);
    }
}
