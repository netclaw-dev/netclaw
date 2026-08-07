#!/usr/bin/env bash
# Netclaw install script
#
# Usage:
#   curl -sSL https://releases.netclaw.dev/install.sh | bash
#   curl -sSL https://releases.netclaw.dev/install.sh | bash -s -- cli            # CLI only
#   curl -sSL https://releases.netclaw.dev/install.sh | bash -s -- daemon         # Daemon only
#   curl -sSL https://releases.netclaw.dev/install.sh | bash -s -- --channel beta # Opt into prereleases
#   curl -sSL https://releases.netclaw.dev/install.sh | bash -s -- --skip-shell   # Don't modify shell profile
#   INSTALL_DIR=/opt/netclaw curl -sSL https://releases.netclaw.dev/install.sh | bash
#
# Arguments:
#   all|cli|daemon          — Which component(s) to install (default: all)
#   --channel stable|beta   — Release channel (default: stable). 'beta' installs the
#                             newest prerelease (or latest stable if no prerelease exists).
#   --dry-run               — Resolve and report what would happen; install nothing.
#   --skip-shell            — Skip automatic shell profile modification.
#
# Environment variables:
#   INSTALL_DIR     — Install directory (default: ~/.netclaw/bin)
#   NETCLAW_VERSION — Specific version to install (overrides --channel; e.g. 0.19.0-beta.1)

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

# Feed base URL — derived from the manifest URL so a local/private mirror
# resolves the plain-text channel pointers and per-version assets against the
# same origin. Override explicitly to point at a different origin.
FEED_BASE_URL="${FEED_BASE_URL:-$(dirname "$MANIFEST_URL")}"

# ── Argument parsing ──
COMPONENT="all"        # "all", "cli", or "daemon"
DRY_RUN=false          # --dry-run: resolve and report what would happen, install nothing
CHANNEL="stable"       # release channel: "stable" (default) or "beta" (opt into prereleases)
CHANNEL_EXPLICIT=false # true when --channel was explicitly passed
SKIP_SHELL=false       # --skip-shell: don't modify shell profile
while [ $# -gt 0 ]; do
    case "$1" in
        --dry-run) DRY_RUN=true; shift ;;
        --skip-shell) SKIP_SHELL=true; shift ;;
        --channel)
            if [ $# -lt 2 ]; then
                echo "Error: --channel requires a value (stable|beta)" >&2; exit 1
            fi
            CHANNEL="$2"; CHANNEL_EXPLICIT=true; shift 2 ;;
        --channel=*) CHANNEL="${1#*=}"; CHANNEL_EXPLICIT=true; shift ;;
        all|cli|daemon) COMPONENT="$1"; shift ;;
        *) echo "Usage: install.sh [all|cli|daemon] [--channel stable|beta] [--dry-run] [--skip-shell]" >&2; exit 1 ;;
    esac
done

# Validate channel — fail loudly on an unknown value rather than silently defaulting.
case "$CHANNEL" in
    stable|beta) ;;
    *) echo "Error: unknown channel '$CHANNEL' (expected 'stable' or 'beta')" >&2; exit 1 ;;
esac

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

