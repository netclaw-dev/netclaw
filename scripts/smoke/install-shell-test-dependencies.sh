#!/usr/bin/env bash
# Install the real fish and zsh processes that the installer smoke test uses.
# The fixed files avoid apt index and mirror resolution during the CI gate.

set -euo pipefail

FISH_VERSION="${FISH_VERSION:-4.8.1}"
FISH_SHA256="${FISH_SHA256:-39cab35242ab77bfdbce73b473000c3b045aaf2fe0951b042199bb7fdba3df78}"
ZSH_VERSION="${ZSH_VERSION:-5.9-6ubuntu2}"
ZSH_SHA256="${ZSH_SHA256:-bd5cc8dd3a01a6db38c0a815d75202c356a9c7f378674ba7bed9bc86dcba8af0}"
SHELL_TEST_BIN_DIR="${SHELL_TEST_BIN_DIR:-/usr/local/bin}"

if [[ "$(uname -s)/$(uname -m)" != "Linux/x86_64" ]]; then
  echo "ERROR: The pinned shell test files support Linux x86_64 only." >&2
  exit 1
fi

for dependency in curl dpkg-deb sha256sum tar; do
  if ! command -v "$dependency" >/dev/null 2>&1; then
    echo "ERROR: '${dependency}' is required to install the shell test files." >&2
    exit 1
  fi
done

install_file() {
  local source="$1"
  local destination="$2"

  if [[ -w "$SHELL_TEST_BIN_DIR" ]]; then
    install -m 0755 "$source" "$destination"
  else
    sudo install -m 0755 "$source" "$destination"
  fi
}

if [[ ! -d "$SHELL_TEST_BIN_DIR" ]]; then
  if [[ -w "$(dirname "$SHELL_TEST_BIN_DIR")" ]]; then
    install -d "$SHELL_TEST_BIN_DIR"
  else
    sudo install -d "$SHELL_TEST_BIN_DIR"
  fi
fi

temporary_dir="$(mktemp -d)"
trap 'rm -rf "$temporary_dir"' EXIT

if command -v fish >/dev/null 2>&1; then
  fish_path="$(command -v fish)"
else
  fish_archive="$temporary_dir/fish.tar.xz"
  fish_url="https://github.com/fish-shell/fish-shell/releases/download/${FISH_VERSION}/fish-${FISH_VERSION}-linux-x86_64.tar.xz"

  echo "Downloading fish ${FISH_VERSION} from its fixed upstream file."
  curl -fsSL "$fish_url" -o "$fish_archive"
  echo "${FISH_SHA256}  ${fish_archive}" | sha256sum -c -
  tar -xJf "$fish_archive" -C "$temporary_dir"

  fish_path="$SHELL_TEST_BIN_DIR/fish"
  install_file "$temporary_dir/fish" "$fish_path"
fi

if command -v zsh >/dev/null 2>&1; then
  zsh_path="$(command -v zsh)"
else
  zsh_package="$temporary_dir/zsh.deb"
  zsh_root="$temporary_dir/zsh-root"
  zsh_url="https://archive.ubuntu.com/ubuntu/pool/main/z/zsh/zsh_${ZSH_VERSION}_amd64.deb"

  echo "Downloading zsh ${ZSH_VERSION} from its fixed Ubuntu archive file."
  curl -fsSL "$zsh_url" -o "$zsh_package"
  echo "${ZSH_SHA256}  ${zsh_package}" | sha256sum -c -
  mkdir -p "$zsh_root"
  dpkg-deb -x "$zsh_package" "$zsh_root"

  zsh_path="$SHELL_TEST_BIN_DIR/zsh"
  install_file "$zsh_root/bin/zsh" "$zsh_path"
fi

"$fish_path" --version
"$zsh_path" --version
