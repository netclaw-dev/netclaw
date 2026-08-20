#!/usr/bin/env bash
# install-smoke.sh — hermetic smoke test for scripts/install.sh
#
# Verifies the installer's platform detection and the
# download/checksum/extract/install path WITHOUT building or downloading the
# real product binary. Stand-in binaries (tiny shell scripts) and a plain-text
# feed served from localhost keep the test fast, offline, and free of any
# dependency on releases.netclaw.dev.
#
# Two layers of coverage:
#   1. Detection matrix — runs install.sh --dry-run under uname/sysctl shims to
#      assert every supported OS/arch maps to the right RID (and unsupported
#      ones are rejected). This runs on any host and is what would have caught
#      the original "Unsupported OS: darwin" bug.
#   2. Mechanical check — one real install of a stand-in archive on the host's
#      native RID, asserting download + checksum + tar extract + cp all work.
#
# Usage:    bash scripts/smoke/install-smoke.sh
# Requires: bash, tar, curl, python3, and sha256sum or shasum.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
INSTALL_SH="$ROOT_DIR/scripts/install.sh"
MANIFEST_GEN="$ROOT_DIR/feeds/scripts/generate-release-manifest.sh"
VERSION="0.0.0"          # stable → latest
BETA_VERSION="0.0.1-beta1"  # prerelease → latest-prerelease
RIDS="linux-x64 linux-arm64 osx-arm64"

PASS=0
FAIL=0
pass() { echo "PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "FAIL: $1"; FAIL=$((FAIL + 1)); }

# ── Temp workspace ───────────────────────────────────────────────────────────
WORK="$(mktemp -d)"
SERVE="$WORK/serve"
SHIM="$WORK/shim"
mkdir -p "$SERVE/$VERSION" "$SHIM" "$WORK/checksums" "$WORK/bin"

SERVER_PID=""
# generate-release-manifest.sh always writes to this fixed path; back up any
# pre-existing file and restore it on exit so the working tree is left clean.
# It also writes the plain-text channel pointers (latest, latest-prerelease).
MANIFEST_DEST="$ROOT_DIR/feeds/releases/manifest.json"
for feed_file in manifest.json latest latest-prerelease; do
  if [ -f "$ROOT_DIR/feeds/releases/$feed_file" ]; then
    cp "$ROOT_DIR/feeds/releases/$feed_file" "$WORK/$feed_file.backup"
  else
    touch "$WORK/$feed_file.absent"
  fi
done

cleanup() {
  if [ -n "$SERVER_PID" ]; then
    kill "$SERVER_PID" 2>/dev/null || true
  fi
  for feed_file in manifest.json latest latest-prerelease; do
    if [ -f "$WORK/$feed_file.backup" ]; then
      cp "$WORK/$feed_file.backup" "$ROOT_DIR/feeds/releases/$feed_file"
    elif [ -f "$WORK/$feed_file.absent" ]; then
      rm -f "$ROOT_DIR/feeds/releases/$feed_file"
    fi
  done
  rm -rf "$WORK"
}
trap cleanup EXIT

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  else
    shasum -a 256 "$1" | cut -d' ' -f1
  fi
}
size_of() { stat -c%s "$1" 2>/dev/null || stat -f%z "$1"; }
indent() { while IFS= read -r line || [ -n "$line" ]; do printf '    %s\n' "$line"; done; }

# ── 1. Stand-in binaries ─────────────────────────────────────────────────────
# The installer only cares about a file named `netclaw` / `netclawd` inside the
# archive — a one-line script is a sufficient stand-in and keeps this instant.
for name in netclaw netclawd; do
  cat > "$WORK/bin/$name" <<EOF
#!/usr/bin/env sh
echo "$name $VERSION-smoke"
EOF
  chmod +x "$WORK/bin/$name"
done

