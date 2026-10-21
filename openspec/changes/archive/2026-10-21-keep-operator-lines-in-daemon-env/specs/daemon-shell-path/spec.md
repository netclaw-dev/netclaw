## MODIFIED Requirements

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

When the environment file already exists, `netclaw daemon install` and `netclaw doctor --fix` SHALL
replace only the `PATH` line and SHALL keep every other line verbatim and in order, including lines
that the operator wrote, such as `NETCLAW_HOME`. A line is a `PATH` line when its key, the text
before the first `=`, is `PATH` once trimmed. The first `PATH` line SHALL change in place, a later
`PATH` line SHALL be removed, and `PATH=` SHALL be added at the end when none exists. A rerun with
the same input SHALL leave the file byte-identical.

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

#### Scenario: Install keeps an operator line

- **WHEN** the environment file holds `NETCLAW_HOME=/x/home` and `PATH=/old`
- **AND** the operator runs `netclaw daemon install`
- **THEN** the `NETCLAW_HOME=/x/home` line is unchanged
- **AND** the `PATH` line holds the new value

#### Scenario: Install removes a duplicate PATH line

- **WHEN** the environment file holds two `PATH` lines
- **AND** the operator runs `netclaw daemon install`
- **THEN** the file holds one `PATH` line
- **AND** that line is at the place of the first `PATH` line