# ── JSON field extraction (POSIX awk only; jq NOT required) ──
# Reflows any brace-balanced JSON document (pretty-printed, minified, or the
# hybrid mix the release generator emits) into one line per leaf object, then
# emits the fields the installer needs:
#
#   latest <value>           — manifest.latest (newest stable)
#   latestPrerelease <value> — manifest.latestPrerelease (newest of all)
#   asset <version> <component> <rid> <url> <sha256>
#
# The parser tracks brace depth and object positions, so it never depends on
# whether the feed is pretty-printed or minified. Works with mawk, gawk, and
# BSD awk — jq is not required.
json_fields() {
    printf '%s\n' "$1" | awk '
        function push(x) { st[++sp] = x }
        function pop()   { return st[sp--] }
        function string_field(record, name, value) {
            if (index(record, "\"" name "\"") == 0) {
                return ""
            }
            value = record
            sub(".*\"" name "\"[[:space:]]*:[[:space:]]*\"", "", value)
            sub("\".*", "", value)
            return value
        }
        function first_key(record, m) {
            if (match(record, /"[^"]*"[[:space:]]*:/)) {
                m = substr(record, RSTART, RLENGTH)
                sub(/"[[:space:]]*:.*/, "", m)
                gsub(/"/, "", m)
                return m
            }
            return ""
        }
        {
            line = $0
            n = length(line)
            for (i = 1; i <= n; i++) {
                c = substr(line, i, 1)
                if (c == "{") {
                    sp++
                    starts[sp] = length(cur) + 1
                    depths[sp] = sp
                    cur = cur c
                } else if (c == "}") {
                    cur = cur c
                    s = starts[sp]; d = depths[sp]; sp--
                    cnt++
                    o_start[cnt] = s
                    o_depth[cnt] = d
                    o_text[cnt] = substr(cur, s)
                    if (s == 1) cur = ""
                } else if (cur != "") {
                    cur = cur c
                }
            }
        }
        END {
            # Sort recorded objects by start position -> document (open) order,
            # so a release object always precedes its nested assets.
            for (i = 2; i <= cnt; i++) {
                k = o_start[i]; kd = o_depth[i]; kt = o_text[i]
                j = i - 1
                while (j >= 1 && o_start[j] > k) {
                    o_start[j+1] = o_start[j]; o_depth[j+1] = o_depth[j]; o_text[j+1] = o_text[j]
                    j--
                }
                o_start[j+1] = k; o_depth[j+1] = kd; o_text[j+1] = kt
            }
            for (i = 1; i <= cnt; i++) {
                obj = o_text[i]
                gsub(/\n/, " ", obj)
                key = first_key(obj)
                if (key == "schemaVersion") {
                    print "latest " string_field(obj, "latest")
                    print "latestPrerelease " string_field(obj, "latestPrerelease")
                } else if (key == "version") {
                    vers[o_depth[i]] = string_field(obj, "version")
                } else if (key == "component") {
                    print "asset " vers[o_depth[i] - 1] " " string_field(obj, "component") " " string_field(obj, "rid") " " string_field(obj, "url") " " string_field(obj, "sha256")
                }
            }
        }
    '
}

json_field() {
    local json="$1" field="$2"
    case "$field" in
        .latest)           json_fields "$json" | awk '$1 == "latest" && !printed         { print $2; printed = 1 }' ;;
        .latestPrerelease) json_fields "$json" | awk '$1 == "latestPrerelease" && !printed { print $2; printed = 1 }' ;;
        *) echo "" ;;
    esac
}

json_asset_field() {
    local json="$1" version="$2" component="$3" rid="$4" field="$5"
    # asset <version> <component> <rid> <url> <sha256>
    # Read all input (no early exit) so the upstream json_fields pipeline never
    # gets SIGPIPE under `set -euo pipefail` on large manifests.
    json_fields "$json" | awk \
        -v wanted_version="$version" \
        -v wanted_component="$component" \
        -v wanted_rid="$rid" \
        -v wanted_field="$field" '
        $1 == "asset" && $2 == wanted_version && $3 == wanted_component && $4 == wanted_rid && !found {
            if (wanted_field == "url") { print $5 }
            if (wanted_field == "sha256") { print $6 }
            found = 1
        }
    '
}

validate_install_dir_for_path() {
    local install_dir="$1"

    # PATH uses ':' as its entry separator, and startup files are line-oriented.
    # These names cannot be represented without changing their meaning.
    if [[ "$install_dir" == *:* || "$install_dir" == *$'\n'* || "$install_dir" == *$'\r'* ]]; then
        echo "Error: INSTALL_DIR cannot contain ':', carriage returns, or newlines when used on PATH." >&2
        return 1
    fi
}

# ── Main ──
check_deps

RID=$(detect_platform)
INSTALL_DIR="${INSTALL_DIR:-$HOME/.netclaw/bin}"
validate_install_dir_for_path "$INSTALL_DIR"

echo "Netclaw installer"
echo "  Platform: $RID"
echo "  Install dir: $INSTALL_DIR"
echo "  Channel: $CHANNEL"
if [ "$DRY_RUN" = true ]; then
    echo "  Mode: dry run (no changes will be made)"
fi
echo ""

