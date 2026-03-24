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

### 4. Copy system skills to install location

The built-in skills are compiled into the binary and copied at first boot, but
for immediate testing, copy them explicitly:

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

## Restore Procedure

To revert to the original binaries:

```bash
netclaw daemon stop 2>&1 || true
cp ~/.netclaw/bin/netclaw.bak ~/.netclaw/bin/netclaw
cp ~/.netclaw/bin/netclawd.bak ~/.netclaw/bin/netclawd
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
| Forgot to copy system skills | New/updated skills not available | Copy from `feeds/skills/.system/files/` |
| Used `-p:` instead of `/p:` | May work but inconsistent with CI | Use `/p:` to match production |
