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

# A developer value would send every case below to a real home directory.
unset NETCLAW_HOME

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
INSTALL_SH="$ROOT_DIR/scripts/install.sh"
UNINSTALL_SH="$ROOT_DIR/scripts/uninstall.sh"
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
# Octal permission bits. GNU stat (Linux) and BSD stat (macOS) disagree on flags.
file_mode() { stat -c%a "$1" 2>/dev/null || stat -f%Lp "$1"; }
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
# NETCLAW_HOME is the only variable that moves the config directory, the same
# way it moves NetclawPaths in the CLI and the daemon. Each case runs with its
# own HOME so a wrong directory shows up as a file under that HOME.
echo ""
echo "=== config channel persistence ==="

hash_of() { sha256_of "$1"; }

# run_home_install <fake-home> <netclaw-home-or-empty> <install-args...>
# An empty second argument runs the installer with NETCLAW_HOME unset.
run_home_install() {
  local fake_home="$1" nc_home="$2"
  shift 2
  mkdir -p "$fake_home" "$WORK/cwd"
  if [ -n "$nc_home" ]; then
    (cd "$WORK/cwd" && SHELL="${INSTALL_SHELL:-/bin/sh}" HOME="$fake_home" NETCLAW_HOME="$nc_home" \
      FEED_BASE_URL="$BASE_URL" bash "$INSTALL_SH" "$@" 2>&1)
  else
    (cd "$WORK/cwd" && SHELL="${INSTALL_SHELL:-/bin/sh}" HOME="$fake_home" \
      FEED_BASE_URL="$BASE_URL" bash "$INSTALL_SH" "$@" 2>&1)
  fi
}

# 8a. Fresh install with --channel beta seeds a config file under NETCLAW_HOME
FRESH_HOME="$WORK/fresh-beta-home"
FRESH_FAKE="$WORK/fresh-beta-user"
set +e
fresh_out=$(INSTALL_DIR="$WORK/fresh-beta" run_home_install "$FRESH_FAKE" "$FRESH_HOME" --channel beta --skip-shell)
fresh_rc=$?
set -e
if [ "$fresh_rc" -eq 0 ] && [ -f "$FRESH_HOME/config/netclaw.json" ] && [ ! -e "$FRESH_FAKE/.netclaw" ]; then
  if echo "$fresh_out" | grep -qF "Created $FRESH_HOME/config/netclaw.json with UpdateChannel 'beta'"; then
    pass "config: fresh --channel beta seeds \$NETCLAW_HOME/config and leaves \$HOME/.netclaw absent"
  else
    fail "config: fresh --channel beta did not print the created-file line"
    echo "$fresh_out" | indent
  fi
  if command -v jq >/dev/null 2>&1; then
    val=$(jq -r '.Daemon.UpdateChannel' "$FRESH_HOME/config/netclaw.json")
    if [ "$val" = "beta" ]; then
      pass "config: fresh --channel beta seeds config with UpdateChannel=beta"
    else
      fail "config: fresh --channel beta wrote UpdateChannel='$val' (expected 'beta')"
    fi
  fi
else
  fail "config: fresh --channel beta did not seed \$NETCLAW_HOME/config (exit=$fresh_rc)"
  echo "$fresh_out" | indent
fi

# 8b. --channel beta on existing config patches only the file under NETCLAW_HOME
EXIST_HOME="$WORK/existing-beta-home"
EXIST_FAKE="$WORK/existing-beta-user"
mkdir -p "$EXIST_HOME/config" "$EXIST_FAKE/.netclaw/config"
printf '{"configVersion":1,"Daemon":{"ExposureMode":"local"}}\n' > "$EXIST_HOME/config/netclaw.json"
printf '{"configVersion":1,"Daemon":{"ExposureMode":"public"}}\n' > "$EXIST_FAKE/.netclaw/config/netclaw.json"
decoy_hash=$(hash_of "$EXIST_FAKE/.netclaw/config/netclaw.json")
set +e
exist_out=$(INSTALL_DIR="$WORK/existing-beta" run_home_install "$EXIST_FAKE" "$EXIST_HOME" --channel beta --skip-shell)
exist_rc=$?
set -e
if [ "$exist_rc" -eq 0 ] && command -v jq >/dev/null 2>&1; then
  val=$(jq -r '.Daemon.UpdateChannel' "$EXIST_HOME/config/netclaw.json")
  mode=$(jq -r '.Daemon.ExposureMode' "$EXIST_HOME/config/netclaw.json")
  if [ "$val" = "beta" ] && [ "$mode" = "local" ]; then
    pass "config: --channel beta patches existing config, preserves other Daemon keys"
  else
    fail "config: --channel beta patch (UpdateChannel='$val', ExposureMode='$mode')"
  fi
  if [ "$(hash_of "$EXIST_FAKE/.netclaw/config/netclaw.json")" = "$decoy_hash" ]; then
    pass "config: --channel beta leaves the \$HOME/.netclaw config alone when NETCLAW_HOME is set"
  else
    fail "config: --channel beta changed the \$HOME/.netclaw config"
  fi