# ── 2. Package archives + checksums for every RID, for a stable AND a prerelease ─
# Two versions let us prove channel selection: the default install must resolve to
# the stable pointer, and --channel beta to the prerelease pointer.
for ver in "$VERSION" "$BETA_VERSION"; do
  mkdir -p "$SERVE/$ver" "$WORK/checksums-$ver"
  for rid in $RIDS; do
    cli="netclaw-$ver-$rid.tar.gz"
    daemon="netclawd-$ver-$rid.tar.gz"
    tar czf "$SERVE/$ver/$cli" -C "$WORK/bin" netclaw
    tar czf "$SERVE/$ver/$daemon" -C "$WORK/bin" netclawd
    {
      echo "$(sha256_of "$SERVE/$ver/$cli")  $cli  $(size_of "$SERVE/$ver/$cli")"
      echo "$(sha256_of "$SERVE/$ver/$daemon")  $daemon  $(size_of "$SERVE/$ver/$daemon")"
    } > "$WORK/checksums-$ver/checksums-$rid.txt"
  done
done

# ── 3. Pick a free port and generate the release feed ────────────────────────
PORT="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); p=s.getsockname()[1]; s.close(); print(p)')"
BASE_URL="http://127.0.0.1:$PORT"
# Start from a clean slate so the generator's latest/latestPrerelease are computed
# from only our two test versions (cleanup restores prior feed files).
rm -f "$MANIFEST_DEST"
# Run the REAL generator once per version; it accumulates releases[] and recomputes
# latest (newest stable) + latestPrerelease (newest of all) across both.
bash "$MANIFEST_GEN" "$VERSION"      "$WORK/checksums-$VERSION"      "$BASE_URL" >/dev/null
bash "$MANIFEST_GEN" "$BETA_VERSION" "$WORK/checksums-$BETA_VERSION" "$BASE_URL" >/dev/null

if [ "$(tr -d '\r\n' < "$ROOT_DIR/feeds/releases/latest")" = "$VERSION" ]; then
  pass "feed: latest pointer selects the newest stable version"
else
  fail "feed: latest pointer has an unexpected value"
fi
if [ "$(tr -d '\r\n' < "$ROOT_DIR/feeds/releases/latest-prerelease")" = "$BETA_VERSION" ]; then
  pass "feed: latest-prerelease pointer selects the newest version"
else
  fail "feed: latest-prerelease pointer has an unexpected value"
fi

cp "$ROOT_DIR/feeds/releases/latest" "$SERVE/latest"
cp "$ROOT_DIR/feeds/releases/latest-prerelease" "$SERVE/latest-prerelease"
for ver in "$VERSION" "$BETA_VERSION"; do
  cp "$WORK/checksums-$ver"/checksums-*.txt "$SERVE/$ver/"
done

# ── 4. Serve the plain-text feed + archives from localhost ───────────────────
python3 -m http.server "$PORT" --bind 127.0.0.1 --directory "$SERVE" >/dev/null 2>&1 &
SERVER_PID=$!
if ! curl -sf --retry 30 --retry-delay 1 --retry-connrefused \
     "$BASE_URL/latest" >/dev/null 2>&1; then
  echo "FATAL: local feed server did not come up on $BASE_URL"
  exit 1
fi

# ── 5. Detection matrix (install.sh --dry-run under uname/sysctl shims) ───────
cat > "$SHIM/uname" <<'EOF'
#!/bin/sh
case "$1" in
  -s) echo "${FAKE_OS}" ;;
  -m) echo "${FAKE_ARCH}" ;;
  *)  echo "fake-uname" ;;
esac
EOF
cat > "$SHIM/sysctl" <<'EOF'
#!/bin/sh
# install.sh only ever calls: sysctl -n sysctl.proc_translated
if [ "$1" = "-n" ] && [ "$2" = "sysctl.proc_translated" ]; then
  echo "${FAKE_TRANSLATED:-0}"
  exit 0
fi
exit 1
EOF
chmod +x "$SHIM/uname" "$SHIM/sysctl"

