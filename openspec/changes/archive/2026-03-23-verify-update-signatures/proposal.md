## Why

Netclaw is distributed as a public open-source tool. The `netclaw update`
command downloads a binary manifest from R2 and trusts its SHA-256 checksums to
verify downloaded binaries — but the manifest itself is never verified for
authenticity. A compromised CDN/R2 bucket could serve a tampered manifest with
matching checksums pointing to malicious binaries and the client would accept it.

The signing infrastructure already exists: CI signs the manifest with minisign,
uploads `manifest.json.sig` and `manifest.pub` to R2 alongside the manifest,
and the public key is committed to the repo. The client just never checks.

Additionally, the daemon only checks for updates once at startup. Long-running
daemons go stale — a periodic recheck with proactive notification via the
existing operational alert system would close the loop on update awareness.

## What Changes

- Verify minisign Ed25519 signature on `manifest.json` before trusting its
  contents during `netclaw update` and all background update checks.
- Download `manifest.json.sig` alongside the manifest; fail loudly if signature
  is missing or invalid.
- Embed the minisign public key in the binary (compiled from
  `feeds/releases/manifest.pub`) so verification works without fetching the key
  from the same CDN being validated.
- Add periodic update recheck to `BinaryUpdateCheckService` (default: every 24
  hours while the daemon is running).
- Emit an `UpdateAvailable` operational alert via `IOperationalNotificationSink`
  when an update is detected, so configured webhooks (including Slack) receive
  proactive notification.

## Capabilities

### New Capabilities

- `manifest-signature-verification`: Minisign Ed25519 signature verification of
  the binary feed manifest, including signature format parsing, public key
  embedding, and fail-closed verification logic.

### Modified Capabilities

- `netclaw-cli`: Add signature verification requirement to the update command
  flow and background update checks. Add periodic daemon-side recheck and
  operational alert emission for update availability.

## Impact

- **Code:** `UpdateCheckService` (signature verification on manifest fetch),
  `UpdateCommand` (no direct changes — inherits verified manifest),
  `BinaryUpdateCheckService` (periodic timer + alert emission),
  `OperationalAlert` (new `UpdateAvailable` alert type), new
  `MinisignVerifier` utility class.
- **Dependencies:** None new — Ed25519 verification available via
  `System.Security.Cryptography` in .NET 10. No external NuGet packages needed.
- **Build:** Public key embedded as a compiled constant or embedded resource;
  no runtime dependency on `feeds/releases/manifest.pub` file.
- **Security:** Closes a supply-chain verification gap. Fail-closed: if
  signature is missing, invalid, or verification fails, the manifest is
  rejected. No silent fallback to unsigned manifests.
- **Wire compatibility:** No protocol changes. The `.sig` file is already
  published by CI. Older clients that don't verify signatures continue to work
  (they just ignore the `.sig` file).
- **PRD traceability:** Extends PRD-004 (CLI onboarding and config) update
  command security posture. Extends PRD-001 (MVP) default-deny security stance
  to the update channel.