else
  fail "config: --channel beta on existing config (exit=$exist_rc)"
  echo "$exist_out" | indent
fi

# 8c. Plain upgrade (no --channel) leaves existing beta config alone
NOFLAG_HOME="$WORK/noflag-home"
NOFLAG_FAKE="$WORK/noflag-user"
mkdir -p "$NOFLAG_HOME/config"
printf '{"configVersion":1,"Daemon":{"UpdateChannel":"beta"}}\n' > "$NOFLAG_HOME/config/netclaw.json"
noflag_hash=$(hash_of "$NOFLAG_HOME/config/netclaw.json")
set +e
noflag_out=$(INSTALL_DIR="$WORK/noflag" run_home_install "$NOFLAG_FAKE" "$NOFLAG_HOME" --skip-shell)
noflag_rc=$?
set -e
if [ "$noflag_rc" -eq 0 ] && [ "$(hash_of "$NOFLAG_HOME/config/netclaw.json")" = "$noflag_hash" ] \
    && [ ! -e "$NOFLAG_FAKE/.netclaw" ]; then
  pass "config: plain upgrade changes no config file"
else
  fail "config: plain upgrade changed a config file or created \$HOME/.netclaw (exit=$noflag_rc)"
  echo "$noflag_out" | indent
fi

# 8d. --channel stable on existing beta overwrites to stable
DOWNGRADE_HOME="$WORK/downgrade-home"
DOWNGRADE_FAKE="$WORK/downgrade-user"
mkdir -p "$DOWNGRADE_HOME/config"
printf '{"configVersion":1,"Daemon":{"UpdateChannel":"beta"}}\n' > "$DOWNGRADE_HOME/config/netclaw.json"
set +e
down_out=$(INSTALL_DIR="$WORK/downgrade" run_home_install "$DOWNGRADE_FAKE" "$DOWNGRADE_HOME" --channel stable --skip-shell)
down_rc=$?
set -e
if [ "$down_rc" -eq 0 ] && command -v jq >/dev/null 2>&1; then
  val=$(jq -r '.Daemon.UpdateChannel' "$DOWNGRADE_HOME/config/netclaw.json")
  if [ "$val" = "stable" ]; then
    pass "config: --channel stable overwrites existing beta"
  else
    fail "config: --channel stable wrote UpdateChannel='$val' (expected 'stable')"
  fi
else
  fail "config: --channel stable on existing beta (exit=$down_rc)"
  echo "$down_out" | indent
fi

# 8e. The install directory and the shell env file follow NETCLAW_HOME
BIN_HOME="$WORK/bin-home"
BIN_FAKE="$WORK/bin-user"
set +e
bin_out=$(INSTALL_SHELL="$(command -v bash)" run_home_install "$BIN_FAKE" "$BIN_HOME" --channel beta)
bin_rc=$?
set -e
if [ "$bin_rc" -eq 0 ] && [ -x "$BIN_HOME/bin/netclaw" ] && [ -f "$BIN_HOME/env" ] \
    && [ ! -e "$BIN_FAKE/.netclaw" ]; then
  pass "home: default install directory and env script follow NETCLAW_HOME"
else
  fail "home: install directory or env script ignored NETCLAW_HOME (exit=$bin_rc)"
  echo "$bin_out" | indent
fi

# 8f. With NETCLAW_HOME unset the installer still uses $HOME/.netclaw
UNSET_FAKE="$WORK/unset-user"
set +e
unset_out=$(INSTALL_DIR="$WORK/unset" run_home_install "$UNSET_FAKE" "" --channel beta --skip-shell)
unset_rc=$?
set -e
if [ "$unset_rc" -eq 0 ] && [ -f "$UNSET_FAKE/.netclaw/config/netclaw.json" ]; then
  pass "home: unset NETCLAW_HOME seeds \$HOME/.netclaw/config"
else
  fail "home: unset NETCLAW_HOME did not seed \$HOME/.netclaw/config (exit=$unset_rc)"
  echo "$unset_out" | indent
fi