# check_detect <desc> <FAKE_OS> <FAKE_ARCH> <FAKE_TRANSLATED> <expect-regex> <expect-exit>
check_detect() {
  local desc="$1" fos="$2" farch="$3" ftrans="$4" expect="$5" want_rc="$6"
  local out rc
  set +e
  out=$(FAKE_OS="$fos" FAKE_ARCH="$farch" FAKE_TRANSLATED="$ftrans" \
        PATH="$SHIM:$PATH" \
        FEED_BASE_URL="$BASE_URL" \
        INSTALL_DIR="$WORK/should-not-exist" \
        bash "$INSTALL_SH" --dry-run 2>&1)
  rc=$?
  set -e
  if [ "$rc" -eq "$want_rc" ] && echo "$out" | grep -Eq "$expect"; then
    pass "detect: $desc"
  else
    fail "detect: $desc (exit=$rc, expected $want_rc matching /$expect/)"
    echo "$out" | indent
  fi
}

echo "=== platform detection matrix ==="
check_detect "Linux x86_64 -> linux-x64"        linux  x86_64  0 'DRY RUN: would install netclaw .*-linux-x64\.tar\.gz'  0
check_detect "Linux aarch64 -> linux-arm64"     linux  aarch64 0 'DRY RUN: would install netclaw .*-linux-arm64\.tar\.gz' 0
check_detect "macOS arm64 -> osx-arm64"         Darwin arm64   0 'DRY RUN: would install netclaw .*-osx-arm64\.tar\.gz'   0
check_detect "macOS x86_64 + Rosetta -> osx-arm64" Darwin x86_64 1 'DRY RUN: would install netclaw .*-osx-arm64\.tar\.gz' 0
check_detect "Intel Mac rejected"               Darwin x86_64  0 'Apple Silicon'    1
check_detect "unsupported OS rejected"          freebsd x86_64 0 'Unsupported OS'   1

set +e
invalid_path_out=$(INSTALL_DIR="$WORK/invalid:path" \
  FEED_BASE_URL="$BASE_URL" \
  bash "$INSTALL_SH" --dry-run 2>&1)
invalid_path_rc=$?
set -e
if [ "$invalid_path_rc" -ne 0 ] && echo "$invalid_path_out" | grep -q "cannot contain ':'"; then
  pass "PATH: unrepresentable Unix install directory rejected"
else
  fail "PATH: Unix install directory containing ':' was accepted"
fi

PHYSICAL_INSTALL="$WORK/physical:install"
SYMLINK_INSTALL="$WORK/safe-install-link"
SYMLINK_HOME="$WORK/symlink-home"
mkdir -p "$PHYSICAL_INSTALL" "$SYMLINK_HOME"
ln -s "$PHYSICAL_INSTALL" "$SYMLINK_INSTALL"
set +e
symlink_path_out=$(HOME="$SYMLINK_HOME" SHELL="$(command -v bash)" \
  INSTALL_DIR="$SYMLINK_INSTALL" FEED_BASE_URL="$BASE_URL" \
  bash "$INSTALL_SH" cli 2>&1)
symlink_path_rc=$?
set -e
if [ "$symlink_path_rc" -ne 0 ] \
    && echo "$symlink_path_out" | grep -q "cannot contain ':'" \
    && [ ! -e "$SYMLINK_HOME/.netclaw/env" ] \
    && [ ! -e "$SYMLINK_HOME/.bashrc" ] \
    && [ ! -e "$PHYSICAL_INSTALL/netclaw" ]; then
  pass "PATH: physical install path is validated before install or shell mutation"
else
  fail "PATH: unrepresentable symlink target was installed or persisted"
  echo "$symlink_path_out" | indent
fi

# The shell installer does not need a JSON parser. Run it from a PATH that has
# no JSON tools and serve no manifest file.
NO_JSON_BIN="$WORK/no-json-bin"
mkdir -p "$NO_JSON_BIN"
for cmd in awk bash curl cut dirname grep head mktemp rm sed sha256sum shasum tar tr uname; do
  command_path=$(command -v "$cmd" 2>/dev/null || true)
  if [ -n "$command_path" ]; then
    ln -s "$command_path" "$NO_JSON_BIN/$cmd"
  fi