# ── Version + asset resolution ──
# Fast path: resolve the channel to a version from a plain-text pointer
# (https://releases.netclaw.dev/latest or /latest-prerelease) — zero JSON
# parsing, matching Deno/kubectl/Go. Fall back to manifest.json + the awk
# parser for feeds that predate the plain-text pointers.
MANIFEST=""   # fetched lazily, only when the plain-text path is unavailable
resolve_version() {
    # 1. Explicit pin never needs the feed at all.
    if [ -n "${NETCLAW_VERSION:-}" ]; then
        VERSION="$NETCLAW_VERSION"
        return 0
    fi

    # 2. Plain-text channel pointer (no JSON).
    local pointer="latest"
    [ "$CHANNEL" = "beta" ] && pointer="latest-prerelease"
    local fetched
    fetched=$(curl -fsSL --max-time 10 "$FEED_BASE_URL/$pointer" 2>/dev/null | tr -d '[:space:]' || true)
    if [ -n "$fetched" ]; then
        VERSION="$fetched"
        echo "  Resolved $CHANNEL channel from $FEED_BASE_URL/$pointer"
        return 0
    fi

    # 3. Fallback: manifest.json + awk parser (older feed without pointers).
    echo "  Plain-text channel pointer not available; falling back to manifest.json" >&2
    MANIFEST=$(curl -sSL --fail "$MANIFEST_URL") || {
        echo "Error: Failed to fetch manifest from $MANIFEST_URL" >&2
        exit 1
    }
    if [ "$CHANNEL" = "beta" ]; then
        VERSION=$(json_field "$MANIFEST" ".latestPrerelease")
        if [ -z "$VERSION" ] || [ "$VERSION" = "null" ]; then
            echo "  Note: manifest has no prerelease channel; using latest stable." >&2
            VERSION=$(json_field "$MANIFEST" ".latest")
        fi
    else
        VERSION=$(json_field "$MANIFEST" ".latest")
    fi
    if [ -z "$VERSION" ] || [ "$VERSION" = "null" ]; then
        echo "Error: Could not determine latest version from manifest" >&2
        exit 1
    fi
    return 0
}

# ── Asset resolution ──
# URL layout is deterministic: $FEED_BASE_URL/$VERSION/$component-$VERSION-$RID.{tar.gz|zip}.
# sha256 comes from the per-version checksums-$RID.txt file; manifest.json's
# sha256 is the fallback when that file is absent.
resolve_asset() {
    local component="$1"
    local ext="tar.gz"
    [ "$RID" = "win-x64" ] && ext="zip"
    url="$FEED_BASE_URL/$VERSION/$component-$VERSION-$RID.$ext"

    sha256=""
    local checksums
    checksums=$(curl -fsSL --max-time 10 "$FEED_BASE_URL/$VERSION/checksums-$RID.txt" 2>/dev/null || true)
    if [ -n "$checksums" ]; then
        sha256=$(printf '%s\n' "$checksums" | awk -v f="$component-$VERSION-$RID.$ext" '$2 == f { print $1; exit }')
    fi
    if [ -z "$sha256" ] && [ -n "$MANIFEST" ]; then
        sha256=$(json_asset_field "$MANIFEST" "$VERSION" "$component" "$RID" "sha256")
    fi
    return 0
}

resolve_version
echo "  Version: $VERSION"
echo ""

# Parse assets from the release manifest. Uses the POSIX awk parser —
# jq is not required, and the parser handles pretty-printed, minified,
# and hybrid manifests alike.
TMPDIR=$(mktemp -d)
trap 'rm -rf "$TMPDIR"' EXIT

download_component() {
    local component="$1"
    local url sha256

    resolve_asset "$component"

    if [ -z "$url" ]; then
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

    # Verify checksum (fail closed when one is available; warn if not)
    if [ -n "$sha256" ]; then
        echo "  Verifying checksum..."
        local actual_sha
        actual_sha=$(sha256_file "$TMPDIR/$filename")
        if [ "$actual_sha" != "$sha256" ]; then
            echo "  Error: Checksum mismatch for $filename" >&2
            echo "    Expected: $sha256" >&2
            echo "    Got:      $actual_sha" >&2
            return 1
        fi
    else
        echo "  Warning: no checksum available for $filename; skipping verification" >&2
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

    cp "$binary_path" "$INSTALL_DIR/$binary_name"
    chmod +x "$INSTALL_DIR/$binary_name"
    echo "  Installed $binary_name to $INSTALL_DIR/"
}

if [ "$DRY_RUN" = false ]; then
    # Resolve symlinks before installing so the exact path persisted into shell
    # startup files is the same path that passed delimiter validation.
    mkdir -p "$INSTALL_DIR"
    INSTALL_DIR="$(cd "$INSTALL_DIR" && pwd -P)"
    validate_install_dir_for_path "$INSTALL_DIR"