# 8g. NETCLAW_HOME forms. Each one lands where NetclawPaths puts it: it expands
# ~, $HOME, ${HOME}, and treats a blank value as unset. A relative path
# resolves against the working directory (a subdirectory of $WORK/cwd here).
check_home_form() {
  local desc="$1" fake_home="$2" value="$3" expected_config="$4" out rc
  set +e
  out=$(INSTALL_DIR="$WORK/forms-bin" run_home_install "$fake_home" "$value" --channel beta --skip-shell)
  rc=$?
  set -e
  if [ "$rc" -eq 0 ] && [ -f "$expected_config/netclaw.json" ]; then
    pass "home: $desc"
  else
    fail "home: $desc (exit=$rc, expected $expected_config/netclaw.json)"
    echo "$out" | indent
  fi
}
FORMS_FAKE="$WORK/forms-user"
check_home_form "NETCLAW_HOME=~/nc"        "$FORMS_FAKE-tilde"  '~/nc'        "$FORMS_FAKE-tilde/nc/config"
check_home_form "NETCLAW_HOME=\$HOME/nc"   "$FORMS_FAKE-dollar" '$HOME/nc'    "$FORMS_FAKE-dollar/nc/config"
check_home_form "NETCLAW_HOME=\${HOME}/nc" "$FORMS_FAKE-brace"  '${HOME}/nc'  "$FORMS_FAKE-brace/nc/config"
check_home_form "NETCLAW_HOME=\$home/nc is case-insensitive" "$FORMS_FAKE-lower" '$home/nc' "$FORMS_FAKE-lower/nc/config"
check_home_form "NETCLAW_HOME=rel/nc is relative to the working directory" "$FORMS_FAKE-rel" 'rel/nc' "$WORK/cwd/rel/nc/config"
check_home_form "blank NETCLAW_HOME means unset" "$FORMS_FAKE-blank" '   ' "$FORMS_FAKE-blank/.netclaw/config"

check_home_form "NETCLAW_HOME=%USERPROFILE%/nc expands to the home directory" "$FORMS_FAKE-profile" '%USERPROFILE%/nc' "$FORMS_FAKE-profile/nc/config"
check_home_form "NETCLAW_HOME=%userprofile%/nc is case-insensitive" "$FORMS_FAKE-profile-lower" '%userprofile%/nc' "$FORMS_FAKE-profile-lower/nc/config"

# 8h. A fresh install with NETCLAW_HOME set uses the new layout: bin, env, and config all sit
# under NETCLAW_HOME, and sourcing the env script puts that bin directory on PATH.
FRESH_HOME="$WORK/fresh-nc-home"
FRESH_FAKE="$WORK/fresh-user"
set +e
fresh_layout_out=$(INSTALL_SHELL="$(command -v bash)" run_home_install "$FRESH_FAKE" "$FRESH_HOME" --channel beta)
fresh_layout_rc=$?
set -e
fresh_bin_physical=""
[ -d "$FRESH_HOME/bin" ] && fresh_bin_physical="$(cd "$FRESH_HOME/bin" && pwd -P)"
fresh_on_path=$(HOME="$FRESH_FAKE" PATH="/usr/bin:/bin" sh -c ". '$FRESH_HOME/env'; command -v netclaw" 2>/dev/null || true)
if [ "$fresh_layout_rc" -eq 0 ] \
    && [ "$fresh_on_path" = "$fresh_bin_physical/netclaw" ] \
    && [ -f "$FRESH_HOME/config/netclaw.json" ] \
    && [ ! -e "$FRESH_FAKE/.netclaw" ]; then
  pass "home: fresh install with NETCLAW_HOME uses <home>/bin, <home>/env, and <home>/config"
else
  fail "home: fresh install with NETCLAW_HOME did not use the new layout (exit=$fresh_layout_rc, on PATH: '$fresh_on_path')"
  echo "$fresh_layout_out" | indent
fi

# 8i. An existing install at the legacy default location is upgraded in place, even when
# NETCLAW_HOME is set. The env script stays at $HOME/.netclaw/env and its PATH entry still
# names the legacy bin directory. Only the config directory follows NETCLAW_HOME.
LEGACY_FAKE="$WORK/legacy-user"
LEGACY_NC_HOME="$WORK/legacy-nc-home"
LEGACY_BIN="$LEGACY_FAKE/.netclaw/bin"
mkdir -p "$LEGACY_BIN"
for name in netclaw netclawd; do
  printf '#!/usr/bin/env sh\necho "%s old"\n' "$name" > "$LEGACY_BIN/$name"
  chmod +x "$LEGACY_BIN/$name"
done
set +e
legacy_out=$(INSTALL_SHELL="$(command -v bash)" run_home_install "$LEGACY_FAKE" "$LEGACY_NC_HOME" --channel beta)
legacy_rc=$?
set -e
legacy_bin_physical="$(cd "$LEGACY_BIN" && pwd -P)"
legacy_on_path=$(HOME="$LEGACY_FAKE" PATH="/usr/bin:/bin" sh -c ". '$LEGACY_FAKE/.netclaw/env'; command -v netclaw" 2>/dev/null || true)
if [ "$legacy_rc" -eq 0 ] \
    && [ "$("$LEGACY_BIN/netclaw")" = "netclaw $VERSION-smoke" ] \
    && [ "$("$LEGACY_BIN/netclawd")" = "netclawd $VERSION-smoke" ] \
    && [ "$legacy_on_path" = "$legacy_bin_physical/netclaw" ] \
    && [ ! -e "$LEGACY_NC_HOME/bin" ] \
    && [ ! -e "$LEGACY_NC_HOME/env" ] \
    && [ -f "$LEGACY_NC_HOME/config/netclaw.json" ] \
    && [ ! -e "$LEGACY_FAKE/.netclaw/config" ] \
    && echo "$legacy_out" | grep -qF "$LEGACY_BIN"; then
  pass "home: existing legacy install is upgraded in place and the old binary stays on PATH"
