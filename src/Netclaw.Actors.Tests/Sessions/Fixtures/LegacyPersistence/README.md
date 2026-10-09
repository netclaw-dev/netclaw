# Pre-change session persistence proof

The fixture uses neutral data. It contains no operator transcript or private session data.

Baseline: `2e6bc4f014dc96b606566709df1bd11f90ecf34a`.
The baseline serializer emitted every byte payload. The candidate serializer did not create these payloads.

The baseline test uses `Sys.Serialization.FindSerializerFor`, `ToBinary`, and `Manifest`.
The serializer identifier is `150`. Each entry includes its manifest, Base64 payload, and SHA-256 digest.
The capture test checks that the baseline reader accepts each payload.
The producer also checks the canonical authorization identifier with the baseline parser.

The journal has these six events:

1. `InputAdmitted`: an ordinary task and its original authority.
2. `TurnRecorded`: the completed ordinary task.
3. `InputAdmitted`: a separate task that needs an approval prompt.
4. `ToolBatchStarted`: one file read and one protected operation.
5. `ToolCallRecorded`: the completed file-read sibling.
6. `ToolApprovalRequested`: the protected operation and its original authority.

The snapshot contains only the completed ordinary task at sequence `2`.
The baseline blocks snapshots when an input or an approval batch remains unresolved.
The snapshot case appends events `3` through `6` after the acknowledged snapshot save.
This proof does not invent a parked-task snapshot.

The consumer decodes the captured bytes with the current real serializer.
It then seeds the real journal and snapshot store through their normal actor interfaces.
The normal session actor consumes those records after the seed actor stops.

The shared approval fake omits typed dispatch receipts.
The consumer keeps that fake's approval checks and effect counters.
It emits the canonical success receipt only after the fake returns successfully.
The candidate requires this receipt before a model continuation.

The four cases cover journal replay and snapshot-plus-journal replay for both task states.
The cases reject another requester's approval answer.
They preserve the original authority, old history, and the completed sibling result.
They require zero replay of completed effects and reject a duplicate approval answer.

This proof does not import an old PostgreSQL store or a plugin envelope.
It does not exercise a live compaction save.
It does not prove that an old reader accepts new records.
It does not prove a downgrade or a rollback procedure.

Apply `capture-baseline.patch` in an isolated checkout at the baseline.
Then use the baseline test source to capture the fixture:

```bash
NETCLAW_LEGACY_FIXTURE_OUTPUT="$PWD/artifacts/pre-change-persistence/legacy-session-v0.json" \
  dotnet test src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj \
  --filter 'FullyQualifiedName~PreChangePersistenceCaptureTests' \
  --logger 'trx;LogFileName=baseline-capture.trx' \
  --results-directory artifacts/pre-change-persistence
```

The fixture SHA-256 digest is `bf400d15ea1541d1a59b8be238705c99eb7f489413c863d93b17976ab1cfc94b`.

Keep the capture test outside the candidate's normal test suite.
The candidate needs the captured fixture and the consumer test only.