done
set +e
no_json_out=$(PATH="$NO_JSON_BIN" \
  FEED_BASE_URL="$BASE_URL" \
  INSTALL_DIR="$WORK/no-json-install" \
  /bin/bash "$INSTALL_SH" --dry-run 2>&1)
no_json_rc=$?
set -e
if [ "$no_json_rc" -eq 0 ] \
    && echo "$no_json_out" | grep -q "Resolved stable channel from $BASE_URL/latest" \
    && echo "$no_json_out" | grep -q "DRY RUN: would install netclaw .*/$VERSION/" \
    && echo "$no_json_out" | grep -q "DRY RUN: would install netclawd .*/$VERSION/"; then
  pass "feed: shell installer works without JSON tools or a manifest"
else
  fail "feed: shell installer requires a JSON tool or manifest (exit=$no_json_rc)"
  echo "$no_json_out" | indent
fi

# Dry run must not create the install directory.
if [ -d "$WORK/should-not-exist" ]; then
  fail "dry-run: created an install directory (should install nothing)"
else
  pass "dry-run: installed nothing"
fi

# ── 6. Mechanical check: a real install on the host's native RID ─────────────
# Uses a temp HOME so shell integration writes to the temp dir, not the
# CI runner's real profile — and we can verify the RC was modified.
echo ""
echo "=== real install (host RID, stand-in archive) ==="
INSTALL_HOME="$WORK/installed-home"
INSTALL_DIR="$INSTALL_HOME/.netclaw/bin"
mkdir -p "$INSTALL_HOME"
set +e
install_out=$(HOME="$INSTALL_HOME" \
              FEED_BASE_URL="$BASE_URL" INSTALL_DIR="$INSTALL_DIR" \
              bash "$INSTALL_SH" 2>&1)
install_rc=$?
set -e
echo "$install_out" | indent

if [ "$install_rc" -ne 0 ]; then
  fail "install: exited $install_rc"
else
  pass "install: exited 0"
fi

for name in netclaw netclawd; do
  if [ -x "$INSTALL_DIR/$name" ] && "$INSTALL_DIR/$name" | grep -q "$name"; then
    pass "install: $name installed and runnable"
  else
    fail "install: $name missing, not executable, or did not run"
  fi
done

# Verify shell integration actually ran
INSTALL_ENV="$INSTALL_HOME/.netclaw/env"
if [ -f "$INSTALL_ENV" ]; then
  pass "real install: env script created"
else
  fail "real install: env script not found at $INSTALL_ENV"
fi

# The RC file depends on $SHELL — check whichever one was created
RC_MODIFIED=false
for rc in "$INSTALL_HOME/.bashrc" "$INSTALL_HOME/.zshrc" "$INSTALL_HOME/.profile" "$INSTALL_HOME/.config/fish/conf.d/netclaw.fish"; do
  if [ -f "$rc" ] && grep -qxF ". '$INSTALL_ENV'" "$rc" 2>/dev/null; then
    pass "real install: $(basename "$rc") sources env script"
    RC_MODIFIED=true
  fi
done
if [ "$RC_MODIFIED" = false ]; then
  fail "real install: no RC file sources env script (SHELL=$SHELL)"
fi

# ── 7. Release channel resolution (dry-run) ──────────────────────────────────
echo ""
echo "=== release channel resolution ==="

# assert_resolves <desc> <want-version> [extra install.sh args...]
# Optional NETCLAW_PIN env exercises the explicit-version-pin path. Asserts on the
# "Version: X" line install.sh prints after resolving the channel.
assert_resolves() {
  local desc="$1" want="$2"; shift 2
  local out rc
  set +e
  out=$(FEED_BASE_URL="$BASE_URL" \
        INSTALL_DIR="$WORK/should-not-exist" \
        NETCLAW_VERSION="${NETCLAW_PIN:-}" \
        bash "$INSTALL_SH" --dry-run "$@" 2>&1)
  rc=$?
  set -e
  if [ "$rc" -eq 0 ] && echo "$out" | grep -Eq "^  Version: ${want}$"; then
    pass "channel: $desc -> $want"
  else
    fail "channel: $desc (exit=$rc, expected 'Version: $want')"
    echo "$out" | indent
  fi
}

