# Historical coordination wire corpus

`legacy-writer-records.json` contains neutral payloads from the deployed draft writer at revision `9f79c0c4b637f005ffba5572d0bf50aa550464f2`.

The isolated capture used the historical `NetclawProtobufSerializer.Manifest(object)` and `ToBinary(object)` methods. `capture-source.cs.txt` holds the temporary test method. The pinned test class supplies its fixture helpers.

To reproduce the corpus from a repository clone, use the exact writer revision and run:

```bash
git worktree add --detach /tmp/netclaw-legacy-capture 9f79c0c4b637f005ffba5572d0bf50aa550464f2
cp src/Netclaw.Actors.Tests/Fixtures/CoordinationLegacy/capture-source.cs.txt /tmp/netclaw-legacy-capture/src/Netclaw.Actors.Tests/SubAgents/BackgroundChildLegacyFixtureCapture.cs
NETCLAW_HISTORICAL_FIXTURE_OUTPUT=/tmp/netclaw-legacy-capture/legacy-writer-records.json dotnet test /tmp/netclaw-legacy-capture/src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj --filter 'FullyQualifiedName~Capture_neutral_deployed_wire_fixtures' -m:1
```

The candidate serializer did not create these payloads. The first corpus hash was `994682681c870ae7b5242405c488c445569f3dd8f327ac6b18d3d0ee0ec85a36`. The expanded corpus hash is `c6e126f2c282db52c228f95da5c515786928c2b0cf7db672d6ab20d3ccd0b41b`.

The corpus contains `cra-v1`, all seven `cre-v1` facts, three `tta-v1` facts, `ia-v1`, `ic-v1`, `tbs-v1`, `tcr-v1`, `tar-v1`, `tares-v1`, and two `ss-v1` snapshots. Each record stores the manifest, Base64 payload, and payload SHA-256.

The added records include a valid ordinary input adoption and a valid child delivery adoption. The child snapshot preserves the deployed call ID, source-operation tool name, argument JSON, and tool result. The source operation is `spawn_agent`; the payload body is omitted here.

The #2453 case holds input A pending, admits input B without source IDs on either record, then records adoption of B. Its task text is neutral. The fixture preserves the writer's bytes and does not imply that the sequence is valid.
