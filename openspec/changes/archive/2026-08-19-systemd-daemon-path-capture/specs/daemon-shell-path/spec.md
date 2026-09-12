## ADDED Requirements

### Requirement: Installed service PATH is captured from the operator environment, not guessed

When installing the systemd `--user` service, `netclaw daemon install` SHALL provision the
daemon's `PATH` by capturing the operator's real `PATH` from the CLI process's own inherited
environment. It SHALL NOT write a hardcoded/guessed list of directories, and it SHALL NOT execute
a shell or source operator dotfiles to obtain the value. The captured `PATH` SHALL be delivered to
the daemon via a netclaw-owned environment file referenced by the unit's `EnvironmentFile=`
directive, and the generated unit SHALL NOT contain an inline `Environment=PATH=` directive.

The provisioned `PATH` value SHALL place the daemon's own install directory first, then the
captured operator `PATH`, then a guaranteed system-directory floor
(`/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin`) so the daemon's shell remains functional even when
the installing shell's `PATH` was empty or partial. Entries SHALL be de-duplicated, and empty
`PATH` elements (which POSIX resolves to the current directory) SHALL be dropped.

#### Scenario: Install captures the caller's PATH into the environment file

- **WHEN** the operator runs `netclaw daemon install` from a shell whose `PATH` includes
  `~/.dotnet`
- **THEN** the netclaw-owned environment file contains a `PATH=` line that includes `~/.dotnet`
- **AND** the file's `PATH` begins with the daemon's install directory
- **AND** no shell process was spawned to read the `PATH`

#### Scenario: Generated unit wires the environment file and omits inline PATH

- **WHEN** `netclaw daemon install` writes `~/.config/systemd/user/netclaw.service`
- **THEN** the unit contains an `EnvironmentFile=` directive pointing at the netclaw-owned
  environment file
- **AND** the unit does NOT contain an `Environment=PATH=` directive

### Requirement: `doctor --fix` rehydrates the daemon PATH environment file

When the installed unit references the daemon PATH environment file, `netclaw doctor --fix` SHALL
rehydrate that file from the current shell's `PATH` when it is missing or does not include the
daemon's install directory. Rehydration SHALL run independently of whether the application config
file (`netclaw.json`) exists. The fix SHALL write files only — creating the parent directory if it
is absent — and SHALL surface an explicit instruction to run `systemctl --user restart netclaw`; it
SHALL NOT restart the daemon implicitly. `doctor --fix` SHALL NOT rewrite the systemd unit: a unit
that does not reference the environment file (a legacy inline-`PATH` unit) is a reinstall case,
routed by the doctor check.

#### Scenario: Missing environment file is recreated by the fix

- **WHEN** the systemd unit is installed but the daemon PATH environment file is absent
- **AND** the operator runs `netclaw doctor --fix`
- **THEN** the fix writes the environment file from the current shell's `PATH`
- **AND** the fix output instructs the operator to `systemctl --user restart netclaw`
- **AND** the fix does not restart the daemon

#### Scenario: Rehydration runs even when the app config file is absent

- **WHEN** `netclaw.json` does not exist
- **AND** the systemd unit is installed but its PATH environment file is stale or missing
- **AND** the operator runs `netclaw doctor --fix`
- **THEN** the environment-file rehydration fix is still evaluated and applied

### Requirement: Systemd PATH doctor check validates the environment-file wiring

`SystemdUnitPathDoctorCheck` SHALL validate that the installed unit references the daemon PATH
environment file via `EnvironmentFile=`, that the referenced file exists, and that the file's
`PATH` includes the daemon's install directory. When the referenced file is missing or omits the
install directory, remediation SHALL point the operator at `netclaw doctor --fix` followed by a
restart. A legacy unit that supplies its `PATH` inline (`Environment=PATH=`) SHALL pass when that
inline `PATH` includes the install directory (with a note to migrate via reinstall), and SHALL warn
with a reinstall remediation otherwise. The check SHALL pass silently when no service is installed
or on non-Linux platforms.

#### Scenario: Wired, present, and install-dir on PATH passes

- **WHEN** the unit references the environment file, the file exists, and its `PATH` includes the
  install directory
- **THEN** the check passes

#### Scenario: Functional legacy inline-PATH unit passes with a migration note

- **WHEN** the installed unit has no `EnvironmentFile=` but supplies an inline `Environment=PATH=`
  that includes the install directory
- **THEN** the check passes and notes that re-running `netclaw daemon install` migrates it

#### Scenario: Unwired or broken legacy unit warns with reinstall remediation

- **WHEN** the installed unit has no `EnvironmentFile=` and no inline `PATH` that includes the
  install directory
- **THEN** the check returns a warning with remediation to reinstall and restart

#### Scenario: Referenced environment file absent warns

- **WHEN** the unit references the environment file but the file does not exist on disk
- **THEN** the check returns a warning with remediation to run `netclaw doctor --fix` and restart

#### Scenario: No service installed skips

- **WHEN** no netclaw systemd unit file exists
- **THEN** the check passes without warning

### Requirement: Uninstall removes the daemon PATH environment file

`netclaw daemon uninstall` SHALL remove the netclaw-owned daemon PATH environment file in addition
to the unit file, leaving no orphaned environment file behind.

#### Scenario: Uninstall deletes the environment file

- **WHEN** the operator runs `netclaw daemon uninstall` with an installed service and an existing
  daemon PATH environment file
- **THEN** both the unit file and the daemon PATH environment file are deleted
