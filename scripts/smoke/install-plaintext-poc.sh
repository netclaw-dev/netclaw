#!/usr/bin/env bash
# POC — plain-text release feed for install.sh
#
# Proves the #1791 design end-to-end WITHOUT touching the real CDN:
#   1. Builds a local mirror that serves the NEW feed shape:
#        /latest                  → "0.0.0"          (plain text)
#        /latest-prerelease       → "0.0.1-beta1"    (plain text)
#        /0.0.0/<component>-<version>-<rid>.tar.gz    (binary tarballs)
#        /0.0.0/checksums-<rid>.txt                   (per-RID checksums)
#   2. Runs scripts/install.sh against it without JSON tools.
#   3. Asserts: version resolved from /latest, assets downloaded
#      from deterministic URLs, checksums verified, binaries installed.
#
# Usage: bash scripts/smoke/install-plaintext-poc.sh

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
INSTALL_SH="$ROOT_DIR/scripts/install.sh"
VERSION="0.0.0"
BETA_VERSION="0.0.1-beta1"
# The installer only downloads the host's native RID; build archives for that
# one (no zip dependency needed).
case "$(uname -s):$(uname -m)" in
  Linux:x86_64|Linux:amd64) RID="linux-x64" ;;
  Linux:aarch64|Linux:arm64) RID="linux-arm64" ;;
  Darwin:arm64) RID="osx-arm64" ;;
  *) echo "POC: unsupported host $(uname -s)/$(uname -m)"; exit 1 ;;
esac