assert_resolves "default install -> latest stable"      "$VERSION"
assert_resolves "--channel stable -> latest stable"     "$VERSION"      --channel stable
assert_resolves "--channel beta -> latest prerelease"   "$BETA_VERSION" --channel beta
NETCLAW_PIN="$BETA_VERSION" \
  assert_resolves "NETCLAW_VERSION pin overrides channel" "$BETA_VERSION" --channel stable

# An unknown channel must fail loudly, not silently fall back to stable.
set +e
bad_out=$(FEED_BASE_URL="$BASE_URL" INSTALL_DIR="$WORK/should-not-exist" \
          bash "$INSTALL_SH" --dry-run --channel bogus 2>&1)
bad_rc=$?
set -e
if [ "$bad_rc" -ne 0 ] && echo "$bad_out" | grep -q "unknown channel"; then
  pass "channel: unknown value rejected"
else
  fail "channel: unknown value should fail loudly (exit=$bad_rc)"
  echo "$bad_out" | indent
fi

# ── 8. Config channel persistence ───────────────────────────────────────────
echo ""
echo "=== config channel persistence ==="

# 8a. Fresh install with --channel beta seeds a config file
FRESH_DIR="$WORK/fresh-beta"
FRESH_CONFIG_DIR="$WORK/fresh-beta-config/config"
set +e
fresh_out=$(FEED_BASE_URL="$BASE_URL" \
            INSTALL_DIR="$FRESH_DIR" \
            CONFIG_DIR="$FRESH_CONFIG_DIR" \
            bash "$INSTALL_SH" --channel beta --skip-shell 2>&1)
fresh_rc=$?
set -e
if [ "$fresh_rc" -eq 0 ] && [ -f "$FRESH_CONFIG_DIR/netclaw.json" ]; then
  if command -v jq >/dev/null 2>&1; then
    val=$(jq -r '.Daemon.UpdateChannel' "$FRESH_CONFIG_DIR/netclaw.json")
    if [ "$val" = "beta" ]; then
      pass "config: fresh --channel beta seeds config with UpdateChannel=beta"
    else
      fail "config: fresh --channel beta wrote UpdateChannel='$val' (expected 'beta')"
    fi
  else
    pass "config: fresh --channel beta created config file (no jq to verify contents)"
  fi
else
  fail "config: fresh --channel beta did not create config (exit=$fresh_rc)"
  echo "$fresh_out" | indent
fi

# 8b. --channel beta on existing config patches UpdateChannel
EXIST_DIR="$WORK/existing-beta"
EXIST_CONFIG_DIR="$WORK/existing-beta-config/config"
mkdir -p "$EXIST_CONFIG_DIR"
printf '{"configVersion":1,"Daemon":{"ExposureMode":"local"}}\n' > "$EXIST_CONFIG_DIR/netclaw.json"
set +e
exist_out=$(FEED_BASE_URL="$BASE_URL" \
            INSTALL_DIR="$EXIST_DIR" \
            CONFIG_DIR="$EXIST_CONFIG_DIR" \
            bash "$INSTALL_SH" --channel beta --skip-shell 2>&1)
exist_rc=$?
set -e
if [ "$exist_rc" -eq 0 ] && command -v jq >/dev/null 2>&1; then
  val=$(jq -r '.Daemon.UpdateChannel' "$EXIST_CONFIG_DIR/netclaw.json")
  mode=$(jq -r '.Daemon.ExposureMode' "$EXIST_CONFIG_DIR/netclaw.json")
  if [ "$val" = "beta" ] && [ "$mode" = "local" ]; then
    pass "config: --channel beta patches existing config, preserves other Daemon keys"
  else
    fail "config: --channel beta patch (UpdateChannel='$val', ExposureMode='$mode')"
  fi
