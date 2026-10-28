#!/usr/bin/env bash
# Netclaw uninstall script
#
# Usage:
#   curl -sSL https://releases.netclaw.dev/uninstall.sh | bash
#   bash scripts/uninstall.sh --dry-run
#   bash scripts/uninstall.sh --purge
#   bash scripts/uninstall.sh --purge --yes
#   bash scripts/uninstall.sh --skip-shell
#   bash scripts/uninstall.sh --install-dir /opt/netclaw
#
# Arguments:
#   --purge                 Also delete $NETCLAW_HOME (config, secrets, sessions, memory.db).
#                           Prompts for confirmation unless --yes is also passed.
#   --yes                   Skip the confirmation prompt. Required for --purge when stdin
#                           is not a terminal.
#   --dry-run               Report what would be removed; change nothing.
#   --skip-shell            Do not touch shell profiles or the env script.
#   --install-dir <path>    Override the detected install directory.
#   --help                  Show this help.
#
# Environment variables:
#   NETCLAW_HOME    — Netclaw home (default: ~/.netclaw). Resolved with the same rules
#                     as install.sh so the two scripts agree on the directory.
#   INSTALL_DIR     — Install directory. Same precedence as install.sh.
#
# Exit codes:
#   0  Netclaw is gone (including "was already gone")
#   1  The daemon would not stop; nothing was deleted
#   2  Invalid arguments
#   3  Refused: --purge without --yes when stdin is not a terminal
#   4  Refused: running from inside the install directory

set -uo pipefail

# ── Argument parsing ──
PURGE=false
ASSUME_YES=false
DRY_RUN=false
SKIP_SHELL=false
INSTALL_DIR_OVERRIDE=""

usage() {
    cat <<'USAGE'
Netclaw uninstall script

Usage:
  curl -sSL https://releases.netclaw.dev/uninstall.sh | bash
  bash scripts/uninstall.sh --dry-run
  bash scripts/uninstall.sh --purge
  bash scripts/uninstall.sh --purge --yes
  bash scripts/uninstall.sh --skip-shell
  bash scripts/uninstall.sh --install-dir /opt/netclaw

Arguments:
  --purge                 Also delete $NETCLAW_HOME (config, secrets, sessions,
                          memory.db). Prompts for confirmation unless --yes is
                          also passed.
  --yes, -y               Skip the confirmation prompt. Required for --purge
                          when stdin is not a terminal.
  --dry-run               Report what would be removed; change nothing.
  --skip-shell            Do not touch shell profiles or the env script.
  --install-dir <path>    Override the detected install directory.
  --help, -h              Show this help.

Environment variables:
  NETCLAW_HOME    Netclaw home (default: ~/.netclaw). Resolved with the same
                  rules as install.sh so the two scripts agree.
  INSTALL_DIR     Install directory. Same precedence as install.sh.

Exit codes:
  0  Netclaw is gone (including "was already gone")
  1  The daemon would not stop; nothing was deleted
  2  Invalid arguments
  3  Refused: --purge without --yes when stdin is not a terminal
  4  Refused: running from inside the install directory
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --purge) PURGE=true; shift ;;
        --yes|-y) ASSUME_YES=true; shift ;;
        --dry-run) DRY_RUN=true; shift ;;
        --skip-shell) SKIP_SHELL=true; shift ;;
        --install-dir)
            if [ $# -lt 2 ]; then
                echo "Error: --install-dir requires a value" >&2
                exit 2
            fi
            INSTALL_DIR_OVERRIDE="$2"; shift 2 ;;
        --install-dir=*) INSTALL_DIR_OVERRIDE="${1#*=}"; shift ;;
        --help|-h) usage; exit 0 ;;
        *) echo "Error: unknown argument '$1'" >&2; usage >&2; exit 2 ;;
    esac
done

# ── Netclaw home resolution ──
# Mirrors install.sh:149-196 exactly. The two scripts must agree on the directory,
# or uninstall deletes the wrong tree.
replace_ignore_case() {
    local value="$1" token="$2" replacement="$3"
    local lower_token lower_value prefix result=""
    lower_token="$(printf '%s' "$token" | tr '[:upper:]' '[:lower:]')"
    while :; do
        lower_value="$(printf '%s' "$value" | tr '[:upper:]' '[:lower:]')"
        case "$lower_value" in
            *"$lower_token"*) ;;
            *) break ;;
        esac
        prefix="${lower_value%%"$lower_token"*}"
        result="$result${value:0:${#prefix}}$replacement"
        value="${value:$((${#prefix} + ${#token}))}"
    done
    printf '%s' "$result$value"
}