else
  fail "home: existing legacy install was not upgraded in place (exit=$legacy_rc, on PATH: '$legacy_on_path')"
  echo "$legacy_out" | indent
fi

# 8j. The install says where it installs and why, so an operator is not surprised by the location.
if echo "$legacy_out" | grep -qE 'Existing install found at .*/\.netclaw/bin'; then
  pass "home: an in-place upgrade names the existing install directory"
else
  fail "home: an in-place upgrade does not name the existing install directory"
  echo "$legacy_out" | indent
fi

# 8k. INSTALL_DIR always wins over the existing install.
EXPLICIT_FAKE="$WORK/explicit-user"
EXPLICIT_DIR="$WORK/explicit-bin"
mkdir -p "$EXPLICIT_FAKE/.netclaw/bin"
printf '#!/usr/bin/env sh\necho "netclaw old"\n' > "$EXPLICIT_FAKE/.netclaw/bin/netclaw"
chmod +x "$EXPLICIT_FAKE/.netclaw/bin/netclaw"
set +e
explicit_out=$(INSTALL_DIR="$EXPLICIT_DIR" run_home_install "$EXPLICIT_FAKE" "$WORK/explicit-nc-home" --skip-shell)
explicit_rc=$?
set -e
if [ "$explicit_rc" -eq 0 ] \
    && [ -x "$EXPLICIT_DIR/netclaw" ] \
    && [ "$("$EXPLICIT_FAKE/.netclaw/bin/netclaw")" = "netclaw old" ]; then
  pass "home: INSTALL_DIR wins over an existing install"
else
  fail "home: INSTALL_DIR did not win over an existing install (exit=$explicit_rc)"
  echo "$explicit_out" | indent
fi

# 8l. A dry run reports the in-place location and changes nothing.
DRY_FAKE="$WORK/dry-user"
mkdir -p "$DRY_FAKE/.netclaw/bin"
printf '#!/usr/bin/env sh\necho "netclaw old"\n' > "$DRY_FAKE/.netclaw/bin/netclaw"
chmod +x "$DRY_FAKE/.netclaw/bin/netclaw"
set +e
dry_out=$(run_home_install "$DRY_FAKE" "$WORK/dry-nc-home" --dry-run)
dry_rc=$?
set -e
if [ "$dry_rc" -eq 0 ] \
    && echo "$dry_out" | grep -qF "Install dir: $DRY_FAKE/.netclaw/bin" \
    && [ ! -e "$WORK/dry-nc-home" ] \
    && [ "$("$DRY_FAKE/.netclaw/bin/netclaw")" = "netclaw old" ]; then
  pass "home: dry run reports the in-place install directory and changes nothing"
else
  fail "home: dry run did not report the in-place install directory (exit=$dry_rc)"
  echo "$dry_out" | indent
fi

# 8m. An install that already sits in $NETCLAW_HOME/bin is upgraded there, even when a stale
# install also sits in $HOME/.netclaw/bin. The legacy directory is not touched, and the env
# script of NETCLAW_HOME puts the upgraded binary on PATH.
BOTH_FAKE="$WORK/both-user"
BOTH_HOME="$WORK/both-nc-home"
for bin_dir in "$BOTH_FAKE/.netclaw/bin" "$BOTH_HOME/bin"; do
  mkdir -p "$bin_dir"
  for name in netclaw netclawd; do
    printf '#!/usr/bin/env sh\necho "%s old"\n' "$name" > "$bin_dir/$name"
    chmod +x "$bin_dir/$name"
  done
done
set +e
both_out=$(INSTALL_SHELL="$(command -v bash)" run_home_install "$BOTH_FAKE" "$BOTH_HOME" --channel beta)
both_rc=$?
set -e
both_bin_physical="$(cd "$BOTH_HOME/bin" && pwd -P)"
both_on_path=$(HOME="$BOTH_FAKE" PATH="/usr/bin:/bin" sh -c ". '$BOTH_HOME/env'; command -v netclaw" 2>/dev/null || true)
if [ "$both_rc" -eq 0 ] \
    && [ "$("$BOTH_HOME/bin/netclaw")" = "netclaw $VERSION-smoke" ] \
    && [ "$("$BOTH_HOME/bin/netclawd")" = "netclawd $VERSION-smoke" ] \
    && [ "$("$BOTH_FAKE/.netclaw/bin/netclaw")" = "netclaw old" ] \
    && [ "$("$BOTH_FAKE/.netclaw/bin/netclawd")" = "netclawd old" ] \
    && [ "$both_on_path" = "$both_bin_physical/netclaw" ] \
    && [ ! -e "$BOTH_FAKE/.netclaw/env" ] \
    && echo "$both_out" | grep -qF "Install dir: $BOTH_HOME/bin"; then
  pass "home: an install in NETCLAW_HOME/bin is upgraded there and a stale legacy install stays untouched"
else
  fail "home: an install in NETCLAW_HOME/bin was not upgraded in place (exit=$both_rc, on PATH: '$both_on_path')"
  echo "$both_out" | indent
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
    bash "$INSTALL_SH" "$@"
}