else
  fail "config: --channel beta on existing config (exit=$exist_rc)"
  echo "$exist_out" | indent
fi

# 8c. Plain upgrade (no --channel) leaves existing beta config alone
NOFLAG_DIR="$WORK/noflag"
NOFLAG_CONFIG_DIR="$WORK/noflag-config/config"
mkdir -p "$NOFLAG_CONFIG_DIR"
printf '{"configVersion":1,"Daemon":{"UpdateChannel":"beta"}}\n' > "$NOFLAG_CONFIG_DIR/netclaw.json"
set +e
noflag_out=$(FEED_BASE_URL="$BASE_URL" \
             INSTALL_DIR="$NOFLAG_DIR" \
             CONFIG_DIR="$NOFLAG_CONFIG_DIR" \
             bash "$INSTALL_SH" --skip-shell 2>&1)
noflag_rc=$?
set -e
if [ "$noflag_rc" -eq 0 ] && command -v jq >/dev/null 2>&1; then
  val=$(jq -r '.Daemon.UpdateChannel' "$NOFLAG_CONFIG_DIR/netclaw.json")
  if [ "$val" = "beta" ]; then
    pass "config: plain upgrade preserves existing beta channel"
  else
    fail "config: plain upgrade changed UpdateChannel to '$val' (expected 'beta')"
  fi
else
  fail "config: plain upgrade (exit=$noflag_rc)"
  echo "$noflag_out" | indent
fi

# 8d. --channel stable on existing beta overwrites to stable
DOWNGRADE_DIR="$WORK/downgrade"
DOWNGRADE_CONFIG_DIR="$WORK/downgrade-config/config"
mkdir -p "$DOWNGRADE_CONFIG_DIR"
printf '{"configVersion":1,"Daemon":{"UpdateChannel":"beta"}}\n' > "$DOWNGRADE_CONFIG_DIR/netclaw.json"
set +e
down_out=$(FEED_BASE_URL="$BASE_URL" \
           INSTALL_DIR="$DOWNGRADE_DIR" \
           CONFIG_DIR="$DOWNGRADE_CONFIG_DIR" \
           bash "$INSTALL_SH" --channel stable --skip-shell 2>&1)
down_rc=$?
set -e
if [ "$down_rc" -eq 0 ] && command -v jq >/dev/null 2>&1; then
  val=$(jq -r '.Daemon.UpdateChannel' "$DOWNGRADE_CONFIG_DIR/netclaw.json")
  if [ "$val" = "stable" ]; then
    pass "config: --channel stable overwrites existing beta"
  else
    fail "config: --channel stable wrote UpdateChannel='$val' (expected 'stable')"
  fi
else
  fail "config: --channel stable on existing beta (exit=$down_rc)"
  echo "$down_out" | indent
fi

# ── 9. Shell integration (PATH automation) ───────────────────────────────────
echo ""
echo "=== shell integration ==="

assert_path_once() {
  local desc="$1" observed_path="$2" install_dir="$3"
  local count
  count=$(printf '%s' "$observed_path" | tr ':' '\n' | grep -cxF "$install_dir" || true)
  if [ "$count" -eq 1 ]; then
    pass "$desc: install directory appears exactly once on PATH"
  else
    fail "$desc: install directory appears $count times on PATH"
  fi
}

run_unix_installer() {
  local shell_path="$1" home="$2" install_dir="$3"
  shift 3
  SHELL="$shell_path" HOME="$home" \
    FEED_BASE_URL="$BASE_URL" INSTALL_DIR="$install_dir" \
    CONFIG_DIR="$home/.netclaw/config" \
    bash "$INSTALL_SH" "$@"
}

# Bash: run the generated startup path through Bash itself, then repeat the
# install to prove both profile mutation and PATH evaluation are idempotent.
BASH_HOME="$WORK/shell-bash"
BASH_INSTALL="$BASH_HOME/netclaw install's/bin"
mkdir -p "$BASH_HOME"
if [ "$(uname -s)" = "Darwin" ]; then
  BASH_RC="$BASH_HOME/.bash_profile"
  printf '# existing bash profile' > "$BASH_RC"
  printf '# profile must remain untouched\n' > "$BASH_HOME/.profile"