PASS=0
FAIL=0
pass() { echo "PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "FAIL: $1"; FAIL=$((FAIL + 1)); }

WORK="$(mktemp -d)"
SERVE="$WORK/serve"
mkdir -p "$SERVE" "$WORK/bin"
trap 'rm -rf "$WORK"' EXIT

sha256_of() { sha256sum "$1" | cut -d' ' -f1; }
indent() { while IFS= read -r line || [ -n "$line" ]; do printf '    %s\n' "$line"; done; }

# ── 1. Stand-in binaries + archives + checksums ──────────────────────────────
for name in netclaw netclawd; do
  cat > "$WORK/bin/$name" <<EOF
#!/usr/bin/env sh
echo "$name $VERSION-smoke"
EOF
  chmod +x "$WORK/bin/$name"
done

for ver in "$VERSION" "$BETA_VERSION"; do
  mkdir -p "$SERVE/$ver"
  for name in netclaw netclawd; do
    archive="$name-$ver-$RID.tar.gz"
    tar czf "$SERVE/$ver/$archive" -C "$WORK/bin" "$name"
    echo "$(sha256_of "$SERVE/$ver/$archive")  $archive  $(stat -c%s "$SERVE/$ver/$archive")" >> "$SERVE/$ver/checksums-$RID.txt"
  done
done

# ── 2. New feed surface: plain-text channel pointers ─────────────────────────
printf '%s' "$VERSION"      > "$SERVE/latest"
printf '%s' "$BETA_VERSION" > "$SERVE/latest-prerelease"

# ── 3. Serve locally ─────────────────────────────────────────────────────────
PORT="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); p=s.getsockname()[1]; s.close(); print(p)')"
BASE_URL="http://127.0.0.1:$PORT"
python3 -m http.server "$PORT" --bind 127.0.0.1 --directory "$SERVE" >/dev/null 2>&1 &
SERVER_PID=$!
trap 'kill $SERVER_PID 2>/dev/null || true; rm -rf "$WORK"' EXIT
if ! curl -sf --retry 30 --retry-delay 1 --retry-connrefused "$BASE_URL/latest" >/dev/null; then
  echo "FATAL: local server did not come up"
  exit 1
fi

# ── 4. Use a PATH that excludes JSON tools ───────────────────────────────────
NO_JSON_BIN="$WORK/no-json-bin"
mkdir -p "$NO_JSON_BIN"
for cmd in awk basename bash cat chmod cp curl cut dirname find grep gzip head mkdir mktemp rm sed sha256sum tar touch tr uname; do
  command_path=$(command -v "$cmd" 2>/dev/null || true)
  if [ -n "$command_path" ]; then
    ln -s "$command_path" "$NO_JSON_BIN/$cmd"
  fi
done

# ── 5. Dry run: version must resolve from /latest ─────────────────────────────
echo "=== dry run (stable, no JSON tools) ==="
set +e
dry_out=$(PATH="$NO_JSON_BIN" INSTALL_DIR="$WORK/dry-install" \
  FEED_BASE_URL="$BASE_URL" \
  bash "$INSTALL_SH" --dry-run 2>&1)
dry_rc=$?
set -e
echo "$dry_out" | indent
if [ "$dry_rc" -eq 0 ] \
    && echo "$dry_out" | grep -q "Resolved stable channel from $BASE_URL/latest" \
    && echo "$dry_out" | grep -q "Version: $VERSION" \
    && echo "$dry_out" | grep -q "DRY RUN: would install netclaw .*/$VERSION/netclaw-$VERSION-$RID.tar.gz" \
    && echo "$dry_out" | grep -q "DRY RUN: would install netclawd .*/$VERSION/netclawd-$VERSION-$RID.tar.gz"; then
  pass "dry-run: version resolved from plain-text /latest; deterministic asset URLs"
else
  fail "dry-run: plain-text path failed (exit=$dry_rc)"
fi

# ── 6. Real install: download + checksum verify + extract + install ──────────
echo "=== real install (stable, no JSON tools) ==="
INSTALL_HOME="$WORK/install-home"
INSTALL_DIR="$INSTALL_HOME/.netclaw/bin"
mkdir -p "$INSTALL_HOME"
set +e
install_out=$(PATH="$NO_JSON_BIN" HOME="$INSTALL_HOME" \
  FEED_BASE_URL="$BASE_URL" INSTALL_DIR="$INSTALL_DIR" \
  bash "$INSTALL_SH" 2>&1)
install_rc=$?
set -e
echo "$install_out" | indent
if [ "$install_rc" -ne 0 ]; then
  fail "real install: exited $install_rc"
else
  pass "real install: exited 0"
fi
for name in netclaw netclawd; do
  if [ -x "$INSTALL_DIR/$name" ] && "$INSTALL_DIR/$name" | grep -q "$name"; then
    pass "real install: $name installed and runnable"
  else
    fail "real install: $name missing/not executable/did not run"
  fi
done
if echo "$install_out" | grep -q "Verifying checksum"; then
  pass "real install: checksum verified against checksums-<rid>.txt"
else
  fail "real install: checksum was NOT verified"
fi

# A missing pointer must fail. The shell installer must not read a manifest.
POINTER_FILE="$SERVE/latest"
POINTER_BACKUP="$WORK/latest.backup"
mv "$POINTER_FILE" "$POINTER_BACKUP"
set +e
missing_pointer_out=$(PATH="$NO_JSON_BIN" INSTALL_DIR="$WORK/missing-pointer-install" \
  FEED_BASE_URL="$BASE_URL" \
  bash "$INSTALL_SH" daemon --dry-run 2>&1)
missing_pointer_rc=$?
set -e
mv "$POINTER_BACKUP" "$POINTER_FILE"
if [ "$missing_pointer_rc" -ne 0 ] \
    && echo "$missing_pointer_out" | grep -q "Failed to fetch release channel" \
    && [ ! -e "$WORK/missing-pointer-install" ]; then
  pass "security: missing pointer fails without a manifest fallback"
else
  fail "security: missing pointer did not fail closed"
  echo "$missing_pointer_out" | indent
fi

# A resolved pointer without a checksum must fail closed before any install.
CHECKSUM_FILE="$SERVE/$VERSION/checksums-$RID.txt"
CHECKSUM_BACKUP="$WORK/checksums-$RID.txt.backup"
mv "$CHECKSUM_FILE" "$CHECKSUM_BACKUP"
set +e
missing_checksum_out=$(PATH="$NO_JSON_BIN" INSTALL_DIR="$WORK/missing-checksum-install" \
  FEED_BASE_URL="$BASE_URL" \
  bash "$INSTALL_SH" daemon --dry-run 2>&1)
missing_checksum_rc=$?
set -e
mv "$CHECKSUM_BACKUP" "$CHECKSUM_FILE"
if [ "$missing_checksum_rc" -ne 0 ] \
    && echo "$missing_checksum_out" | grep -q "Failed to fetch checksum file" \
    && [ ! -e "$WORK/missing-checksum-install" ]; then
  pass "security: pointer path rejects a missing checksum"
else
  fail "security: pointer path accepted a missing checksum"
  echo "$missing_checksum_out" | indent
fi

# ── 7. Beta channel: resolves from /latest-prerelease ────────────────────────
echo "=== dry run (beta channel, no JSON tools) ==="
set +e
beta_out=$(PATH="$NO_JSON_BIN" INSTALL_DIR="$WORK/beta-install" \
  FEED_BASE_URL="$BASE_URL" \
  bash "$INSTALL_SH" --dry-run --channel beta 2>&1)
beta_rc=$?
set -e
echo "$beta_out" | indent
if [ "$beta_rc" -eq 0 ] \
    && echo "$beta_out" | grep -q "Resolved beta channel from $BASE_URL/latest-prerelease" \
    && echo "$beta_out" | grep -q "Version: $BETA_VERSION"; then
  pass "beta: version resolved from plain-text /latest-prerelease"
else
  fail "beta: plain-text prerelease path failed (exit=$beta_rc)"
fi

# ── 8. Pinned version: no feed resolution needed at all ──────────────────────
echo "=== dry run (pinned NETCLAW_VERSION, no JSON tools) ==="
set +e
pin_out=$(PATH="$NO_JSON_BIN" INSTALL_DIR="$WORK/pin-install" \
  FEED_BASE_URL="$BASE_URL" NETCLAW_VERSION="$BETA_VERSION" \
  bash "$INSTALL_SH" --dry-run 2>&1)
pin_rc=$?
set -e
echo "$pin_out" | indent
if [ "$pin_rc" -eq 0 ] && echo "$pin_out" | grep -q "Version: $BETA_VERSION"; then
  pass "pin: NETCLAW_VERSION used directly, no channel pointer fetched"
else
  fail "pin: pinned-version path failed (exit=$pin_rc)"
fi

echo ""
echo "Results: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
