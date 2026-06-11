# release-channels Specification

## Purpose

Define how Netclaw publishes, distributes, and self-updates across stable and
beta release channels. Covers the release feed manifest format, installer channel
selection, prerelease-aware CI publishing, version comparison, and update-check
behavior.

## Requirements

### Requirement: Release feed manifest channel pointers

The release feed manifest (`releases/manifest.json`) SHALL expose two version
pointers: `latest` (the newest **stable** version) and `latestPrerelease` (the newest
version of any kind — stable or prerelease). `latestPrerelease` SHALL always be greater
than or equal to `latest` by SemVer precedence. Both pointers SHALL be computed over the
union of the version being published and all versions already in the manifest. The field
is additive and `schemaVersion` SHALL remain `1`.

#### Scenario: Publishing a prerelease does not move `latest`

- **WHEN** a prerelease version (e.g. `0.19.0-beta.1`) is published while the newest
  stable is `0.18.1`
- **THEN** `latest` remains `0.18.1`
- **AND** `latestPrerelease` becomes `0.19.0-beta.1`
- **AND** the prerelease's assets are appended to `releases[]`

#### Scenario: A stable release supersedes a prior prerelease

- **WHEN** stable `0.19.0` is published after prerelease `0.19.0-beta.1`
- **THEN** `latest` becomes `0.19.0`
- **AND** `latestPrerelease` becomes `0.19.0` (the newest of all versions)

#### Scenario: Older clients ignore the new field

- **WHEN** a client built before this change deserializes the manifest
- **THEN** the unknown `latestPrerelease` field is ignored and deserialization succeeds

### Requirement: Installer channel selection

The install scripts SHALL accept a channel selector (`install.sh --channel <stable|beta>`,
`install.ps1 -Channel <stable|beta>`) defaulting to `stable`. The `beta` channel SHALL
resolve to the manifest's `latestPrerelease`, falling back to `latest` only when
`latestPrerelease` is absent (a manifest published before this capability). An explicit
version pin (`NETCLAW_VERSION` / `-Version`) SHALL override the channel. An unrecognized
channel value SHALL fail loudly rather than silently defaulting.

#### Scenario: Default install resolves to latest stable

- **WHEN** the installer is run with no channel argument
- **THEN** it installs the manifest's `latest` (stable) version

#### Scenario: Beta channel resolves to the newest prerelease

- **WHEN** the installer is run with `--channel beta` (or `-Channel beta`)
- **THEN** it installs the manifest's `latestPrerelease` version

#### Scenario: Explicit version pin overrides the channel

- **WHEN** `NETCLAW_VERSION` (or `-Version`) is set alongside `--channel beta`
- **THEN** the pinned version is installed regardless of channel

#### Scenario: Unknown channel is rejected

- **WHEN** the installer is run with an unrecognized channel value
- **THEN** the installer exits non-zero with an error and installs nothing

### Requirement: Prerelease-aware publishing

The release pipeline SHALL treat a tag containing a hyphen (`-`) as a prerelease.
A prerelease publish SHALL be marked a GitHub *prerelease*, SHALL NOT move the floating
stable Docker tags (`:latest`, `:major.minor`), and SHALL publish only its exact-version
Docker tag. A rolling Docker `:beta` tag SHALL track `latestPrerelease`. The CI version
gate SHALL validate the tag against `<VersionPrefix>` + `<VersionSuffix>` rather than the
verbatim tag string.

#### Scenario: Prerelease tag is marked and does not move `:latest`

- **WHEN** a tag like `0.19.0-beta.1` is published
- **THEN** the GitHub release is flagged as a prerelease
- **AND** Docker `:latest` and `:major.minor` are unchanged
- **AND** only `ghcr.io/netclaw-dev/netclaw:0.19.0-beta.1` is pushed for that build

#### Scenario: Stable tag moves the floating stable tags

- **WHEN** a stable tag like `0.19.0` is published
- **THEN** Docker `:latest` and `:0.19` are moved to it
- **AND** the GitHub release is not flagged as a prerelease

#### Scenario: `:beta` tracks the newest prerelease

- **WHEN** `latestPrerelease` is `0.19.0-beta.1`
- **THEN** `ghcr.io/netclaw-dev/netclaw:beta` resolves to that image

#### Scenario: Version gate validates prefix and suffix

- **WHEN** tag `0.19.0-beta.1` is published with `<VersionPrefix>0.19.0</VersionPrefix>`
  and `<VersionSuffix>beta.1</VersionSuffix>`
- **THEN** the version gate passes
- **AND** a tag whose prefix/suffix do not match the props fails the gate

### Requirement: Prerelease tags use dotted identifiers

A prerelease tag's suffix SHALL use dot-separated identifiers where each identifier is
either all-letters or all-digits (e.g. `beta.1`, `rc.10`) — never a mixed token like
`beta1`. The release version gate SHALL reject a tag containing a mixed-alphanumeric
identifier. This keeps a numeric part a numeric identifier, so `beta.10` outranks
`beta.2` consistently in both the C# comparator and the manifest generator (a mixed
`beta10` would compare lexically and rank below `beta2`).

