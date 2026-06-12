# Tasks

Most work is already implemented across PR #1314 (merged) and the in-flight update-check
PR; checkboxes reflect actual state.

## 1. Release feed manifest pointers (PR #1314)

- [x] 1.1 Compute `latest` (newest stable) + additive `latestPrerelease` (newest of all) with semver precedence in `feeds/scripts/generate-release-manifest.sh`
- [x] 1.2 Keep `schemaVersion` at 1; preserve `releases[]` accumulation
- [x] 1.3 Add `LatestPrerelease` to `BinaryFeedManifest` (additive, ignored by old clients)

## 2. Installer + Docker channel selection (PR #1314)

- [x] 2.1 `install.sh --channel <stable|beta>` with precedence pin > channel > stable; loud failure on unknown channel; loud fallback to `latest` when `latestPrerelease` absent
- [x] 2.2 `install.ps1 -Channel <stable|beta>` mirroring the same precedence
- [x] 2.3 Docker `:beta` retag job tracking `latestPrerelease`; `:latest`/`:major.minor` suppressed for prerelease tags
- [x] 2.4 README "Beta / prerelease versions" section

## 3. Prerelease-aware publishing (PR #1314)

- [x] 3.1 GitHub release `prerelease: ${{ contains(github.ref_name, '-') }}`
- [x] 3.2 CI version gate validates `VersionPrefix` + `VersionSuffix`

## 4. Update channel configuration (update-check PR)

- [x] 4.1 Add `Daemon.UpdateChannel` (`stable` default | `beta`) to `DaemonConfig` + `ParseUpdateChannel` (loud on unknown)
- [x] 4.2 Add `UpdateChannel` enum to schema (`netclaw-config.v1.schema.json`, string enum + default)

## 5. Channel-aware, semver-correct update check (update-check PR)

- [x] 5.1 Add self-contained `SemVer` comparator (no `NuGet.Versioning`); reimplement `IsNewerVersion` on it
- [x] 5.2 Add `BuildInfo.FullVersion` (Configuration + Daemon facade) reading informational version
- [x] 5.3 Make `EvaluateManifest`/`CheckForUpdateAsync` channel-aware (stable → `latest` only; beta → `latestPrerelease`)
- [x] 5.4 Thread channel + `FullVersion` through `BinaryUpdateCheckService`, `UpdateCommand`, `StatusUpdateChecker`, `UpdateAvailableDoctorCheck`, and `Program.cs`

## 6. Tests

- [x] 6.1 `SemVerTests` (precedence rules, build metadata, unparseable fail-safe)
- [x] 6.2 `UpdateChannelEvaluationTests` (stable-never-prerelease, beta tracks/rolls onto stable, fallback)
- [x] 6.3 `DaemonConfig.ParseUpdateChannel` tests (known/empty/unknown)
- [x] 6.4 `install-smoke.{sh,ps1}` beta-channel assertions (default→stable, `--channel beta`→prerelease, pin overrides, unknown rejected)

## 7. Delivery

- [x] 7.1 PR #1314 (publish + install half) merged to `dev`
- [ ] 7.2 Open the update-check PR (draft) into `dev` and get CI green
- [ ] 7.3 Sync delta spec to `openspec/specs/release-channels/` and archive this change once both PRs land
