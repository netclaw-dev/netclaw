# Deployed v2 coordination wire corpus

`v2-writer-records.json` holds the bytes that the child-run writers of revision `c4ed4dcc6a2165d92d074dfd6cf603d2873bca13` produce. The S2 change deletes these writers. Nothing else can make these bytes after that change.

The corpus has six scenarios:

- `delivered-text`: a child ends, and the parent consumes the result in a text turn. The journal holds `cre-v2` `TerminalRecorded` and `ResultPrepared` rows, and a `tr-v1` row with a child continuation.
- `delivered-tools`: the same flow, but the consuming turn calls a tool. The journal holds a `tbs-v1` row with a child continuation.
- `undelivered`: a child ends, and the journal stops at `ResultPrepared`. The parent never read the result.
- `snapshot-prepared`, `snapshot-consumed`, `snapshot-closed`: `ss-v2` snapshots with the lifecycle cases 17, 18, and 19.

The real flows made the journal rows. The test serialized each committed event with `NetclawProtobufSerializer.Manifest` and `ToBinary`, which is the call path of Akka persistence. The in-memory snapshot store gave the closed snapshot and an accepted snapshot. The prepared snapshot comes from the reducer, which applies the recorded `TerminalRecorded` and `ResultPrepared` events to the accepted snapshot. The consumed snapshot replaces the lifecycle of the closed snapshot with the consumed record of the same result. The test checks each snapshot with a read back through the serializer.

The payloads hold absolute paths of the temporary folder of the capture run. Replays ignore those paths.

To make the corpus again, use a checkout of the writer revision:

```bash
git worktree add --detach /tmp/netclaw-v2-capture c4ed4dcc6a2165d92d074dfd6cf603d2873bca13
```

Then copy `ChildNoticeTestBase.cs` and `capture-source.cs.txt` from the test project of the S2 test commit. Name the second file `CoordinationV2FixtureCapture.cs`. Put both in `src/Netclaw.Actors.Tests/Sessions/` of the checkout. Run:

```bash
NETCLAW_V2_SOURCE_REVISION=c4ed4dcc6a2165d92d074dfd6cf603d2873bca13 \
NETCLAW_V2_FIXTURE_OUTPUT=/tmp/v2-writer-records.json \
dotnet test src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj -c Release \
  --filter "FullyQualifiedName~CoordinationV2FixtureCapture"
```

`CoordinationV2CorpusTests` checks the corpus by the raw protobuf tags. `DeployedV2JournalReplayTests` replays the corpus through the production reader.
