## Why

`netclaw daemon install` and `netclaw doctor --fix` rewrote the daemon environment file with only a `PATH=` line. They removed every line that the operator wrote, for example `NETCLAW_HOME`. The spec calls the file netclaw-owned and does not say what happens to other lines.

## What Changes

- Install and `doctor --fix` replace only the `PATH` line of the daemon environment file.
- Both commands keep every other line verbatim and in order.
- A later `PATH` line goes, because systemd keeps the last assignment of a key.

## Capabilities

### New Capabilities

### Modified Capabilities
- `daemon-shell-path`: the install requirement now keeps operator lines and removes a duplicate `PATH` line.

## Impact

- `DaemonPathEnvironmentFile.Merge`, `DaemonManager.InstallAsync`, and `DoctorFixService`.
- PR 2528 implements the change.
