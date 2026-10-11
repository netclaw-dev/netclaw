# Pre-change session storage fixture

Baseline: `2e6bc4f014dc96b606566709df1bd11f90ecf34a`.
Session: `signalr/pre-change-persistence-proof`.
The existing conversation and approval fixture uses this same session identifier.

The baseline migrations create the schema. The baseline `SqliteSessionStorageResolver` creates the binding.
The producer reads its actual row and writes one neutral workspace marker.
The fixture retains all four row columns, the marker's relative path, its bytes, and its SHA-256 digest.
The capture creates the binding before any journal seed, as a normal new session does.
The producer uses a fixed clock and deletes its test home after capture.
Its old absolute root names only a test-owned temporary directory.

The consumer restores the exact row under the current schema and the marker under a different test home.
It calls the real resolver twice through separate instances.
It requires the current envelope and workspace paths, the original marker bytes, and one unchanged database row.
The consumer uses a later clock, so a replacement binding cannot pass the original-row assertion.

Re-root semantics are explicit: the resolver maps the old `sessions/<envelope>` suffix below the current sessions directory.
It retains the old absolute database value and creation time.
The consumer does not rewrite the stored row or use the old absolute path for a file write.
Both separator styles remain part of the existing resolver contract.

Fixture SHA-256: `a3fadd7978ffbecc1667120860486c0cebbb01a46d87854ce3151f3f2bd4326a`.

The bundled `capture-baseline.patch` adds only the capture test.
Apply it in an isolated baseline checkout after the consumer and fixture patch.
Then capture to an owned artifact path:

```bash
NETCLAW_STORAGE_FIXTURE_OUTPUT="$PWD/artifacts/pre-change-storage/legacy-storage-binding-v0.json" \
  dotnet test src/Netclaw.Daemon.Tests/Netclaw.Daemon.Tests.csproj \
  --filter 'FullyQualifiedName~PreChangeStorageBindingCaptureTests' \
  --logger 'trx;LogFileName=baseline-storage-capture.trx' \
  --results-directory artifacts/pre-change-storage
```

The normal suite contains only `PreChangeStorageBindingTests`, which consumes the captured asset.
Keep the capture test outside that suite.
A new capture uses another temporary home, so its whole-file digest can differ.
Retain its baseline revision, producer source, payload digest, and actual test receipt.

This fixture captures one actual storage row. It does not import an old complete SQLite database or plugin envelope.
It does not prove a live compaction save, old-reader compatibility, child recovery, or an empty new child ledger.
Those proof gates remain separate.
