## Context

Netclaw distributes self-contained binaries via a signed release feed
(`releases.netclaw.dev/manifest.json`, minisign-verified per the
`manifest-signature-verification` capability), Docker images on GHCR, and `curl | sh` /
`iwr | iex` install scripts. The daemon and CLI poll the feed to notify operators of
updates. Before this change there was exactly one channel: whatever tag was pushed last
became "latest" everywhere, so a prerelease could not be published without leaking to all
users (issue #1027). This design adds an opt-in **beta** channel across the publish and
consume paths with one unifying invariant: *stable users never see prereleases.*

## Goals / Non-Goals

**Goals:**
- Publish `x.y.z-beta.n` builds installable only by opt-in testers.
- Keep default installs, Docker `:latest`, and the GitHub "Latest" release on stable.
- Make the update check semver-correct and channel-aware.
- Add zero new runtime dependencies; keep build-side and runtime-side version precedence
  identical so the two never disagree.

**Non-Goals:**
- A `nightly` channel (tracked separately; see proposal follow-ups).
- Bounding manifest growth (tracked in #1310).
- Auto-downloading updates — the check stays advisory.

## Decisions

**One manifest with two pointers (not two manifests/feeds).** `latest` = newest stable,
`latestPrerelease` = newest of all. Additive field, `schemaVersion` stays `1`, so old
clients ignore it. *Alternative considered:* a separate `manifest-beta.json` / beta feed
— rejected because betas are a small fraction of entries (it wouldn't reduce size) and it
would add a second signed artifact, a second cache surface, and a separate URL to route.

**Beta = newest of {stable, prerelease}, not "newest prerelease only".** Testers roll
onto a stable release once it supersedes their beta, mirroring `dotnet --prerelease` /
npm prerelease tags. *Alternative:* sticky prerelease line — rejected because it strands
testers behind shipped stables.

**Self-contained `SemVer` comparator, not `NuGet.Versioning`.** Inputs are our own
well-formed tags, and the bash manifest generator already computes `latest`/
`latestPrerelease` with the same precedence rules in ~10 lines of Python — a ~40-line C#
comparator keeps the two in lockstep with no new dependency. *Alternative:*
`NuGet.Versioning` — rejected as an avoidable direct dependency for a narrow need.

**`BuildInfo.FullVersion` from `AssemblyInformationalVersion`.** `BuildInfo.Version`
reads the numeric `AssemblyVersion`, which the .NET SDK strips the `-beta.1` suffix from —
a beta build would report `0.19.0` and never see `0.19.0-beta.2`. `FullVersion` reads the
informational version (SourceLink `{version}+{sha}`) and strips the `+sha`. The update
check uses `FullVersion`; user-agent/`--version` display is left on `Version` to avoid
churn on unrelated surfaces.

**Channel rides the existing `DaemonConfig` seam.** `UpdateChannel` is read wherever
`DisableSelfUpdate` already flows (background check, `netclaw update`, status). `doctor`
loads it from `NetclawPaths` like its sibling checks. *Alternative:* a new injected
"channel provider" — rejected; the value is already reachable at every call site.

**Docker `:beta` is a retag, not a rebuild.** A dedicated job re-points `:beta` at the
`latestPrerelease` image via `docker buildx imagetools create`, gated behind
`publish-docker` + `publish-binary-manifest` so the source image exists and the
semver-correct `latestPrerelease` is known. This handles "stable supersedes beta" and
"old line patched while a newer beta is live" without the publish-docker job needing to
reason about precedence.

## Risks / Trade-offs

- **Transitional manifest lacks `latestPrerelease`** → installers and the update check
  fall back to `latest` (loud note in installers); the next release republishes the field.
- **Prerelease ordering (`beta10` vs `beta2`)** → a single mixed-alphanumeric identifier
  compares lexically (so `beta10 < beta2`), which is SemVer-correct but surprising.
  Mitigation: the **dotted convention** (`beta.10`) makes the number a numeric identifier
  that orders numerically in both the C# comparator and the bash generator, and the
  release version gate **rejects** mixed identifiers like `beta1` so a non-dotted tag can
  never ship.
- **`latestPrerelease` drift between bash and C#** → mitigated by a shared ordered-version
  fixture that BOTH a C# test (over `SemVer`) and a python check (over the generator's
  precedence key) assert against, so a change to one precedence implementation that
  diverges from the other fails CI.
- **Doctor channel resolution on malformed config** → falls back to stable so the check
  still runs; invalid enum values are surfaced by `ConfigSchemaDoctorCheck`, not masked.

## Migration Plan

No data migration. Schema change is additive (`Daemon.UpdateChannel` has a default;
`netclaw doctor --fix` can insert it). Rollout is the normal release: the first publish
after merge republishes the manifest with `latestPrerelease`. Rollback is reverting the
release workflow / installer scripts; older clients are unaffected (they read only
`latest`).

## Open Questions

- Whether to add a `nightly` channel later (a `:nightly` tag + `latestNightly` pointer +
  `--channel nightly`) — out of scope here, captured for a future change.
