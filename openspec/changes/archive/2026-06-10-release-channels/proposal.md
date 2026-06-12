## Why

Netclaw had no concept of a release *channel*: any pushed tag — including a semver
prerelease like `0.19.0-beta.1` — became the de-facto "latest" everywhere (install
scripts, Docker `:latest`, the GitHub "Latest" release, and the update check). A real
`0.19.0-beta.1` was abandoned and a plain `0.18.1` shipped instead, because the pipeline
could not publish a prerelease for opt-in testers without leaking it to every fresh
install (GitHub issue #1027). This change introduces a public **beta** channel so we can
ship prereleases to testers who explicitly opt in, while default installs and stable
users are never affected.

## What Changes

- **Release feed manifest** gains an additive `latestPrerelease` pointer
  (newest of {stable, prerelease}) alongside `latest` (newest stable). `schemaVersion`
  stays `1`; the field is ignored by older clients (not breaking).
- **Installers** gain channel selection: `install.sh --channel beta` and
  `install.ps1 -Channel beta` resolve to `latestPrerelease`. An explicit version pin
  (`NETCLAW_VERSION` / `-Version`) overrides the channel; an unknown channel fails loudly.
- **Docker** gains a rolling `:beta` tag that tracks `latestPrerelease`; `:latest`
  (and `:major.minor`) only ever point at the newest stable.
- **Prerelease-aware publishing**: a tag containing `-` is marked a GitHub *prerelease*,
  does not move the floating stable tags, and the CI version gate validates
  `VersionPrefix` + `VersionSuffix` (not the verbatim tag).
- **Channel-aware update check**: a new `Daemon.UpdateChannel` (`stable` default | `beta`)
  governs the daemon notification, `netclaw update`, the CLI startup notice,
  `netclaw status`, and `netclaw doctor`. Stable clients are never offered a prerelease.
- **SemVer-2.0.0-correct version comparison** replaces `System.Version`, which could not
  parse prerelease suffixes (and so silently never offered a prerelease). The running
  binary's self-version is read from the assembly informational version
  (`BuildInfo.FullVersion`, retains `-beta.1`) rather than the suffix-stripped
  `AssemblyVersion`.
- The update check remains **advisory only** — it emits a notice/alert and never
  auto-downloads; `Daemon.DisableSelfUpdate` continues to block in-place update while the
  check still runs.

## Capabilities

### New Capabilities
- `release-channels`: the stable/beta channel model end-to-end — manifest pointer
  semantics, prerelease-aware publishing (GitHub release flag, Docker tag policy, CI
  version gate), installer + Docker channel selection, and the channel-aware update-check
  policy (`Daemon.UpdateChannel`, stable-never-prerelease invariant, SemVer comparison,
  full-version self-identification, advisory-only behavior).

### Modified Capabilities
<!-- None. The manifest gains an additive field but manifest-signature-verification
     behavior (minisign parsing/verification/fail-closed) is unchanged. -->

## Impact

- **Build/release**: `feeds/scripts/generate-release-manifest.sh`,
  `.github/workflows/publish_release_binaries.yml` (GitHub release flag, Docker tags +
  `:beta` job, version gate), `scripts/install.sh`, `scripts/install.ps1`,
  `scripts/smoke/install-smoke.{sh,ps1}`, `README.md`.
- **Runtime (.NET)**: `BinaryFeedManifest` (`LatestPrerelease`), new `SemVer` comparator,
  `UpdateCheckService` (channel-aware `EvaluateManifest`/`CheckForUpdateAsync`, semver
  `IsNewerVersion`), `BuildInfo.FullVersion` (Configuration + Daemon facade),
  `DaemonConfig.UpdateChannel` + `netclaw-config.v1.schema.json`, and the consumers:
  `BinaryUpdateCheckService`, `UpdateCommand`, `StatusUpdateChecker`,
  `UpdateAvailableDoctorCheck`, `Program.cs` wiring.
- **No new dependencies**: the SemVer comparator is self-contained (no `NuGet.Versioning`),
  kept in sync with the bash manifest generator's precedence rules.
- **Delivery**: merged PR netclaw-dev/netclaw#1314 (publish + install half) and the
  in-flight update-check PR on `feat/update-check-channel-aware`. Follow-up: manifest
  unbounded-growth (#1310).
