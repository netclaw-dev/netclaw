## Context

The `netclaw update` command fetches `manifest.json` from
`https://releases.netclaw.dev/manifest.json` over HTTPS. The manifest contains
SHA-256 checksums for each binary asset. The client verifies downloaded binaries
against these checksums, but never verifies that the manifest itself is
authentic.

The CI release pipeline already signs the manifest with minisign (Ed25519) and
uploads `manifest.json.sig` and `manifest.pub` to R2. The public key is
committed to the repo at `feeds/releases/manifest.pub`. The verification loop
just needs to be closed on the client side.

Current update check architecture:
- `UpdateCheckService` (static, in `Netclaw.Configuration`) — shared manifest
  fetch + evaluation logic with 1-hour cache
- `UpdateCommand` (in `Netclaw.Cli`) — interactive CLI update flow
- `BinaryUpdateCheckService` (in `Netclaw.Daemon`) — startup-only hosted service
- `UpdateAvailableDoctorCheck` — doctor check (5s timeout)
- `StatusUpdateChecker` / `BackgroundUpdateCheckAsync` — CLI background checks

## Goals / Non-Goals

**Goals:**
- Verify minisign Ed25519 signature on the manifest before trusting it
- Embed the public key in the binary so verification doesn't depend on CDN
- Add periodic (24h) daemon-side update rechecks
- Emit `UpdateAvailable` operational alert through existing notification infra
- Zero new NuGet dependencies

**Non-Goals:**
- Signing individual binary assets (SHA-256 in the verified manifest is sufficient)
- Key rotation mechanism (can be added later; requires a new binary release)
- Configurable recheck interval (hardcoded 24h is fine for MVP)
- Verifying the manifest during `install.sh` bootstrap (separate script, out of scope)

## Decisions

### 1. Minisign format parser as a standalone utility

**Decision:** Create `MinisignVerifier` in `Netclaw.Configuration.Security` as a
pure static utility that parses minisign signature files and verifies Ed25519
signatures.

**Rationale:** The minisign format is simple (2 lines: untrusted comment +
base64-encoded signature blob). The signature blob is 74 bytes: 2-byte algorithm
ID + 8-byte key ID + 64-byte Ed25519 signature. Parsing this is ~50 lines. The
public key format is identical (2 lines: comment + 42 bytes: 2-byte algorithm +
8-byte key ID + 32-byte Ed25519 public key).

**Alternative considered:** Use a NuGet package for minisign. Rejected because
no well-maintained .NET minisign library exists, and the format is trivial to
parse. Adding a dependency for 50 lines of parsing is not justified.

### 2. Ed25519 verification via System.Security.Cryptography

**Decision:** Use `System.Security.Cryptography.Ed25519` (available in .NET 9+)
for signature verification.

**Rationale:** Built-in, no external dependencies, hardware-accelerated where
available. The Ed25519 API is straightforward:
`Ed25519.VerifyData(publicKey, data, signature)`.

**Alternative considered:** `NSec.Cryptography` or `libsodium-net`. Rejected
because .NET now has native Ed25519 support and we don't want external crypto
dependencies.

### 3. Public key embedded as a compiled constant

**Decision:** Embed the raw public key bytes as a `ReadOnlySpan<byte>` constant
in `MinisignVerifier`. The key value comes from `feeds/releases/manifest.pub`.

**Rationale:** The public key must not be fetched from the CDN being verified
(circular trust). Embedding it in the binary means it ships with every release
and requires a new release to rotate. This is the standard approach (e.g.,
Homebrew, Go toolchain).

**Alternative considered:** Load from a local file in `~/.netclaw/`. Rejected
because it adds a file-not-found failure mode and the key rarely changes.

### 4. Signature verification in FetchManifestAsync

**Decision:** Add signature verification inside `UpdateCheckService.FetchManifestAsync()`.
After fetching the manifest JSON, fetch `manifest.json.sig`, parse it, and
verify before deserializing the manifest. If verification fails, return `null`
(same as any other fetch failure).

**Rationale:** This is the single point through which all manifest access flows
(CLI update, daemon check, doctor check, background check). Verifying here means
every consumer gets verified manifests without code changes.

The method already returns `null` on failure and callers handle it gracefully.
For the interactive `netclaw update` command, we need to distinguish "no update"
from "signature failure" — so `FetchManifestAsync` will be changed to return a
result type that includes the failure reason. `UpdateCommand` can then display
an appropriate error message.

### 5. Periodic timer in BinaryUpdateCheckService

**Decision:** Convert `BinaryUpdateCheckService` from `IHostedService` to
`BackgroundService` with a periodic timer. Check at startup (existing behavior),
then every 24 hours.

**Rationale:** `BackgroundService.ExecuteAsync` is the standard pattern for
long-running periodic work in ASP.NET Core. The 1-hour cache in
`UpdateCheckService` will have long expired by the 24-hour mark, so each
periodic check triggers a fresh manifest fetch.

### 6. UpdateAvailable alert type and emission

**Decision:** Add `UpdateAvailable` to the `AlertType` enum. Emit from
`BinaryUpdateCheckService` when `result.IsUpdateAvailable` is true. Use
`IOperationalNotificationSink` (already a registered singleton in the daemon).

**Rationale:** The notification infrastructure (webhook delivery, Slack
formatting, deduplication) already exists. The deduplication key will be
`update.available:{latestVersion}`, so the same version doesn't spam. This is
~10 lines of code.

## Risks / Trade-offs

**[Risk] Public key becomes stale after key rotation** → Mitigation: Key
rotation requires publishing a new binary release with the updated key. This is
acceptable because key rotation is a rare, deliberate event. Document the
rotation procedure in the release runbook.

**[Risk] Ed25519 API availability** → Mitigation: .NET 9+ includes Ed25519 in
`System.Security.Cryptography`. Netclaw targets .NET 10, so this is available.
Verify in CI.

**[Risk] Signature check adds latency to update flow** → Mitigation: The `.sig`
file is ~200 bytes. Fetching it is negligible compared to the manifest fetch.
Ed25519 verification is sub-millisecond. No measurable impact.

**[Risk] Older manifests published before signing was enabled** → Mitigation:
The signing infrastructure has been in place since the first release. All
published manifests have signatures. If a pre-signing manifest somehow exists,
the signature fetch returns 404 and the update is rejected (fail-closed, as
designed).

**[Trade-off] Fail-closed on signature failure vs. graceful degradation** → We
chose fail-closed. A missing or invalid signature means the manifest cannot be
trusted. Silent fallback to unsigned manifests would defeat the purpose. The
user sees a clear error message and can investigate.
