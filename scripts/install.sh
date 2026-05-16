#!/usr/bin/env bash
# Netclaw install script
#
# Usage:
#   curl -sSL https://releases.netclaw.dev/install.sh | bash
#   curl -sSL https://releases.netclaw.dev/install.sh | bash -s -- cli      # CLI only
#   curl -sSL https://releases.netclaw.dev/install.sh | bash -s -- daemon   # Daemon only
#   INSTALL_DIR=/opt/netclaw curl -sSL https://releases.netclaw.dev/install.sh | bash
#
# Environment variables:
#   INSTALL_DIR   — Install directory (default: ~/.netclaw/bin)
#   NETCLAW_VERSION — Specific version to install (default: latest)

set -euo pipefail

# Progress display: show curl progress bar when stderr is a terminal
if [ -t 2 ]; then
    CURL_PROGRESS=(--progress-bar)
else
    CURL_PROGRESS=(-s)
fi

# MANIFEST_URL is overridable so the script can be pointed at a local manifest
# (smoke tests) or a private mirror.
MANIFEST_URL="${MANIFEST_URL:-https://releases.netclaw.dev/manifest.json}"

# ── Argument parsing ──
COMPONENT="all"   # "all", "cli", or "daemon"
DRY_RUN=false     # --dry-run: resolve and report what would happen, install nothing
for arg in "$@"; do
    case "$arg" in
        --dry-run) DRY_RUN=true ;;
        all|cli|daemon) COMPONENT="$arg" ;;
        *) echo "Usage: install.sh [all|cli|daemon] [--dry-run]" >&2; exit 1 ;;
    esac
done

# ── Platform detection ──
detect_platform() {
    local os arch rid

    os=$(uname -s | tr '[:upper:]' '[:lower:]')
    arch=$(uname -m)

    case "$os" in
        linux)
            case "$arch" in
                x86_64|amd64) rid="linux-x64" ;;
                aarch64|arm64) rid="linux-arm64" ;;
                *) echo "Error: Unsupported architecture '$arch' on Linux." >&2; exit 1 ;;
            esac
            ;;
        darwin)
            # A shell running under Rosetta 2 on Apple Silicon reports x86_64;
            # sysctl.proc_translated == 1 means the real CPU is arm64.
            if [ "$arch" = "x86_64" ] && \
               [ "$(sysctl -n sysctl.proc_translated 2>/dev/null || echo 0)" = "1" ]; then
                arch="arm64"
            fi
            case "$arch" in
                arm64) rid="osx-arm64" ;;
                x86_64)
                    echo "Error: Intel Macs are not supported. Netclaw requires" >&2
                    echo "Apple Silicon (M1 or later)." >&2
                    exit 1
                    ;;
                *) echo "Error: Unsupported architecture '$arch' on macOS." >&2; exit 1 ;;
            esac
            ;;
        *)
            echo "Error: Unsupported OS: $os. Netclaw supports Linux and macOS." >&2
            exit 1
            ;;
    esac

    echo "$rid"
}

# ── Dependency checks ──
check_deps() {
    for cmd in curl tar; do
        if ! command -v "$cmd" >/dev/null 2>&1; then
            echo "Error: Required command '$cmd' not found." >&2
            exit 1
        fi
    done
    # macOS ships 'shasum'; most Linux distros ship 'sha256sum' — accept either.
    if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
        echo "Error: Need either 'sha256sum' or 'shasum' for checksum verification." >&2
        exit 1
    fi
}

# ── SHA-256 of a file (sha256sum on Linux, shasum on macOS) ──
sha256_file() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | cut -d' ' -f1
    else
        shasum -a 256 "$1" | cut -d' ' -f1
    fi
}

# ── JSON field extraction (no jq dependency) ──
# Uses jq if available, falls back to grep/sed
json_field() {
    local json="$1" field="$2"
    if command -v jq >/dev/null 2>&1; then
        echo "$json" | jq -r "$field"
    else
        # Simple grep/sed fallback for flat JSON fields
        echo "$json" | grep -o "\"${field#.}\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" | head -1 | sed 's/.*: *"\(.*\)"/\1/'
    fi
}

# ── Main ──
check_deps

RID=$(detect_platform)
INSTALL_DIR="${INSTALL_DIR:-$HOME/.netclaw/bin}"

echo "Netclaw installer"
echo "  Platform: $RID"
echo "  Install dir: $INSTALL_DIR"
if [ "$DRY_RUN" = true ]; then
    echo "  Mode: dry run (no changes will be made)"
fi
echo ""

# Fetch manifest
echo "Fetching release manifest..."
MANIFEST=$(curl -sSL --fail "$MANIFEST_URL") || {
    echo "Error: Failed to fetch manifest from $MANIFEST_URL" >&2
    exit 1
}