# Bash: run the generated startup path through Bash itself, then repeat the
# install to prove both profile mutation and PATH evaluation are idempotent.
BASH_HOME="$WORK/shell bash's"
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
    && bash_install_out=$(run_unix_installer "$(command -v bash)" "$BASH_HOME" "$BASH_INSTALL"); then
  BASH_INSTALL_PHYSICAL=$(cd "$BASH_INSTALL" && pwd -P)
  bash_current_command=$(printf '%s\n' "$bash_install_out" | sed -n 's/^  \(\. .*\)$/\1/p' | head -1)
  if printf '%s\n' "$bash_install_out" | grep -qxF "To use Netclaw in this terminal now, run:" \
      && [ -n "$bash_current_command" ]; then
    pass "bash: prints a quoted Netclaw env command"
  else
    fail "bash: current-terminal instruction is missing or incorrect"
  fi
  if bash_current_path=$(PATH="/usr/bin:/bin" HOME="$BASH_HOME" \
      bash --noprofile --norc -c "$bash_current_command; $bash_current_command; printf '%s' \"\$PATH\""); then
    assert_path_once "bash-current" "$bash_current_path" "$BASH_INSTALL_PHYSICAL"
  else
    fail "bash: displayed current-terminal command failed"
  fi
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
  source_count=$(grep -cxF "$bash_current_command" "$BASH_RC" || true)
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
  ZSH_HOME="$WORK/shell zsh's"
  ZDOT_DIR="$ZSH_HOME/custom-zdotdir"
  ZSH_INSTALL="$ZSH_HOME/netclaw install's/bin"
  mkdir -p "$ZDOT_DIR"
  printf 'ZDOTDIR="%s"\n' "$ZDOT_DIR" > "$ZSH_HOME/.zshenv"
  printf '# existing zsh config\n' > "$ZDOT_DIR/.zshrc"
  if (unset ZDOTDIR; run_unix_installer "$ZSH_EXECUTABLE" "$ZSH_HOME" "$ZSH_INSTALL" >/dev/null) \
      && zsh_install_out=$(unset ZDOTDIR; run_unix_installer "$ZSH_EXECUTABLE" "$ZSH_HOME" "$ZSH_INSTALL"); then
    ZSH_INSTALL_PHYSICAL=$(cd "$ZSH_INSTALL" && pwd -P)
    zsh_current_command=$(printf '%s\n' "$zsh_install_out" | sed -n 's/^  \(\. .*\)$/\1/p' | head -1)
    if printf '%s\n' "$zsh_install_out" | grep -qxF "To use Netclaw in this terminal now, run:" \
        && [ -n "$zsh_current_command" ]; then
      pass "zsh: prints a quoted Netclaw env command"
    else
      fail "zsh: current-terminal instruction is missing or incorrect"
    fi
    if zsh_current_path=$(PATH="/usr/bin:/bin" HOME="$ZSH_HOME" \
        "$ZSH_EXECUTABLE" -f -c "$zsh_current_command; $zsh_current_command; print -rn -- \"\$PATH\""); then
      assert_path_once "zsh-current" "$zsh_current_path" "$ZSH_INSTALL_PHYSICAL"
    else
      fail "zsh: displayed current-terminal command failed"
    fi
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
  FISH_HOME="$WORK/shell fish's"
  FISH_INSTALL="$FISH_HOME/netclaw install's/bin"
  FISH_RC="$FISH_HOME/.config/fish/conf.d/netclaw.fish"
  if XDG_CONFIG_HOME="$FISH_HOME/.config" \
      run_unix_installer "$FISH_EXECUTABLE" "$FISH_HOME" "$FISH_INSTALL" >/dev/null \
      && fish_install_out=$(XDG_CONFIG_HOME="$FISH_HOME/.config" \
          run_unix_installer "$FISH_EXECUTABLE" "$FISH_HOME" "$FISH_INSTALL"); then
    FISH_INSTALL_PHYSICAL=$(cd "$FISH_INSTALL" && pwd -P)
    fish_current_command=$(printf '%s\n' "$fish_install_out" | sed -n 's/^  \(source .*\)$/\1/p' | head -1)
    if printf '%s\n' "$fish_install_out" | grep -qxF "To use Netclaw in this terminal now, run:" \
        && [ -n "$fish_current_command" ]; then
      pass "fish: prints a quoted Netclaw config command"
    else
      fail "fish: current-terminal instruction is missing or incorrect"
    fi
    if fish_current_path=$(PATH="/usr/bin:/bin" "$FISH_EXECUTABLE" --no-config -c \
        "$fish_current_command; $fish_current_command; string join : -- \$PATH"); then
      assert_path_once "fish-current" "$fish_current_path" "$FISH_INSTALL_PHYSICAL"
    else
      fail "fish: displayed current-terminal command failed"
    fi
    fish_path=$(PATH="/usr/bin:/bin" "$FISH_EXECUTABLE" --no-config -c \
      "$fish_current_command; string join : -- \$PATH")
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

