> **Status: PROPOSED** — infrastructure commitments (hosting, CI publishing) not yet finalized.
> Implementation is blocked until the feed hosting backend is confirmed.

## Why

Netclaw ships two long-running components (CLI and daemon) that users install
directly. Without a stable, operator-controlled update endpoint, any repository
move or hosting change would silently break auto-update for every installed
instance. A thin managed feed in front of GitHub Releases gives us a stable
surface to evolve independently of where the repository lives.

## What Changes

- **New**: Cloudflare Worker at a stable URL (e.g.
  `https://netclaw.stannardlabs.com/releases/stable.json`) that proxies GitHub
  Releases API and returns a versioned manifest in our own schema
- **New**: Manifest schema with separate `cli` and `daemon` download entries per
  platform so the two components can version independently
- **New**: Release pipeline step that triggers the Worker cache flush (or
  equivalent) when a new GitHub Release is published
- **New**: `netclaw update` command — checks feed, downloads platform binary,
  self-replaces CLI executable using shell/PowerShell trampoline script
- **New**: `netclaw daemon update` command — stops daemon gracefully, replaces
  daemon binary, signals restart; requires daemon to be reachable via SignalR
- **New**: Background update check on `netclaw status` and `netclaw --version`
  with a non-blocking 3-second timeout; prints a nudge line if a newer version
  is available
- **Modified**: `netclaw status` response includes current daemon version/commit
  alongside any available update version (already partially landed on this branch)

## Capabilities

### New Capabilities

- `netclaw-update-feed`: Managed feed infrastructure — the Cloudflare Worker,
  manifest schema, cache/invalidation strategy, and release pipeline sync.
  Covers the server side of auto-update; the client-side update commands are
  part of `netclaw-cli`.

### Modified Capabilities

- `netclaw-cli`: Add `update` and `daemon update` commands, background update
  nudge, and version display in `netclaw status` and `--version` output.

## Impact

**Infrastructure (not yet committed)**
- Cloudflare Worker account and routing under `stannardlabs.com`
- GitHub personal access token (or GitHub App) for the Worker to call GitHub
  Releases API without anonymous rate limits
- Worker KV or Cache API for short-lived caching of the GitHub response
  (suggested TTL: 5 minutes) to avoid hammering GitHub on every daemon restart

**Release pipeline**
- GitHub Actions workflow addition: on `release` event, dispatch a Worker cache
  flush via Cloudflare API so the new manifest is live within seconds
- Binary asset naming convention must be locked in before the Worker can map
  them: `netclaw-{version}-{os}-{arch}.tar.gz` / `.zip` and
  `netclawd-{version}-{os}-{arch}.tar.gz` / `.zip`

**Client code (Netclaw.Cli)**
- New `UpdateService` — HTTP GET to feed URL, manifest deserialization, semver
  comparison (strip `+metadata` before comparing, same approach as freshdesk-cli)
- Platform detection: `RuntimeInformation.OSDescription` + `RuntimeInformation.ProcessArchitecture`
- Binary self-replace: shell script trampoline on Unix, PowerShell on Windows
  (sleep 2s → mv backup → mv new → chmod → verify `--version`)
- Feed URL should be a compile-time constant (not user-configurable in MVP)
  so users cannot accidentally point at a malicious feed

**Security**
- SHA-256 checksum verification before replacing any binary (checksums published
  alongside assets in GitHub Release, surfaced in manifest)
- Daemon binary replacement requires daemon to be stopped first; no in-process
  hot-swap
- Manifest served over HTTPS only; Worker enforces HTTPS redirect
- No auto-apply without explicit user confirmation (except `--force` flag)
