---
name: local-binary-swap
description: Publish and swap local netclaw/netclawd binaries for live integration testing. Activate when user says "swap binaries", "publish locally", "test locally", or wants to test CLI/daemon changes against their running install.
---

# Local Binary Swap for Integration Testing

Publish the CLI and/or daemon from the current worktree and swap them into the
user's local `~/.netclaw/bin/` install for live testing.

## Safety Protocol

1. **Stop the daemon first** — never overwrite a running binary
2. **Back up originals** — always create `.bak` copies before overwriting
3. **Confirm with user** before swapping if unsure about their intent

## Procedure

### 1. Stop any running daemon

```bash
netclaw daemon stop 2>&1 || true
```

If a daemon is still running (check `pgrep netclawd`), warn the user and abort.

### 2. Back up existing binaries

```bash
cp ~/.netclaw/bin/netclaw ~/.netclaw/bin/netclaw.bak
cp ~/.netclaw/bin/netclawd ~/.netclaw/bin/netclawd.bak
```

### 3. Publish from current worktree

```bash
dotnet publish src/Netclaw.Cli/Netclaw.Cli.csproj \
  -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -o /tmp/netclaw-publish

dotnet publish src/Netclaw.Daemon/Netclaw.Daemon.csproj \
  -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -o /tmp/netclawd-publish
```

### 4. Swap binaries

```bash
cp /tmp/netclaw-publish/netclaw ~/.netclaw/bin/netclaw
cp /tmp/netclawd-publish/netclawd ~/.netclaw/bin/netclawd
```

### 5. Verify

```bash
netclaw --version   # should show dev version or updated output
```

## Restore Procedure

To revert to the original binaries:

```bash
cp ~/.netclaw/bin/netclaw.bak ~/.netclaw/bin/netclaw
cp ~/.netclaw/bin/netclawd.bak ~/.netclaw/bin/netclawd
```

## Notes

- Published binaries will be larger than release builds (self-contained includes
  the .NET runtime; release builds may use NativeAOT/trimming)
- Only swap what changed — if only CLI code changed, no need to republish the daemon
- The `.bak` files are not cleaned up automatically; remove them manually when done
- On macOS, use `-r osx-arm64` or `-r osx-x64` instead of `linux-x64`