# ── 10. Uninstall round-trip ──────────────────────────────────────────────────
# The highest-value test: install into a throwaway HOME, snapshot the tree,
# uninstall, snapshot again, and assert the tree is restored. A test that only
# checks "the files I expected are gone" misses the case where uninstall
# deletes the right file but damages the shell profile.
echo ""
echo "=== uninstall round-trip ==="

# Snapshot every file under a directory as "path sha256", sorted. Directories
# are included so an empty leftover directory is visible. BSD sort (macOS) has
# no -z flag, so paths are newline-delimited; the test tree has no newlines.
snapshot_tree() {
  local root="$1"
  ( cd "$root" && find . -mindepth 1 \( -type f -o -type d \) -print \
      | LC_ALL=C sort \
      | while IFS= read -r entry; do
          if [ -f "$entry" ]; then
            printf '%s %s\n' "$entry" "$(sha256_of "$entry")"
          else
            printf '%s DIR\n' "$entry"
          fi
        done )
}

run_uninstaller() {
  local home="$1" install_dir="$2"
  shift 2
  # Pin the shell to bash so the uninstaller resolves the same rc file the
  # installer wrote. Inheriting $SHELL would pick zsh on some macOS runners.
  HOME="$home" SHELL="$(command -v bash)" \
    bash "$UNINSTALL_SH" --install-dir "$install_dir" "$@"
}

# Find the rc file the installer actually modified. Linux uses .bashrc;
# macOS uses the first existing of .bash_profile, .bash_login, .profile.
find_installed_rc() {
  local home="$1" candidate
  for candidate in "$home/.bashrc" "$home/.bash_profile" "$home/.bash_login" "$home/.profile"; do
    if [ -f "$candidate" ] && grep -qF "netclaw shell setup" "$candidate" 2>/dev/null; then
      printf '%s' "$candidate"
      return 0
    fi
  done
  return 1
}

# 10a. Round-trip: install, uninstall, and compare against the pre-install tree.
RT_HOME="$WORK/roundtrip"
RT_INSTALL="$RT_HOME/netclaw/bin"
mkdir -p "$RT_HOME/netclaw"
printf '# pre-existing bash rc\n' > "$RT_HOME/.bashrc"
printf '# pre-existing profile\n' > "$RT_HOME/.profile"

BEFORE_TREE="$(snapshot_tree "$RT_HOME")"

if run_unix_installer "$(command -v bash)" "$RT_HOME" "$RT_INSTALL" >/dev/null 2>&1; then
  pass "roundtrip: install succeeded"
else
  fail "roundtrip: install failed"
fi

INSTALLED_TREE="$(snapshot_tree "$RT_HOME")"
if [ "$BEFORE_TREE" != "$INSTALLED_TREE" ]; then
  pass "roundtrip: install changed the tree"
else
  fail "roundtrip: install changed nothing"
fi

if run_uninstaller "$RT_HOME" "$RT_INSTALL" >/dev/null 2>&1; then
  pass "roundtrip: uninstall succeeded"
else
  fail "roundtrip: uninstall failed"
fi

AFTER_TREE="$(snapshot_tree "$RT_HOME")"
if [ "$BEFORE_TREE" = "$AFTER_TREE" ]; then
  pass "roundtrip: tree restored byte-for-byte"
else
  fail "roundtrip: tree not restored"
  echo "  --- expected ---"
  printf '%s\n' "$BEFORE_TREE" | indent
  echo "  --- actual ---"
  printf '%s\n' "$AFTER_TREE" | indent
fi

# 10b. The data directory survives a standard uninstall.
RT_DATA="$RT_HOME/.netclaw/config/netclaw.json"
mkdir -p "$(dirname "$RT_DATA")"
printf '{"configVersion":1}\n' > "$RT_DATA"
run_uninstaller "$RT_HOME" "$RT_INSTALL" >/dev/null 2>&1
if [ -f "$RT_DATA" ]; then
  pass "roundtrip: standard uninstall keeps the data directory"
else
  fail "roundtrip: standard uninstall deleted the data directory"
fi

# 10c. --purge removes it.
run_uninstaller "$RT_HOME" "$RT_INSTALL" --purge --yes >/dev/null 2>&1
if [ ! -e "$RT_HOME/.netclaw" ]; then
  pass "roundtrip: --purge removes the data directory"
else
  fail "roundtrip: --purge left the data directory"
fi

# 10d. Idempotency: a second uninstall exits 0.
if run_uninstaller "$RT_HOME" "$RT_INSTALL" >/dev/null 2>&1; then
  pass "roundtrip: second uninstall exits 0"
else
  fail "roundtrip: second uninstall failed"
fi

