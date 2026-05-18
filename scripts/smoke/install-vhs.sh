#!/usr/bin/env bash
# Ensure VHS (charmbracelet/vhs) is installed for the interactive tape harness.
#
# Installation strategy:
#   - If `vhs` is already on PATH, do nothing.
#   - On Linux x86_64: install vhs from the upstream release with SHA256 verification,
#     and ensure ttyd / ffmpeg are present (apt-get if available).
#   - On macOS: install vhs / ttyd / ffmpeg via Homebrew.
#   - Other platforms: print install hints and fail.
#
# Pin the VHS version + SHA256 here. Bumping vhs requires updating both.
# Refresh by running:
#   curl -fsSL https://github.com/charmbracelet/vhs/releases/download/v${VHS_VERSION}/checksums.txt \
#     | grep "Linux_x86_64.tar.gz"

set -euo pipefail

VHS_VERSION="${VHS_VERSION:-0.11.0}"
# SHA256 of vhs_0.11.0_Linux_x86_64.tar.gz from the upstream checksums.txt.
# When bumping VHS_VERSION, refresh this with the curl command in the header
# comment above. The pin matters for screenshot regression: a VHS bump can
# change the bundled font/renderer and silently drift every baseline PNG.
VHS_LINUX_X64_SHA256="${VHS_LINUX_X64_SHA256:-99cb634587eaae0473c1ea377db80c3a048c27f99fe0a7febb1a1e8cb7ee5009}"
# 0.11.0 is the minimum that supports `Wait+Screen /pattern/`.
# Earlier versions (e.g. 0.8.0) parse `Wait` as an unknown command.

have() { command -v "$1" >/dev/null 2>&1; }

if have vhs; then
  installed="$(vhs --version 2>/dev/null | awk '{print $NF}' | sed 's/^v//')"
  echo "vhs ${installed:-unknown} already installed at $(command -v vhs)"
  exit 0
fi

uname_s="$(uname -s)"
uname_m="$(uname -m)"

case "${uname_s}/${uname_m}" in
  Linux/x86_64)
    : # supported below
    ;;
  Darwin/*)
    # macOS install path. Unlike Linux there is no pinned VHS version + SHA:
    # the macOS leg does not run the screenshot comparison (Linux is the
    # canonical screenshot platform), so renderer/font drift across VHS
    # versions does not affect it. macOS VHS only drives the flow tapes,
    # which assert on exit codes — not pixels — so `brew`'s latest is fine.
    if ! have brew; then
      cat >&2 <<'EOF'
ERROR: vhs is not installed and Homebrew ('brew') is not on PATH.
GitHub macOS runners ship Homebrew; for a local macOS run install it
from https://brew.sh first, then re-run. To install vhs manually:
    brew install vhs ttyd ffmpeg coreutils
EOF
      exit 1
    fi
    # coreutils provides `gtimeout` — stock macOS has no `timeout`, and
    # run-native-tape.sh needs one to bound the vhs run.
    echo "Installing vhs + runtime deps (ttyd, ffmpeg, coreutils) via Homebrew..."
    brew install vhs ttyd ffmpeg coreutils
    if ! have vhs; then
      echo "ERROR: 'brew install vhs' completed but 'vhs' is still not on PATH." >&2
      exit 1
    fi
    echo "vhs installed at $(command -v vhs)"
    vhs --version || true
    exit 0
    ;;
  *)
    cat >&2 <<EOF
ERROR: vhs is not installed and automatic install is not supported on ${uname_s}/${uname_m}.
See https://github.com/charmbracelet/vhs#installation for manual instructions.
EOF
    exit 1
    ;;
esac

# Linux x86_64 path.

if have apt-get; then
  echo "Installing vhs runtime deps (ttyd, ffmpeg) via apt-get..."
  # Force non-interactive mode so apt never tries to open whiptail dialogs
  # (e.g., the kernel-upgrade prompt) when running on a CI runner or under
  # an SSH/agent session.
  export DEBIAN_FRONTEND=noninteractive
  if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
    apt-get update
    apt-get install -y --no-install-recommends ttyd ffmpeg ca-certificates curl
  else
    sudo -E apt-get update
    sudo -E apt-get install -y --no-install-recommends ttyd ffmpeg ca-certificates curl
  fi
else
  for dep in ttyd ffmpeg curl; do
    if ! have "$dep"; then
      echo "WARNING: '$dep' is not on PATH and apt-get is unavailable. vhs will likely fail." >&2
    fi
  done
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

archive="$tmp/vhs.tar.gz"
url="https://github.com/charmbracelet/vhs/releases/download/v${VHS_VERSION}/vhs_${VHS_VERSION}_Linux_x86_64.tar.gz"

echo "Downloading vhs v${VHS_VERSION} from ${url}..."
curl -fsSL "$url" -o "$archive"

if [[ "${VHS_LINUX_X64_SHA256}" != "SKIP_VERIFY" ]]; then
  echo "Verifying SHA256..."
  echo "${VHS_LINUX_X64_SHA256}  ${archive}" | sha256sum -c -
else
  echo "WARNING: SHA256 verification skipped (VHS_LINUX_X64_SHA256=SKIP_VERIFY)." >&2
  echo "         Set VHS_LINUX_X64_SHA256 to the upstream checksum to enable verification." >&2
fi

tar -xzf "$archive" -C "$tmp"

# Archive layout: vhs_<version>_Linux_x86_64/vhs
binary="$(find "$tmp" -type f -name vhs -perm -u+x | head -n1)"
if [[ -z "${binary:-}" ]]; then
  echo "ERROR: vhs binary not found in extracted archive." >&2
  exit 1
fi

dest="${VHS_INSTALL_PATH:-/usr/local/bin/vhs}"
if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
  install -m 0755 "$binary" "$dest"
else
  sudo install -m 0755 "$binary" "$dest"
fi

echo "vhs v${VHS_VERSION} installed at ${dest}"
vhs --version || true