fi

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

# ── Persist UpdateChannel into config ──
# Only runs when --channel was explicitly passed. Without this guard a plain
# upgrade (`install.sh` with no flags) would silently overwrite an existing
# beta channel to stable — a silent fallback the project prohibits.
if [ "$CHANNEL_EXPLICIT" = true ]; then
    CONFIG_DIR="${CONFIG_DIR:-$HOME/.netclaw/config}"
    CONFIG_FILE="$CONFIG_DIR/netclaw.json"
    if [ -f "$CONFIG_FILE" ]; then
        if command -v jq >/dev/null 2>&1; then
            if jq --arg ch "$CHANNEL" '.Daemon = ((.Daemon // {}) + {UpdateChannel: $ch})' \
                "$CONFIG_FILE" > "${CONFIG_FILE}.tmp"; then
                mv "${CONFIG_FILE}.tmp" "$CONFIG_FILE"
                echo "  Set Daemon.UpdateChannel to '$CHANNEL' in $CONFIG_FILE"
            else
                rm -f "${CONFIG_FILE}.tmp"
                echo "  Warning: could not update Daemon.UpdateChannel (malformed config?)." >&2
            fi
        else
            echo "  Note: jq not found — could not set Daemon.UpdateChannel in config."
            echo "  To receive $CHANNEL updates, add to $CONFIG_FILE:"
            echo "    \"Daemon\": { \"UpdateChannel\": \"$CHANNEL\" }"
        fi
    elif [ "$CHANNEL" != "stable" ]; then
        # Fresh install: config doesn't exist yet. Write a minimal seed so
        # `netclaw init` can discover the channel preference.
        mkdir -p "$CONFIG_DIR"
        printf '{"configVersion":1,"Daemon":{"UpdateChannel":"%s"}}\n' "$CHANNEL" > "$CONFIG_FILE"
        echo "  Created $CONFIG_FILE with UpdateChannel '$CHANNEL'"
    fi
fi

# ── Shell integration ─────────────────────────────────────────────────────
# Bash and zsh source a small POSIX env file. Fish gets native syntax in its
# dedicated conf.d file; fish cannot source POSIX `case ... esac` syntax.
ENV_SCRIPT="$HOME/.netclaw/env"

shell_quote() {
    printf "'"
    printf '%s' "$1" | sed "s/'/'\\\\''/g"
    printf "'"
}

INSTALL_DIR_QUOTED="$(shell_quote "$INSTALL_DIR")"
ENV_SCRIPT_QUOTED="$(shell_quote "$ENV_SCRIPT")"
SOURCE_LINE=". $ENV_SCRIPT_QUOTED"
MANUAL_PATH_LINE="export PATH=$INSTALL_DIR_QUOTED\${PATH:+:\"\$PATH\"}"

detect_shell() {
    # $SHELL is inherited from the parent login shell — it reflects the user's
    # configured shell even when this script is piped via `curl | bash`.
    local shell_name
    shell_name="$(basename "${SHELL:-/bin/sh}")"
    echo "$shell_name"
}

