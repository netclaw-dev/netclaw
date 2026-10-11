---
name: local-binary-swap
description: Publish and swap local netclaw/netclawd binaries for live integration testing. Activate when user says "swap binaries", "publish locally", "test locally", or wants to test CLI/daemon changes against their running install.
---

# Local Binary Swap for Integration Testing

Publish the CLI and/or daemon from the current worktree and swap them into the
user's local `~/.netclaw/bin/` install for live testing.

## Safety Protocol

1. **Stop the daemon first** — never overwrite a running binary. If systemd
   manages the daemon, stop the systemd unit. `netclaw daemon stop` is not
   sufficient, because systemd restarts the daemon in a few seconds.
2. **Back up originals** — always create `.bak` copies before overwriting
3. **Confirm with user** before swapping if unsure about their intent
4. **Always re-launch the daemon after swap** — the swap procedure leaves the
   daemon stopped. Channels, webhooks, and the doctor's MCP/daemon-connectivity
   checks all stay broken until you start it again. This step is not optional.

## Procedure

### 1. Stop any running daemon

First, find out if a systemd user service manages the daemon:

```bash
systemctl --user is-enabled netclaw.service
systemctl --user is-active netclaw.service
```

If either command prints `enabled` or `active`, systemd manages the daemon.
`netclaw daemon stop` stops the unit for you when `NETCLAW_HOME` is unset or is
`~/.netclaw` (the unit serves only that home), so it is safe in both cases:

```bash
netclaw daemon stop 2>&1 || true
```

To stop the unit directly instead, run `systemctl --user stop netclaw.service`.
Never use `pkill`; the unit has a restart policy, and a killed daemon returns
in a few seconds.

In both cases, make sure that no daemon process remains:

```bash
pgrep netclawd   # must print nothing
```

If `pgrep netclawd` finds a process, warn the user and abort.

### 2. Back up existing binaries

```bash
cp ~/.netclaw/bin/netclaw ~/.netclaw/bin/netclaw.bak
cp ~/.netclaw/bin/netclawd ~/.netclaw/bin/netclawd.bak
```

### 3. Publish from current worktree

**CRITICAL: You MUST use the EXACT flags below.** These match the production
CI pipeline in `.github/workflows/publish_release_binaries.yml`. Missing any
flag will produce broken binaries:

- `IncludeNativeLibrariesForSelfExtract=true` — **REQUIRED** or SQLite and
  other native libraries will not be bundled. The daemon will crash on startup
  with `TypeInitializationException` for `SqliteConnection`.
- `EnableCompressionInSingleFile=true` — Reduces binary size significantly.
- `PublishSingleFile=true` — Produces a single executable.
- `--self-contained true` — Bundles the .NET runtime.

```bash
dotnet publish src/Netclaw.Cli/Netclaw.Cli.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  /p:PublishSingleFile=true \
  /p:EnableCompressionInSingleFile=true \
  /p:IncludeNativeLibrariesForSelfExtract=true \
  -o /tmp/netclaw-publish

dotnet publish src/Netclaw.Daemon/Netclaw.Daemon.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  /p:PublishSingleFile=true \
  /p:EnableCompressionInSingleFile=true \
  /p:IncludeNativeLibrariesForSelfExtract=true \
  -o /tmp/netclawd-publish
```

**DO NOT simplify these commands.** DO NOT omit flags. DO NOT use `-p:` shorthand
(use `/p:` for clarity). The production pipeline uses these exact flags and any
deviation produces subtly broken binaries that appear to work but fail at runtime.

### 4. Copy system skills and swap binaries

Schemas are embedded in the CLI binary, so no separate schema copy is needed.

```bash
cp -r feeds/skills/.system/files/* ~/.netclaw/skills/.system/
```

### 5. Swap binaries

```bash
cp /tmp/netclaw-publish/netclaw ~/.netclaw/bin/netclaw
cp /tmp/netclawd-publish/netclawd ~/.netclaw/bin/netclawd
```

### 6. Verify

```bash
netclaw --version   # should show dev version with current commit hash
netclaw doctor      # should not show SQLite failures
```

### 7. Re-launch the daemon

The swap procedure stops the daemon in step 1 and never restarts it. You MUST
start it again before the install is usable — channels, webhooks, and the
doctor's daemon/MCP-connectivity checks all stay broken until the daemon is up.

If systemd manages the daemon (step 1), start the unit with systemd:

```bash
systemctl --user start netclaw.service
systemctl --user is-active netclaw.service   # must print "active"
netclaw daemon status                        # confirm it came up cleanly
```

If no systemd unit exists, use the CLI:

```bash
netclaw daemon start
netclaw daemon status   # confirm it came up cleanly
```

If `daemon status` shows a crash or the PID never appears, check
`~/.netclaw/logs/crash-*.log` — a missing `IncludeNativeLibrariesForSelfExtract`
flag in step 3 is the most common cause.

**Check the crash-log timestamp before you blame the new binary.** The old
daemon can write a crash log while it stops. An example is a SlackNet socket
"Failed to open socket" unobserved-task crash. Such a log does not show that
the new binary is broken. Compare the crash-log time with the time of the swap.
Only a crash log written after the new daemon started applies to the new binary.

## Restore Procedure

To revert to the original binaries, use the same stop and start method as the
swap. If systemd manages the daemon:

```bash
systemctl --user stop netclaw.service
pgrep netclawd   # must print nothing
cp ~/.netclaw/bin/netclaw.bak ~/.netclaw/bin/netclaw
cp ~/.netclaw/bin/netclawd.bak ~/.netclaw/bin/netclawd
systemctl --user start netclaw.service
systemctl --user is-active netclaw.service   # must print "active"
netclaw daemon status
```

If no systemd unit exists:

```bash
netclaw daemon stop 2>&1 || true
pgrep netclawd   # must print nothing
cp ~/.netclaw/bin/netclaw.bak ~/.netclaw/bin/netclaw
cp ~/.netclaw/bin/netclawd.bak ~/.netclaw/bin/netclawd
netclaw daemon start
netclaw daemon status
```

## Platform-Specific RIDs

| Platform | RID |
|----------|-----|
| Linux x64 | `linux-x64` |
| macOS Apple Silicon | `osx-arm64` |
| macOS Intel | `osx-x64` |
| Windows x64 | `win-x64` |

## Common Mistakes

| Mistake | Symptom | Fix |
|---------|---------|-----|
| Missing `IncludeNativeLibrariesForSelfExtract` | `TypeInitializationException` for `SqliteConnection`, memory health doctor check fails | Republish with all flags |
| Missing `EnableCompressionInSingleFile` | Binary is 2-3x larger than expected | Republish with all flags |
| Forgot to stop daemon before swap | Binary overwrite fails or daemon crashes | Always `netclaw daemon stop` first |
| Used `netclaw daemon stop` on a systemd-managed daemon | Systemd restarts the daemon in seconds; the copy fails with "Text file busy" or replaces a running binary | Use `systemctl --user stop netclaw.service`, then confirm `pgrep netclawd` prints nothing |
| Blamed the new binary for an old crash log | A crash log (for example, SlackNet "Failed to open socket") appears near the swap | Compare the crash-log time with the swap time; a log from before the new start is from the old daemon |
| Forgot to copy system skills | New/updated skills not available | Copy from `feeds/skills/.system/files/` |
| Used `-p:` instead of `/p:` | May work but inconsistent with CI | Use `/p:` to match production |
| Forgot to re-launch daemon after swap | Channels offline, webhooks dead, doctor reports daemon unreachable | `netclaw daemon start` — swap leaves daemon stopped from step 1 |