resolve_netclaw_home() {
    local value="${NETCLAW_HOME:-}"
    value="${value#"${value%%[![:space:]]*}"}"
    value="${value%"${value##*[![:space:]]}"}"
    if [ -z "$value" ]; then
        printf '%s' "$HOME/.netclaw"
        return
    fi

    case "$value" in
        "~") value="$HOME" ;;
        "~"*)
            local rest="${value#\~}"
            while [ "${rest#[/\\]}" != "$rest" ]; do rest="${rest#[/\\]}"; done
            value="$HOME/$rest"
            ;;
    esac

    # shellcheck disable=SC2016
    value="$(replace_ignore_case "$value" '$HOME' "$HOME")"
    # shellcheck disable=SC2016
    value="$(replace_ignore_case "$value" '${HOME}' "$HOME")"
    value="$(replace_ignore_case "$value" '%USERPROFILE%' "$HOME")"

    case "$value" in
        /*) ;;
        *) value="$PWD/$value" ;;
    esac
    printf '%s' "$value"
}

NETCLAW_HOME_DIR="$(resolve_netclaw_home)"

# ── Install directory resolution ──
# Mirrors install.sh:204-225. A legacy install keeps its env script in
# $HOME/.netclaw even when NETCLAW_HOME points elsewhere, so ENV_HOME_DIR is
# tracked separately from NETCLAW_HOME_DIR.
LEGACY_HOME_DIR="$HOME/.netclaw"
ENV_HOME_DIR="$NETCLAW_HOME_DIR"

if [ -n "$INSTALL_DIR_OVERRIDE" ]; then
    INSTALL_DIR="$INSTALL_DIR_OVERRIDE"
elif [ -n "${INSTALL_DIR:-}" ]; then
    :
elif [ -f "$NETCLAW_HOME_DIR/bin/netclaw" ] || [ -f "$NETCLAW_HOME_DIR/bin/netclawd" ]; then
    INSTALL_DIR="$NETCLAW_HOME_DIR/bin"
elif [ -f "$LEGACY_HOME_DIR/bin/netclaw" ] || [ -f "$LEGACY_HOME_DIR/bin/netclawd" ]; then
    INSTALL_DIR="$LEGACY_HOME_DIR/bin"
    ENV_HOME_DIR="$LEGACY_HOME_DIR"
else
    INSTALL_DIR="$NETCLAW_HOME_DIR/bin"
fi

ENV_SCRIPT="$ENV_HOME_DIR/env"

# ── Safety: refuse to run from inside the install directory ──
# Deleting the directory that holds the running script, or the operator's cwd,
# produces confusing partial failures.
if [ -d "$INSTALL_DIR" ]; then
    INSTALL_DIR_PHYSICAL="$(cd "$INSTALL_DIR" && pwd -P)"
    CWD_PHYSICAL="$(pwd -P)"
    case "$CWD_PHYSICAL/" in
        "$INSTALL_DIR_PHYSICAL"/*)
            echo "Error: refusing to run from inside the install directory ($INSTALL_DIR_PHYSICAL)." >&2
            echo "Change to another directory and run again." >&2
            exit 4
            ;;
    esac
fi

# ── Safety: --purge needs confirmation ──
if [ "$PURGE" = true ] && [ "$ASSUME_YES" = false ] && [ "$DRY_RUN" = false ]; then
    if [ ! -t 0 ]; then
        echo "Error: --purge deletes $NETCLAW_HOME_DIR and needs confirmation." >&2
        echo "stdin is not a terminal, so the script cannot prompt." >&2
        echo "Pass --yes to confirm, or omit --purge to keep your data." >&2
        exit 3
    fi
    echo "This will permanently delete:"
    echo "  $NETCLAW_HOME_DIR"
    echo ""
    echo "That directory holds your config, secrets, sessions, and memory database."
    printf "Type 'yes' to continue: "
    read -r confirmation
    if [ "$confirmation" != "yes" ]; then
        echo "Aborted. Nothing was deleted."
        exit 0
    fi
fi

# ── Reporting helpers ──
REMOVED=()
KEPT=()
WARNINGS=()

report_removed() { REMOVED+=("$1"); }
report_kept() { KEPT+=("$1"); }
report_warning() { WARNINGS+=("$1"); }

# Remove a file if it exists. Idempotent: a missing file is success.
# Sets REMOVED_ANY_FILE=true when a file was actually removed, so callers can
# tell "we emptied this directory" from "it was already empty".
REMOVED_ANY_FILE=false
remove_file() {
    local path="$1" label="$2"
    if [ -e "$path" ] || [ -L "$path" ]; then
        if [ "$DRY_RUN" = true ]; then
            echo "  would remove: $path"
        else
            if rm -f "$path" 2>/dev/null; then
                echo "  removed: $path"
            else
                echo "  warning: could not remove $path" >&2
                report_warning "could not remove $path"
                return 1
            fi
        fi
        REMOVED_ANY_FILE=true
        report_removed "$label"
    fi
    return 0
}

# Remove a directory only when this run emptied it. A directory that was
# already empty is not ours to delete: --install-dir may point at any path,
# including the operator's home directory.
remove_dir_if_emptied() {
    local path="$1" label="$2" emptied="$3"
    [ -d "$path" ] || return 0

    if [ "$emptied" != true ]; then
        return 0
    fi

    if [ -n "$(ls -A "$path" 2>/dev/null)" ]; then
        report_kept "$label ($path)"
        return 0
    fi

    if [ "$DRY_RUN" = true ]; then
        echo "  would remove empty directory: $path"
    else
        if rmdir "$path" 2>/dev/null; then
            echo "  removed empty directory: $path"
        else
            echo "  warning: could not remove empty directory $path" >&2
            report_warning "could not remove empty directory $path"
            return 1
        fi
    fi
    report_removed "$label"
    return 0
}

echo "Netclaw uninstaller"
echo "  Install dir: $INSTALL_DIR"
echo "  Netclaw home: $NETCLAW_HOME_DIR"
if [ "$PURGE" = true ]; then
    echo "  Mode: purge (data will be deleted)"
else
    echo "  Mode: standard (data will be kept)"
fi
if [ "$DRY_RUN" = true ]; then
    echo "  Dry run: no changes will be made"
fi
echo ""

# ── 1. Stop the daemon ──
# Must happen before any deletion. On Linux a live daemon holds the SQLite WAL
# open; deleting the home directory under it leaves a corrupt database.
echo "Stopping the daemon..."
DAEMON_STOPPED=false
if [ -f "$NETCLAW_HOME_DIR/netclaw.pid" ]; then
    PID="$(cat "$NETCLAW_HOME_DIR/netclaw.pid" 2>/dev/null || true)"
    if [ -n "$PID" ] && kill -0 "$PID" 2>/dev/null; then
        if [ "$DRY_RUN" = true ]; then
            echo "  would stop daemon (pid $PID)"
            DAEMON_STOPPED=true
        else
            # Prefer the CLI's graceful stop when the binary is still present.
            if [ -x "$INSTALL_DIR/netclaw" ]; then
                "$INSTALL_DIR/netclaw" daemon stop >/dev/null 2>&1 || true
            else
                kill -TERM "$PID" 2>/dev/null || true
            fi

            # Wait for the process to exit. 30 seconds is generous for a
            # graceful shutdown and short enough to fail fast.
            waited=0
            while [ "$waited" -lt 30 ] && kill -0 "$PID" 2>/dev/null; do
                sleep 1
                waited=$((waited + 1))
            done

            if kill -0 "$PID" 2>/dev/null; then
                echo "Error: the daemon (pid $PID) did not stop within 30 seconds." >&2
                echo "Nothing was deleted. Stop it manually and run again:" >&2
                echo "  kill -TERM $PID" >&2
                exit 1
            fi
            echo "  daemon stopped (pid $PID)"
            DAEMON_STOPPED=true
        fi
    else
        echo "  no running daemon"
    fi
else
    echo "  no running daemon"
fi

# ── 2. Remove the systemd user service ──
# Before the binaries: the unit's ExecStop= points at the CLI binary, so a
# missing binary breaks the graceful stop path.
if [ "$(uname -s)" = "Linux" ]; then
    UNIT_PATH="$HOME/.config/systemd/user/netclaw.service"
    if [ -f "$UNIT_PATH" ]; then
        echo ""
        echo "Removing systemd user service..."
        if [ "$DRY_RUN" = true ]; then
            echo "  would stop and disable netclaw.service"
            echo "  would remove: $UNIT_PATH"
        else
            systemctl --user stop netclaw.service >/dev/null 2>&1 || true
            systemctl --user disable netclaw.service >/dev/null 2>&1 || true
            if rm -f "$UNIT_PATH" 2>/dev/null; then
                echo "  removed: $UNIT_PATH"
            else
                echo "  warning: could not remove $UNIT_PATH" >&2
                report_warning "could not remove $UNIT_PATH"
            fi
            # A host without a user systemd instance fails here. That is not
            # fatal: the unit file is already gone.
            systemctl --user daemon-reload >/dev/null 2>&1 || true
        fi
        report_removed "systemd user service"
    fi
fi

# ── 3. Remove the daemon environment file ──
remove_file "$NETCLAW_HOME_DIR/config/daemon.env" "daemon environment file"

# ── 4. Remove binaries and update leftovers ──
echo ""
echo "Removing binaries..."
REMOVED_ANY_FILE=false
remove_file "$INSTALL_DIR/netclaw" "netclaw binary"
remove_file "$INSTALL_DIR/netclawd" "netclawd binary"
# The updater leaves these behind. Uninstall runs outside the install dir, so
# both are deletable here even on Windows-equivalent constraints.
remove_file "$INSTALL_DIR/netclaw.backup" "netclaw backup"
remove_file "$INSTALL_DIR/netclawd.backup" "netclawd backup"
INSTALL_DIR_EMPTIED=$REMOVED_ANY_FILE

# Remove the install directory only when this run emptied it. --install-dir
# accepts any path, so an already-empty directory is not ours to delete.
remove_dir_if_emptied "$INSTALL_DIR" "install directory" "$INSTALL_DIR_EMPTIED"

# ── 5. Remove shell integration ──
if [ "$SKIP_SHELL" = false ]; then
    echo ""
    echo "Removing shell integration..."

    # The rc block is exactly two lines: a comment and a source line. Remove
    # the pair only when the source line matches this install's env script.
    # A stray comment without a matching source line is left alone.
    remove_rc_block() {
        local rc_file="$1"
        [ -f "$rc_file" ] || return 0

        local source_line
        source_line=". $(shell_quote "$ENV_SCRIPT")"

        if ! grep -qxF "$source_line" "$rc_file" 2>/dev/null; then
            return 0
        fi

        if [ "$DRY_RUN" = true ]; then
            echo "  would remove netclaw block from: $rc_file"
            report_removed "shell profile block in $rc_file"
            return 0
        fi

        local backup_file="$rc_file.netclaw-uninstall-backup"
        cp "$rc_file" "$backup_file" 2>/dev/null || true

        # Walk the file line by line, holding one pending comment. Consecutive
        # comment lines collapse to a single pending marker, so a duplicated
        # comment is removed with its source line. A comment is emitted only
        # when the next line is neither the comment nor the source line.
        local tmp_file
        tmp_file="$(mktemp)" || return 1
        # mktemp creates the file 0600; carry the profile's original mode over
        # so an uninstall does not silently tighten the user's ~/.bashrc.
        # stat flags differ between GNU (Linux) and BSD (macOS): try both.
        local rc_mode
        rc_mode="$(stat -c '%a' "$rc_file" 2>/dev/null || stat -f '%Lp' "$rc_file" 2>/dev/null)"
        if [ -n "$rc_mode" ]; then
            chmod "$rc_mode" "$tmp_file" 2>/dev/null || true
        fi
        local pending_comment=false
        local removed_any=false
        while IFS= read -r line || [ -n "$line" ]; do
            if [ "$line" = "$source_line" ]; then
                pending_comment=false
                removed_any=true
                continue
            fi
            if [ "$line" = "# netclaw shell setup" ]; then
                pending_comment=true
                continue
            fi
            if [ "$pending_comment" = true ]; then
                printf '%s\n' "# netclaw shell setup" >> "$tmp_file"
                pending_comment=false
            fi
            printf '%s\n' "$line" >> "$tmp_file"
        done < "$rc_file"
        # A trailing comment with no source line after it is not ours to remove.
        if [ "$pending_comment" = true ]; then
            printf '%s\n' "# netclaw shell setup" >> "$tmp_file"
        fi

        if [ "$removed_any" = true ]; then
            if mv "$tmp_file" "$rc_file" 2>/dev/null; then
                echo "  removed netclaw block from: $rc_file"
                report_removed "shell profile block in $rc_file"
                rm -f "$backup_file"
            else
                echo "  warning: could not rewrite $rc_file; backup at $backup_file" >&2
                report_warning "could not rewrite $rc_file"
                rm -f "$tmp_file"
            fi
        else
            rm -f "$tmp_file"
            rm -f "$backup_file"
        fi
    }

    shell_quote() {
        printf "'"
        printf '%s' "$1" | sed "s/'/'\\\\''/g"
        printf "'"
    }

    # Resolve the rc file the same way install.sh does.
    detect_shell() {
        basename "${SHELL:-/bin/sh}"
    }

    get_rc_file() {
        local shell_name="$1" shell_path="$2"
        local os
        os="$(uname -s | tr '[:upper:]' '[:lower:]')"

        case "$shell_name" in
            zsh)
                local effective_zdotdir
                effective_zdotdir="$("$shell_path" -c "printf '%s' \"\${ZDOTDIR:-\$HOME}\"" 2>/dev/null)" || return 1
                if [ -z "$effective_zdotdir" ] || [ "$effective_zdotdir" != /* ]; then
                    return 1
                fi
                echo "$effective_zdotdir/.zshrc"
                ;;
            bash)
                if [ "$os" = "darwin" ]; then
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

    SHELL_NAME="$(detect_shell)"
    case "$SHELL_NAME" in
        bash|zsh)
            if RC_FILE="$(get_rc_file "$SHELL_NAME" "${SHELL:-/bin/bash}")" && [ -n "$RC_FILE" ]; then
                remove_rc_block "$RC_FILE"
            else
                echo "  could not resolve the shell profile; skipping"
            fi
            ;;
        fish)
            FISH_CONFIG="${XDG_CONFIG_HOME:-$HOME/.config}/fish/conf.d/netclaw.fish"
            remove_file "$FISH_CONFIG" "fish conf.d file"
            ;;
        *)
            echo "  shell '$SHELL_NAME' has no automatic PATH setup; skipping"
            ;;
    esac

    # The env script is sourced by the rc block. Remove it after the block so a
    # shell never sources a missing file.
    REMOVED_ANY_FILE=false
    remove_file "$ENV_SCRIPT" "env script"
    ENV_DIR_EMPTIED=$REMOVED_ANY_FILE
    # Remove ENV_HOME_DIR only when this run emptied it. A populated Netclaw
    # home is kept, and an already-empty directory is not ours to delete.
    remove_dir_if_emptied "$ENV_HOME_DIR" "env script directory" "$ENV_DIR_EMPTIED"
else
    echo ""
    echo "Skipping shell integration (--skip-shell)."
    report_kept "shell profile and env script"
fi

# ── 6. Purge the data directory ──
if [ "$PURGE" = true ]; then
    echo ""
    echo "Removing Netclaw home..."
    if [ -d "$NETCLAW_HOME_DIR" ]; then
        if [ "$DRY_RUN" = true ]; then
            echo "  would remove: $NETCLAW_HOME_DIR"
        else
            if rm -rf "$NETCLAW_HOME_DIR" 2>/dev/null; then
                echo "  removed: $NETCLAW_HOME_DIR"
            else
                echo "  warning: could not fully remove $NETCLAW_HOME_DIR" >&2
                report_warning "could not fully remove $NETCLAW_HOME_DIR"
            fi
        fi
        report_removed "Netclaw home"
    fi
else
    report_kept "Netclaw home ($NETCLAW_HOME_DIR)"
fi

# ── Summary ──
echo ""
echo "Summary"
if [ "${#REMOVED[@]}" -eq 0 ]; then
    echo "  Nothing to remove. Netclaw was already uninstalled."
else
    echo "  Removed:"
    for item in "${REMOVED[@]}"; do
        echo "    - $item"
    done
fi

if [ "${#KEPT[@]}" -gt 0 ]; then
    echo "  Kept:"
    for item in "${KEPT[@]}"; do
        echo "    - $item"
    done
fi

if [ "${#WARNINGS[@]}" -gt 0 ]; then
    echo "  Warnings:"
    for item in "${WARNINGS[@]}"; do
        echo "    - $item"
    done
fi

if [ "$PURGE" = false ] && [ -d "$NETCLAW_HOME_DIR" ]; then
    echo ""
    echo "Your data is still at $NETCLAW_HOME_DIR."
    echo "To remove it too, run this script again with --purge."
fi

if [ "$DRY_RUN" = true ]; then
    echo ""
    echo "Dry run complete. Nothing was changed."
fi

exit 0