get_rc_file() {
    local shell_name="$1" shell_path="$2"
    local os
    os="$(uname -s | tr '[:upper:]' '[:lower:]')"

    case "$shell_name" in
        zsh)
            local effective_zdotdir
            # ZDOTDIR is often assigned without export in ~/.zshenv, so ask zsh
            # for the value it actually uses rather than relying on Bash's env.
            effective_zdotdir="$("$shell_path" -c "printf '%s' \"\${ZDOTDIR:-\$HOME}\"")" || return 1
            if [[ -z "$effective_zdotdir" || "$effective_zdotdir" != /* || \
                  "$effective_zdotdir" == *$'\n'* || "$effective_zdotdir" == *$'\r'* ]]; then
                return 1
            fi
            echo "$effective_zdotdir/.zshrc"
            ;;
        bash)
            if [ "$os" = "darwin" ]; then
                # A login shell reads only the first existing file in this list.
                if [ -f "$HOME/.bash_profile" ]; then
                    echo "$HOME/.bash_profile"
                elif [ -f "$HOME/.bash_login" ]; then
                    echo "$HOME/.bash_login"
                else
                    echo "$HOME/.profile"
                fi
            else
                echo "$HOME/.bashrc"
            fi
            ;;
        *)
            echo ""
            ;;
    esac
}

write_posix_env_script() {
    mkdir -p "$(dirname "$ENV_SCRIPT")"
    cat > "$ENV_SCRIPT" <<ENVEOF
#!/bin/sh
# netclaw shell setup
netclaw_bin=$INSTALL_DIR_QUOTED
case ":\${PATH:-}:" in
    *:"\${netclaw_bin}":*)
        ;;
    *)
        if [ -n "\${PATH:-}" ]; then
            export PATH="\${netclaw_bin}:\${PATH}"
        else
            export PATH="\${netclaw_bin}"
        fi
        ;;
esac
unset netclaw_bin
ENVEOF
}

modify_posix_rc_file() {
    local rc_file="$1"
    mkdir -p "$(dirname "$rc_file")"
    touch "$rc_file"

    if grep -qxF "$SOURCE_LINE" "$rc_file" 2>/dev/null; then
        echo "  Shell profile '$rc_file' already sources netclaw."
        return 0
    fi

    if [ -s "$rc_file" ] && [ "$(tail -c1 "$rc_file" | wc -l)" -eq 0 ]; then
        echo "" >> "$rc_file"
    fi

    {
        echo "# netclaw shell setup"
        echo "$SOURCE_LINE"
    } >> "$rc_file"

    echo "  Modified '$rc_file' to add netclaw to PATH."
}

write_fish_config() {
    local fish_config_dir="${XDG_CONFIG_HOME:-$HOME/.config}/fish/conf.d"
    local fish_config="$fish_config_dir/netclaw.fish"
    mkdir -p "$fish_config_dir"
    cat > "$fish_config" <<FISHEOF
# netclaw shell setup
set -l netclaw_bin $INSTALL_DIR_QUOTED
if not contains -- \$netclaw_bin \$PATH
    set -gx PATH \$netclaw_bin \$PATH
end
FISHEOF
    echo "  Wrote '$fish_config' to add netclaw to PATH."
}

if [ "$SKIP_SHELL" = false ]; then
    SHELL_NAME="$(detect_shell)"
    echo ""
    echo "Setting up shell integration..."

    case "$SHELL_NAME" in
        bash)
            RC_FILE="$(get_rc_file "$SHELL_NAME" "${SHELL:-/bin/bash}")"
            write_posix_env_script
            modify_posix_rc_file "$RC_FILE"
            echo ""
            echo "Installation complete! netclaw will be on PATH in new shells."
            echo "To update this shell, run:"
            echo ""
            echo "  $SOURCE_LINE"
            ;;
        zsh)
            if RC_FILE="$(get_rc_file "$SHELL_NAME" "${SHELL:-/bin/zsh}")"; then
                write_posix_env_script
                modify_posix_rc_file "$RC_FILE"
                echo ""
                echo "Installation complete! netclaw will be on PATH in new shells."
                echo "To update this shell, run:"
                echo ""
                echo "  $SOURCE_LINE"
            else
                echo "  Could not safely resolve zsh's effective ZDOTDIR."
                echo "  No shell profile was changed. Add this to the appropriate zsh profile:"
                echo ""
                echo "    $MANUAL_PATH_LINE"
            fi
            ;;
        fish)
            write_fish_config
            echo ""
            echo "Installation complete! netclaw will be on PATH in new fish shells."
            echo "To update this shell, run:"
            echo ""
            echo "  set -gx PATH $INSTALL_DIR_QUOTED \$PATH"
            ;;
        *)
            echo "  Shell '$SHELL_NAME' is not supported for automatic PATH setup."
            echo "  No shell profile was changed. Add this directory to PATH using your shell's syntax:"
            echo ""
            echo "    $INSTALL_DIR"
            ;;
    esac
else
    echo ""
    echo "Installation complete! (shell integration skipped)"
    echo ""
    echo "Add netclaw to your PATH by adding this to your shell profile:"
    echo ""
    echo "  $MANUAL_PATH_LINE"
fi

echo ""
echo "Get started:"
echo "  netclaw init             # First-run setup wizard"
echo "  netclaw doctor           # Verify configuration"
if [ "$(uname -s)" = "Linux" ]; then
    echo "  netclaw daemon install   # Enable auto-start on boot (systemd)"
fi