#### Scenario: Dotted prerelease tag is accepted

- **WHEN** a tag `0.19.0-beta.1` is published
- **THEN** the release version gate accepts it

#### Scenario: Mixed-identifier prerelease tag is rejected

- **WHEN** a tag `0.19.0-beta1` is published
- **THEN** the release version gate fails with guidance to use the dotted form `0.19.0-beta.1`

### Requirement: Update channel configuration

The daemon configuration SHALL expose `Daemon.UpdateChannel` with values `stable`
(default) and `beta`, validated by the config schema. An unrecognized value SHALL fail
loudly. This single setting SHALL govern every client-side update surface (daemon
notification, `netclaw update`, CLI startup notice, `netclaw status`, `netclaw doctor`).

#### Scenario: Defaults to stable when unset

- **WHEN** `Daemon.UpdateChannel` is absent from configuration
- **THEN** the resolved channel is `stable`

#### Scenario: Unknown channel value is rejected

- **WHEN** `Daemon.UpdateChannel` is set to an unrecognized value
- **THEN** configuration binding throws rather than silently defaulting

### Requirement: Stable clients are never offered a prerelease

When the configured channel is `stable`, the update check SHALL only ever compare the
running version against the manifest's `latest` pointer. It SHALL NOT read
`latestPrerelease` and SHALL NOT offer a prerelease to a stable client under any
manifest contents.

#### Scenario: A newer prerelease is not offered to a stable client

- **WHEN** a stable client checks for updates and the manifest has `latest=0.18.1`,
  `latestPrerelease=0.19.0-beta.1`
- **THEN** no update is reported

#### Scenario: A newer stable is offered to a stable client

- **WHEN** a stable client on `0.18.1` checks and the manifest has `latest=0.19.0`
- **THEN** an update to `0.19.0` is reported

### Requirement: Beta clients track the newest version

When the configured channel is `beta`, the update check SHALL compare the running
version against `latestPrerelease` (the newest of {stable, prerelease}). A beta client
SHALL be offered a newer prerelease, and SHALL be rolled onto a stable release once it
supersedes the running prerelease.

#### Scenario: Beta client is offered the next prerelease

- **WHEN** a beta client on `0.19.0-beta.1` checks and `latestPrerelease=0.19.0-beta.2`
- **THEN** an update to `0.19.0-beta.2` is reported

#### Scenario: Beta client rolls onto a superseding stable

- **WHEN** a beta client on `0.19.0-beta.1` checks after stable `0.19.0` shipped
  (`latestPrerelease=0.19.0`)
- **THEN** an update to `0.19.0` is reported

#### Scenario: Beta client on the newest prerelease has no update

- **WHEN** a beta client on `0.19.0-beta.1` checks and `latestPrerelease=0.19.0-beta.1`
- **THEN** no update is reported

### Requirement: SemVer-correct version comparison

The update check SHALL compare versions using SemVer 2.0.0 precedence: a version with a
prerelease suffix has lower precedence than the same core version without one, and
prerelease identifiers are compared per the specification (numeric identifiers lower than
alphanumeric, longer identifier sets higher when prefixes match). Build metadata SHALL be
ignored. An unparseable version SHALL be treated as "no update available" (fail safe).

#### Scenario: A prerelease precedes its own release

- **WHEN** comparing `0.19.0-beta.1` against `0.19.0`
- **THEN** `0.19.0` is the newer version

#### Scenario: Unparseable version yields no update

- **WHEN** either the running version or the candidate cannot be parsed as SemVer
- **THEN** no update is reported

### Requirement: Self-version includes the prerelease suffix

The update check SHALL identify the running binary's version from the assembly
informational version (which retains the prerelease suffix, e.g. `0.19.0-beta.1`), not the
numeric assembly version (which strips it). This prevents a beta build from reporting its
core version and stranding on its own prerelease line.

#### Scenario: A beta build reports its full version

- **WHEN** a binary built from tag `0.19.0-beta.1` reports its version for the update check
- **THEN** the reported version is `0.19.0-beta.1`, not `0.19.0`

### Requirement: Update check is advisory only

The update check SHALL never download or install an update on its own. When an update is
available it SHALL surface a notice (CLI) and/or an operational alert (daemon). In-place
self-update SHALL require an explicit `netclaw update`. When `Daemon.DisableSelfUpdate`
is true, in-place update SHALL be blocked while the availability check still runs and
notifies.

#### Scenario: Available update notifies without downloading

- **WHEN** the background check finds an available update
- **THEN** a notice/alert is produced
- **AND** no binary is downloaded or replaced

#### Scenario: Self-update disabled still checks and notifies

- **WHEN** `Daemon.DisableSelfUpdate` is true and an update is available
- **THEN** the availability check still runs and emits a notice
- **AND** `netclaw update` declines to perform an in-place update