else
  BASH_RC="$BASH_HOME/.bashrc"
  printf '# existing bash rc' > "$BASH_RC"
fi

if run_unix_installer "$(command -v bash)" "$BASH_HOME" "$BASH_INSTALL" >/dev/null \
    && run_unix_installer "$(command -v bash)" "$BASH_HOME" "$BASH_INSTALL" >/dev/null; then
  BASH_INSTALL_PHYSICAL=$(cd "$BASH_INSTALL" && pwd -P)
  bash_path=$(PATH="/usr/bin:/bin" HOME="$BASH_HOME" \
    bash --noprofile --rcfile "$BASH_RC" -i -c 'printf "%s" "$PATH"' 2>/dev/null)
  assert_path_once "bash" "$bash_path" "$BASH_INSTALL_PHYSICAL"
  bash_empty_path=$(PATH="" HOME="$BASH_HOME" \
    /bin/bash --noprofile --rcfile "$BASH_RC" -i -c 'printf "%s" "$PATH"' 2>/dev/null)
  if [ "$bash_empty_path" = "$BASH_INSTALL_PHYSICAL" ]; then
    pass "bash: empty PATH does not introduce a current-directory entry"
  else
    fail "bash: empty PATH produced '$bash_empty_path'"
  fi
  source_count=$(grep -cF "$BASH_HOME/.netclaw/env" "$BASH_RC" || true)
  if [ "$source_count" -eq 1 ]; then
    pass "bash: profile source line is idempotent"
  else
    fail "bash: profile contains $source_count netclaw source lines"
  fi
  if [ "$(uname -s)" = "Darwin" ] && ! grep -qF netclaw "$BASH_HOME/.profile"; then
    pass "bash-macos: existing .bash_profile wins over .profile"
  fi
else
  fail "bash: installer failed"
fi

# Zsh: resolve a non-exported ZDOTDIR from .zshenv, then execute the selected
# startup file under zsh so a Bash-compatible false positive cannot pass.
if command -v zsh >/dev/null 2>&1; then
  ZSH_EXECUTABLE="$(command -v zsh)"
  ZSH_HOME="$WORK/shell-zsh"
  ZDOT_DIR="$ZSH_HOME/custom-zdotdir"
  ZSH_INSTALL="$ZSH_HOME/netclaw install's/bin"
  mkdir -p "$ZDOT_DIR"
  printf "ZDOTDIR='%s'\n" "$ZDOT_DIR" > "$ZSH_HOME/.zshenv"
  printf '# existing zsh config\n' > "$ZDOT_DIR/.zshrc"
  if (unset ZDOTDIR; run_unix_installer "$ZSH_EXECUTABLE" "$ZSH_HOME" "$ZSH_INSTALL" >/dev/null) \
      && (unset ZDOTDIR; run_unix_installer "$ZSH_EXECUTABLE" "$ZSH_HOME" "$ZSH_INSTALL" >/dev/null); then
    ZSH_INSTALL_PHYSICAL=$(cd "$ZSH_INSTALL" && pwd -P)
    zsh_path=$(PATH="/usr/bin:/bin" ZDOTDIR="$ZDOT_DIR" \
      "$ZSH_EXECUTABLE" -f -c 'source "$ZDOTDIR/.zshrc"; print -rn -- "$PATH"')
    assert_path_once "zsh" "$zsh_path" "$ZSH_INSTALL_PHYSICAL"
    if [ ! -e "$ZSH_HOME/.zshrc" ]; then
      pass "zsh: non-exported ZDOTDIR is authoritative"
    else
      fail "zsh: installer touched ~/.zshrc despite ZDOTDIR"
    fi
  else
    fail "zsh: installer failed"
  fi
else
  echo "SKIP: zsh executable not available"
fi

