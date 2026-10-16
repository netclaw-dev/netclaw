// -----------------------------------------------------------------------
// <copyright file="CoordinationV2CorpusTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Guards the v2 wire corpus that <see cref="DeployedV2JournalReplayTests"/> replays. The corpus came from the writers at
/// c4ed4dcc6, and S2 deletes those writers, so the corpus cannot be made again. This spec reads the raw protobuf tags.
/// The tag numbers are the wire contract, and they stay valid after the writers go. A corpus that lost a deployed shape
/// would let the replay specs pass without proof.
/// </summary>
public sealed class CoordinationV2CorpusTests
{
    private const string Revision = "c4ed4dcc6a2165d92d074dfd6cf603d2873bca13";

    [Fact]
    public void The_corpus_holds_every_deployed_v2_shape_that_the_reader_must_keep()
    {
        // Arrange
        var corpus = JsonSerializer.Deserialize<Corpus>(File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "CoordinationV2", "v2-writer-records.json")))!;
        var rows = corpus.Records.ToDictionary(record => record.Name, StringComparer.Ordinal);

        // Assert: the corpus is intact and comes from the pinned writers.
        Assert.Equal(Revision, corpus.SourceRevision);
        Assert.Equal(150, corpus.SerializerIdentifier);
        Assert.All(corpus.Records, record => Assert.Equal(
            record.Sha256, Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(record.Base64))).ToLowerInvariant()));

        // cre-v2: a ResultPrepared row (oneof case 7 of ChildRunEventV2Proto) and a TerminalRecorded row (case 6).
        var cre = corpus.Records.Where(record => record.Manifest == "cre-v2").ToArray();
        Assert.Contains(cre, record => Tags(record).Contains(7));
        Assert.Contains(cre, record => Tags(record).Contains(6));

        // tr-v1: a TurnRecorded row with child_continuation (field 8). tbs-v1: a ToolBatchStarted row with it (field 11).
        Assert.Contains(corpus.Records.Where(record => record.Manifest == "tr-v1"), record => Tags(record).Contains(8));
        Assert.Contains(corpus.Records.Where(record => record.Manifest == "tbs-v1"), record => Tags(record).Contains(11));

        // ss-v2: one snapshot for each deployed lifecycle case: prepared 17, consumed 18, closed 19.
        foreach (var (name, lifecycleCase) in new[] { ("snapshot-prepared", 17), ("snapshot-consumed", 18), ("snapshot-closed", 19) })
        {
            Assert.Equal("ss-v2", rows[name].Manifest);
            Assert.Equal([lifecycleCase], LifecycleCases(rows[name]));
        }

        // Every scenario names rows that exist, in the order of the deployed journal.
        Assert.All(corpus.Scenarios, scenario => Assert.All(scenario.Rows, row => Assert.Contains(row, rows.Keys)));
        Assert.Equal(["delivered-text", "delivered-tools", "snapshot-closed", "snapshot-consumed", "snapshot-prepared", "undelivered"],
            corpus.Scenarios.Select(scenario => scenario.Scenario).Order(StringComparer.Ordinal));
    }

    // The tag numbers of the top level fields of one message.
    private static HashSet<int> Tags(CorpusRecord record) => Tags(Convert.FromBase64String(record.Base64));

    private static HashSet<int> Tags(byte[] payload)
    {
        var tags = new HashSet<int>();
        var input = new CodedInputStream(payload);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            tags.Add(WireFormat.GetTagFieldNumber(tag));
            input.SkipLastField();
        }

        return tags;
    }

    // SessionSnapshotV2Proto holds BackgroundChildRunV2Proto in field 2. The lifecycle oneof uses the cases 14 to 19.
    private static int[] LifecycleCases(CorpusRecord record)
    {
        var cases = new List<int>();
        var input = new CodedInputStream(Convert.FromBase64String(record.Base64));
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == 2)
                cases.AddRange(Tags(input.ReadBytes().ToByteArray()).Where(field => field is >= 14 and <= 19));
            else
                input.SkipLastField();
        }

        return [.. cases];
    }

    private sealed class Corpus
    {
        [JsonPropertyName("sourceRevision")] public string SourceRevision { get; init; } = string.Empty;
        [JsonPropertyName("serializerIdentifier")] public int SerializerIdentifier { get; init; }
        [JsonPropertyName("records")] public CorpusRecord[] Records { get; init; } = [];
        [JsonPropertyName("scenarios")] public CorpusScenario[] Scenarios { get; init; } = [];
    }

    private sealed class CorpusRecord
    {
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("manifest")] public string Manifest { get; init; } = string.Empty;
        [JsonPropertyName("base64")] public string Base64 { get; init; } = string.Empty;
        [JsonPropertyName("sha256")] public string Sha256 { get; init; } = string.Empty;
    }

    private sealed class CorpusScenario
    {
        [JsonPropertyName("scenario")] public string Scenario { get; init; } = string.Empty;
        [JsonPropertyName("rows")] public string[] Rows { get; init; } = [];
    }
}