# 10e. --dry-run changes nothing.
DRY_HOME="$WORK/dryrun"
DRY_INSTALL="$DRY_HOME/netclaw/bin"
mkdir -p "$DRY_HOME"
printf '# pre-existing bash rc\n' > "$DRY_HOME/.bashrc"
run_unix_installer "$(command -v bash)" "$DRY_HOME" "$DRY_INSTALL" >/dev/null 2>&1
DRY_BEFORE="$(snapshot_tree "$DRY_HOME")"
run_uninstaller "$DRY_HOME" "$DRY_INSTALL" --dry-run >/dev/null 2>&1
DRY_AFTER="$(snapshot_tree "$DRY_HOME")"
if [ "$DRY_BEFORE" = "$DRY_AFTER" ]; then
  pass "roundtrip: --dry-run changes nothing"
else
  fail "roundtrip: --dry-run changed the tree"
fi

# 10f. --purge without --yes and no terminal refuses and preserves data.
REFUSE_HOME="$WORK/refuse"
REFUSE_INSTALL="$REFUSE_HOME/netclaw/bin"
mkdir -p "$REFUSE_HOME"
printf '# pre-existing bash rc\n' > "$REFUSE_HOME/.bashrc"
run_unix_installer "$(command -v bash)" "$REFUSE_HOME" "$REFUSE_INSTALL" >/dev/null 2>&1
mkdir -p "$REFUSE_HOME/.netclaw/config"
printf '{"configVersion":1}\n' > "$REFUSE_HOME/.netclaw/config/netclaw.json"
# set -e would abort on the non-zero exit, so capture it inside a subshell
# that always succeeds.
REFUSE_EXIT=0
HOME="$REFUSE_HOME" SHELL=/bin/bash bash "$UNINSTALL_SH" \
  --install-dir "$REFUSE_INSTALL" --purge </dev/null >/dev/null 2>&1 || REFUSE_EXIT=$?
if [ "$REFUSE_EXIT" -eq 3 ]; then
  pass "roundtrip: --purge without --yes exits 3"
else
  fail "roundtrip: --purge without --yes exited $REFUSE_EXIT (expected 3)"
fi
if [ -f "$REFUSE_HOME/.netclaw/config/netclaw.json" ]; then
  pass "roundtrip: refused purge preserved the data directory"
else
  fail "roundtrip: refused purge deleted the data directory"
fi

# 10f-2. Uninstall preserves the shell profile's file mode. The uninstaller
# rewrites the rc file through a mktemp temp file, which is created 0600; without
# an explicit chmod the user's ~/.bashrc silently becomes 0600. The tree
# snapshot compares content only, so this needs its own assertion.
MODE_HOME="$WORK/uninstall-mode"
MODE_INSTALL="$MODE_HOME/netclaw/bin"
mkdir -p "$MODE_HOME"
printf '# pre-existing bash rc\n' > "$MODE_HOME/.bashrc"
chmod 644 "$MODE_HOME/.bashrc"
MODE_BEFORE="$(file_mode "$MODE_HOME/.bashrc")"
run_unix_installer "$(command -v bash)" "$MODE_HOME" "$MODE_INSTALL" >/dev/null 2>&1
run_uninstaller "$MODE_HOME" "$MODE_INSTALL" >/dev/null 2>&1
MODE_AFTER="$(file_mode "$MODE_HOME/.bashrc")"
if [ -n "$MODE_BEFORE" ] && [ "$MODE_BEFORE" = "$MODE_AFTER" ]; then
  pass "roundtrip: uninstall preserves the shell profile mode ($MODE_AFTER)"
else
  fail "roundtrip: uninstall changed the shell profile mode ($MODE_BEFORE -> $MODE_AFTER)"
fi

# 10g. --skip-shell leaves the shell profile untouched.
SKIP_HOME="$WORK/uninstall-skip"
SKIP_INSTALL="$SKIP_HOME/netclaw/bin"
mkdir -p "$SKIP_HOME"
printf '# pre-existing bash rc\n' > "$SKIP_HOME/.bashrc"
printf '# pre-existing profile\n' > "$SKIP_HOME/.profile"
run_unix_installer "$(command -v bash)" "$SKIP_HOME" "$SKIP_INSTALL" >/dev/null 2>&1
if SKIP_RC="$(find_installed_rc "$SKIP_HOME")"; then
  SKIP_RC_BEFORE="$(sha256_of "$SKIP_RC")"
  run_uninstaller "$SKIP_HOME" "$SKIP_INSTALL" --skip-shell >/dev/null 2>&1
  SKIP_RC_AFTER="$(sha256_of "$SKIP_RC")"
  if [ "$SKIP_RC_BEFORE" = "$SKIP_RC_AFTER" ]; then
    pass "roundtrip: --skip-shell leaves the shell profile untouched"
  else
    fail "roundtrip: --skip-shell modified the shell profile"
  fi
else
  fail "roundtrip: could not locate the rc file for the --skip-shell test"
fi