# Fish owns a native conf.d file. Execute that file with fish, not Bash.
if command -v fish >/dev/null 2>&1; then
  FISH_EXECUTABLE="$(command -v fish)"
  FISH_HOME="$WORK/shell-fish"
  FISH_INSTALL="$FISH_HOME/netclaw install's/bin"
  FISH_RC="$FISH_HOME/.config/fish/conf.d/netclaw.fish"
  if XDG_CONFIG_HOME="$FISH_HOME/.config" \
      run_unix_installer "$FISH_EXECUTABLE" "$FISH_HOME" "$FISH_INSTALL" >/dev/null \
      && XDG_CONFIG_HOME="$FISH_HOME/.config" \
      run_unix_installer "$FISH_EXECUTABLE" "$FISH_HOME" "$FISH_INSTALL" >/dev/null; then
    FISH_INSTALL_PHYSICAL=$(cd "$FISH_INSTALL" && pwd -P)
    fish_path=$(PATH="/usr/bin:/bin" "$FISH_EXECUTABLE" --no-config -c \
      "source '$FISH_RC'; string join : -- \$PATH")
    assert_path_once "fish" "$fish_path" "$FISH_INSTALL_PHYSICAL"
  else
    fail "fish: installer failed"
  fi
else
  echo "SKIP: fish executable not available"
fi

# Opt-out under a supported shell must print a self-contained command instead
# of referring to an env file that was not made.
MANUAL_HOME="$WORK/shell-skip"
MANUAL_INSTALL="$MANUAL_HOME/netclaw install's/bin"
mkdir -p "$MANUAL_HOME"
manual_out=$(run_unix_installer "$(command -v bash)" "$MANUAL_HOME" "$MANUAL_INSTALL" --skip-shell)
manual_command=$(printf '%s\n' "$manual_out" | sed -n 's/^  \{0,4\}\(export PATH=.*\)$/\1/p' | head -1)
if [ -n "$manual_command" ] && [ ! -e "$MANUAL_HOME/.netclaw/env" ]; then
  MANUAL_INSTALL_PHYSICAL=$(cd "$MANUAL_INSTALL" && pwd -P)
  manual_path=$(PATH="/usr/bin:/bin" bash -c "$manual_command; printf '%s' \"\$PATH\"")
  assert_path_once "skip" "$manual_path" "$MANUAL_INSTALL_PHYSICAL"
  manual_empty_path=$(PATH="" /bin/bash -c "$manual_command; printf '%s' \"\$PATH\"")
  if [ "$manual_empty_path" = "$MANUAL_INSTALL_PHYSICAL" ]; then
    pass "skip: manual command preserves an empty PATH without adding current directory"
  else
    fail "skip: manual command produced '$manual_empty_path' from an empty PATH"
  fi
else
  fail "skip: missing usable manual PATH command or created shell files"
fi

# Unsupported shells get shell-neutral guidance; emitting Bash syntax for an
# arbitrary shell would make the suggested command actively misleading.
UNKNOWN_HOME="$WORK/shell-unknown"
UNKNOWN_INSTALL="$UNKNOWN_HOME/netclaw install's/bin"
unknown_out=$(run_unix_installer /bin/unknownshell "$UNKNOWN_HOME" "$UNKNOWN_INSTALL")
UNKNOWN_INSTALL_PHYSICAL=$(cd "$UNKNOWN_INSTALL" && pwd -P)
if echo "$unknown_out" | grep -qF "$UNKNOWN_INSTALL_PHYSICAL" \
    && ! echo "$unknown_out" | grep -q 'export PATH=' \
    && [ ! -e "$UNKNOWN_HOME/.netclaw/env" ]; then
  pass "unknown: guidance is shell-neutral and no shell files are created"
else
  fail "unknown: guidance is shell-specific or shell files were created"
fi

# ── Summary ──────────────────────────────────────────────────────────────────
echo ""
echo "Results: $PASS passed, $FAIL failed"
if [ "$FAIL" -gt 0 ]; then
  echo "install smoke: FAILED"
  exit 1
fi
echo "install smoke: PASSED"