# Determine version
if [ -n "${NETCLAW_VERSION:-}" ]; then
    VERSION="$NETCLAW_VERSION"
else
    VERSION=$(json_field "$MANIFEST" ".latest")
fi

if [ -z "$VERSION" ]; then
    echo "Error: Could not determine latest version from manifest" >&2
    exit 1
fi

echo "  Version: $VERSION"
echo ""

# Parse assets using jq if available, otherwise use a simpler approach
TMPDIR=$(mktemp -d)
trap 'rm -rf "$TMPDIR"' EXIT

download_component() {
    local component="$1"
    local url sha256

    if command -v jq >/dev/null 2>&1; then
        url=$(echo "$MANIFEST" | jq -r ".releases[] | select(.version==\"$VERSION\") | .assets[] | select(.component==\"$component\" and .rid==\"$RID\") | .url")
        sha256=$(echo "$MANIFEST" | jq -r ".releases[] | select(.version==\"$VERSION\") | .assets[] | select(.component==\"$component\" and .rid==\"$RID\") | .sha256")
    else
        # Fallback: extract URL and sha256 using grep (fragile but works for well-formed JSON)
        # Find the block for this component+rid
        local block
        block=$(echo "$MANIFEST" | tr '\n' ' ' | grep -oP "\"component\"\\s*:\\s*\"${component}\"[^}]*\"rid\"\\s*:\\s*\"${RID}\"[^}]*}" | head -1)
        if [ -z "$block" ]; then
            # Try reversed order
            block=$(echo "$MANIFEST" | tr '\n' ' ' | grep -oP "\"rid\"\\s*:\\s*\"${RID}\"[^}]*\"component\"\\s*:\\s*\"${component}\"[^}]*}" | head -1)
        fi
        url=$(echo "$block" | grep -oP '"url"\s*:\s*"\K[^"]+')
        sha256=$(echo "$block" | grep -oP '"sha256"\s*:\s*"\K[^"]+')
    fi

    if [ -z "$url" ] || [ "$url" = "null" ]; then
        echo "  Warning: No $component binary found for $RID in version $VERSION" >&2
        return 1
    fi

    if [ "$DRY_RUN" = true ]; then
        echo "  DRY RUN: would install $component from $url"
        return 0
    fi

    local filename
    filename=$(basename "$url")

    echo "  Downloading $component..."
    curl "${CURL_PROGRESS[@]}" -fL -o "$TMPDIR/$filename" "$url" || {
        echo "  Error: Failed to download $url" >&2
        return 1
    }

    # Verify checksum
    echo "  Verifying checksum..."
    local actual_sha
    actual_sha=$(sha256_file "$TMPDIR/$filename")
    if [ "$actual_sha" != "$sha256" ]; then
        echo "  Error: Checksum mismatch for $filename" >&2
        echo "    Expected: $sha256" >&2
        echo "    Got:      $actual_sha" >&2
        return 1
    fi

    # Extract
    echo "  Extracting..."
    tar xzf "$TMPDIR/$filename" -C "$TMPDIR"

    # Find and install binary
    local binary_name="$component"
    local binary_path
    binary_path=$(find "$TMPDIR" -name "$binary_name" -type f | head -1)
    if [ -z "$binary_path" ]; then
        echo "  Error: Could not find $binary_name in archive" >&2
        return 1
    fi

    mkdir -p "$INSTALL_DIR"
    cp "$binary_path" "$INSTALL_DIR/$binary_name"
    chmod +x "$INSTALL_DIR/$binary_name"
    echo "  Installed $binary_name to $INSTALL_DIR/"
}

# Download requested components
SUCCESS=true
if [[ "$COMPONENT" == "all" || "$COMPONENT" == "cli" ]]; then
    download_component "netclaw" || SUCCESS=false
fi
if [[ "$COMPONENT" == "all" || "$COMPONENT" == "daemon" ]]; then
    download_component "netclawd" || SUCCESS=false
fi

if [ "$SUCCESS" = false ]; then
    echo ""
    echo "Some components failed to install." >&2
    exit 1
fi

if [ "$DRY_RUN" = true ]; then
    echo ""
    echo "Dry run complete — nothing was installed."
    exit 0
fi

# PATH instructions
echo ""
if echo "$PATH" | tr ':' '\n' | grep -qx "$INSTALL_DIR"; then
    echo "Installation complete! netclaw is already on your PATH."
else
    echo "Installation complete!"
    echo ""
    echo "Add Netclaw to your PATH by adding this to your shell profile:"
    echo ""
    echo "  export PATH=\"$INSTALL_DIR:\$PATH\""
    echo ""
    echo "Then restart your shell or run:"
    echo ""
    echo "  source ~/.bashrc  # or ~/.zshrc"
fi

echo ""
echo "Get started:"
echo "  netclaw init      # First-run setup wizard"
echo "  netclaw doctor    # Verify configuration"
