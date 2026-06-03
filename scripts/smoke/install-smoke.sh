#!/usr/bin/env bash
# install-smoke.sh — hermetic smoke test for scripts/install.sh
#
# Verifies the installer's platform detection, manifest parsing, and the
# download/checksum/extract/install path WITHOUT building or downloading the
# real product binary. Stand-in binaries (tiny shell scripts) and a manifest
# served from localhost keep the test fast, offline, and free of any
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
VERSION="0.0.0"          # stable → manifest.latest
BETA_VERSION="0.0.1-beta1"  # prerelease → manifest.latestPrerelease
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
MANIFEST_DEST="$ROOT_DIR/feeds/releases/manifest.json"
MANIFEST_BACKUP="$WORK/manifest.json.backup"
MANIFEST_PREEXISTING=false
if [ -f "$MANIFEST_DEST" ]; then
  cp "$MANIFEST_DEST" "$MANIFEST_BACKUP"
  MANIFEST_PREEXISTING=true
fi

cleanup() {
  if [ -n "$SERVER_PID" ]; then
    kill "$SERVER_PID" 2>/dev/null || true
  fi
  if [ "$MANIFEST_PREEXISTING" = true ]; then
    cp "$MANIFEST_BACKUP" "$MANIFEST_DEST"
  else
    rm -f "$MANIFEST_DEST"
  fi
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
# the stable (manifest.latest), --channel beta to the prerelease (latestPrerelease).
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

# ── 3. Pick a free port and generate the manifest ────────────────────────────
PORT="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); p=s.getsockname()[1]; s.close(); print(p)')"
BASE_URL="http://127.0.0.1:$PORT"
# Start from a clean slate so the generator's latest/latestPrerelease are computed
# from only our two test versions (cleanup restores any pre-existing manifest).
rm -f "$MANIFEST_DEST"
# Run the REAL generator once per version; it accumulates releases[] and recomputes
# latest (newest stable) + latestPrerelease (newest of all) across both.
bash "$MANIFEST_GEN" "$VERSION"      "$WORK/checksums-$VERSION"      "$BASE_URL" >/dev/null
bash "$MANIFEST_GEN" "$BETA_VERSION" "$WORK/checksums-$BETA_VERSION" "$BASE_URL" >/dev/null
cp "$MANIFEST_DEST" "$SERVE/manifest.json"

# ── 4. Serve the manifest + archives from localhost ──────────────────────────
python3 -m http.server "$PORT" --bind 127.0.0.1 --directory "$SERVE" >/dev/null 2>&1 &
SERVER_PID=$!
if ! curl -sf --retry 30 --retry-delay 1 --retry-connrefused \
     "$BASE_URL/manifest.json" >/dev/null 2>&1; then
  echo "FATAL: local manifest server did not come up on $BASE_URL"
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
        MANIFEST_URL="$BASE_URL/manifest.json" \
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

# Dry run must not create the install directory.
if [ -d "$WORK/should-not-exist" ]; then
  fail "dry-run: created an install directory (should install nothing)"
else
  pass "dry-run: installed nothing"
fi

# ── 6. Mechanical check: a real install on the host's native RID ─────────────
echo ""
echo "=== real install (host RID, stand-in archive) ==="
INSTALL_DIR="$WORK/installed"
set +e
install_out=$(MANIFEST_URL="$BASE_URL/manifest.json" INSTALL_DIR="$INSTALL_DIR" \
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
  out=$(MANIFEST_URL="$BASE_URL/manifest.json" \
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
bad_out=$(MANIFEST_URL="$BASE_URL/manifest.json" INSTALL_DIR="$WORK/should-not-exist" \
          bash "$INSTALL_SH" --dry-run --channel bogus 2>&1)
bad_rc=$?
set -e
if [ "$bad_rc" -ne 0 ] && echo "$bad_out" | grep -q "unknown channel"; then
  pass "channel: unknown value rejected"
else
  fail "channel: unknown value should fail loudly (exit=$bad_rc)"
  echo "$bad_out" | indent
fi

# ── Summary ──────────────────────────────────────────────────────────────────
echo ""
echo "Results: $PASS passed, $FAIL failed"
if [ "$FAIL" -gt 0 ]; then
  echo "install smoke: FAILED"
  exit 1
fi
echo "install smoke: PASSED"
