#!/usr/bin/env bash
# Ensure VHS (charmbracelet/vhs) is installed for the interactive tape harness.
#
# Installation strategy:
#   - If the pinned vhs and its runtime tools exist, do nothing.
#   - On Linux x86_64: install vhs and ttyd from pinned upstream releases.
#     Install the imageio-ffmpeg static binary. Verify all files with SHA256.
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
TTYD_VERSION="${TTYD_VERSION:-1.7.7}"
# SHA256 of ttyd.x86_64 from the upstream SHA256SUMS file.
TTYD_LINUX_X64_SHA256="${TTYD_LINUX_X64_SHA256:-8a217c968aba172e0dbf3f34447218dc015bc4d5e59bf51db2f2cd12b7be4f55}"
# SHA256 of the imageio-ffmpeg 0.6.0 manylinux2014 x86_64 wheel from PyPI.
FFMPEG_WHEEL_SHA256="${FFMPEG_WHEEL_SHA256:-c7e46fcec401dd990405049d2e2f475e2b397779df2519b544b8aab515195282}"
# 0.11.0 is the minimum that supports `Wait+Screen /pattern/`.
# Earlier versions (e.g. 0.8.0) parse `Wait` as an unknown command.

have() { command -v "$1" >/dev/null 2>&1; }

if have vhs; then
  installed="$(vhs --version 2>/dev/null | sed -n 's/.*version v\([^ ]*\).*/\1/p')"
  if [[ "$installed" == "$VHS_VERSION" ]] && have ttyd && have ffmpeg && have python3; then
    echo "vhs ${installed} and its runtime tools are already installed."
    exit 0
  fi
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
for dep in curl python3; do
  if ! have "$dep"; then
    echo "ERROR: '$dep' is required to install and run vhs." >&2
    exit 1
  fi
done

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

if ! have ffmpeg; then
  ffmpeg_wheel="$tmp/imageio-ffmpeg.whl"
  ffmpeg_url="https://files.pythonhosted.org/packages/a0/2d/43c8522a2038e9d0e7dbdf3a61195ecc31ca576fb1527a528c877e87d973/imageio_ffmpeg-0.6.0-py3-none-manylinux2014_x86_64.whl"
  echo "Downloading the pinned imageio-ffmpeg binary..."
  curl -fsSL "$ffmpeg_url" -o "$ffmpeg_wheel"
  echo "${FFMPEG_WHEEL_SHA256}  ${ffmpeg_wheel}" | sha256sum -c -
  python3 -m zipfile -e "$ffmpeg_wheel" "$tmp/imageio-ffmpeg"

  ffmpeg_binary="$tmp/imageio-ffmpeg/imageio_ffmpeg/binaries/ffmpeg-linux-x86_64-v7.0.2"
  ffmpeg_dest="${FFMPEG_INSTALL_PATH:-/usr/local/bin/ffmpeg}"
  if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
    install -m 0755 "$ffmpeg_binary" "$ffmpeg_dest"
  else
    sudo install -m 0755 "$ffmpeg_binary" "$ffmpeg_dest"
  fi
fi

if ! have ttyd; then
  ttyd_binary="$tmp/ttyd"
  ttyd_url="https://github.com/tsl0922/ttyd/releases/download/${TTYD_VERSION}/ttyd.x86_64"
  echo "Downloading ttyd ${TTYD_VERSION} from ${ttyd_url}..."
  curl -fsSL "$ttyd_url" -o "$ttyd_binary"
  echo "${TTYD_LINUX_X64_SHA256}  ${ttyd_binary}" | sha256sum -c -

  ttyd_dest="${TTYD_INSTALL_PATH:-/usr/local/bin/ttyd}"
  if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
    install -m 0755 "$ttyd_binary" "$ttyd_dest"
  else
    sudo install -m 0755 "$ttyd_binary" "$ttyd_dest"
  fi
fi

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