# 10h. A duplicated comment line is fully removed.
DUP_HOME="$WORK/uninstall-dup"
DUP_INSTALL="$DUP_HOME/netclaw/bin"
mkdir -p "$DUP_HOME"
printf '# pre-existing bash rc\n' > "$DUP_HOME/.bashrc"
printf '# pre-existing profile\n' > "$DUP_HOME/.profile"
run_unix_installer "$(command -v bash)" "$DUP_HOME" "$DUP_INSTALL" >/dev/null 2>&1
# Duplicate the comment line the installer wrote, in whichever rc file it used.
if DUP_RC="$(find_installed_rc "$DUP_HOME")"; then
  DUP_SOURCE_LINE="$(grep -F '. ' "$DUP_RC" | head -1)"
  printf '# netclaw shell setup\n%s\n' "$DUP_SOURCE_LINE" >> "$DUP_RC"
  run_uninstaller "$DUP_HOME" "$DUP_INSTALL" >/dev/null 2>&1
  if grep -qF "netclaw shell setup" "$DUP_RC"; then
    fail "roundtrip: duplicated comment line survived uninstall"
  else
    pass "roundtrip: duplicated comment line removed"
  fi
  if grep -qF "# pre-existing" "$DUP_RC"; then
    pass "roundtrip: unrelated rc line preserved after duplicate removal"
  else
    fail "roundtrip: unrelated rc line lost after duplicate removal"
  fi
else
  fail "roundtrip: could not locate the rc file the installer modified"
fi

# 10i. A stray comment with no source line is not ours to remove.
STRAY_HOME="$WORK/uninstall-stray"
STRAY_INSTALL="$STRAY_HOME/netclaw/bin"
mkdir -p "$STRAY_HOME"
printf '# pre-existing bash rc\n# netclaw shell setup\n# unrelated line\n' > "$STRAY_HOME/.bashrc"
run_uninstaller "$STRAY_HOME" "$STRAY_INSTALL" >/dev/null 2>&1
if grep -qF "netclaw shell setup" "$STRAY_HOME/.bashrc"; then
  pass "roundtrip: stray comment preserved"
else
  fail "roundtrip: stray comment removed"
fi

# 10j. The uninstaller refuses to run from inside the install directory.
CWD_HOME="$WORK/uninstall-cwd"
CWD_INSTALL="$CWD_HOME/netclaw/bin"
mkdir -p "$CWD_HOME"
printf '# pre-existing bash rc\n' > "$CWD_HOME/.bashrc"
run_unix_installer "$(command -v bash)" "$CWD_HOME" "$CWD_INSTALL" >/dev/null 2>&1
CWD_EXIT=0
( cd "$CWD_INSTALL" && HOME="$CWD_HOME" SHELL=/bin/bash \
    bash "$UNINSTALL_SH" --install-dir "$CWD_INSTALL" >/dev/null 2>&1 ) || CWD_EXIT=$?
if [ "$CWD_EXIT" -eq 4 ]; then
  pass "roundtrip: refuses to run from inside the install directory"
else
  fail "roundtrip: cwd guard exited $CWD_EXIT (expected 4)"
fi

# 10k. An empty --install-dir that is not a Netclaw install is not deleted.
# --install-dir accepts any path, so "the directory is empty" is not proof
# that we emptied it. Deleting it would destroy an unrelated directory.
EMPTY_HOME="$WORK/uninstall-empty"
EMPTY_INSTALL="$EMPTY_HOME/not-a-netclaw-install"
mkdir -p "$EMPTY_INSTALL"
run_uninstaller "$EMPTY_HOME" "$EMPTY_INSTALL" >/dev/null 2>&1
if [ -d "$EMPTY_INSTALL" ]; then
  pass "roundtrip: an already-empty install directory is not deleted"
else
  fail "roundtrip: an already-empty install directory was deleted"
fi

# 10l. The same guard holds when --install-dir is the home directory itself.
SELF_HOME="$WORK/uninstall-self"
mkdir -p "$SELF_HOME"
run_uninstaller "$SELF_HOME" "$SELF_HOME" >/dev/null 2>&1
if [ -d "$SELF_HOME" ]; then
  pass "roundtrip: --install-dir pointing at HOME is not deleted"
else
  fail "roundtrip: --install-dir pointing at HOME was deleted"
fi

# 10m. A real install still removes its own now-empty directory.
CLEAN_HOME="$WORK/uninstall-clean"
CLEAN_INSTALL="$CLEAN_HOME/netclaw/bin"
mkdir -p "$CLEAN_HOME"
printf '# pre-existing bash rc\n' > "$CLEAN_HOME/.bashrc"
run_unix_installer "$(command -v bash)" "$CLEAN_HOME" "$CLEAN_INSTALL" >/dev/null 2>&1
run_uninstaller "$CLEAN_HOME" "$CLEAN_INSTALL" >/dev/null 2>&1
if [ ! -d "$CLEAN_INSTALL" ]; then
  pass "roundtrip: a real install removes its own emptied directory"
else
  fail "roundtrip: a real install left its emptied directory behind"
fi

# ── Summary ──────────────────────────────────────────────────────────────────
echo ""
echo "Results: $PASS passed, $FAIL failed"
if [ "$FAIL" -gt 0 ]; then
  echo "install smoke: FAILED"
  exit 1
fi
echo "install smoke: PASSED"
