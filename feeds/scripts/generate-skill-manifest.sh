#!/usr/bin/env bash
# generate-skill-manifest.sh — Builds manifest.json from skill files under feeds/skills/.system/files/
#
# Usage: ./feeds/scripts/generate-skill-manifest.sh [--base-url <url>] [--existing-manifest <path>]
#
# Options:
#   --base-url <url>              Base URL for skill file downloads (default: https://skills.netclaw.dev)
#   --existing-manifest <path>    Path to previously-fetched manifest for cumulative merge
#
# Outputs:
#   feeds/skills/.system/manifest.json   — manifest with skills (latest) + allVersions (cumulative)
#   feeds/skills/.system/upload-list.txt — list of files to upload to R2 (<local-path> <r2-key>)
#
# All skills use directory-based layout:
#   files/skill-name/1.0.0/SKILL.md  (+ optional references/, scripts/, assets/)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
FEEDS_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SYSTEM_DIR="$FEEDS_ROOT/skills/.system"
FILES_DIR="$SYSTEM_DIR/files"
MANIFEST_PATH="$SYSTEM_DIR/manifest.json"
UPLOAD_LIST_PATH="$SYSTEM_DIR/upload-list.txt"

# Defaults
BASE_URL="https://skills.netclaw.dev"
EXISTING_MANIFEST=""

# Parse arguments
while [ $# -gt 0 ]; do
    case "$1" in
        --base-url)
            BASE_URL="${2%/}"  # strip trailing slash
            shift 2
            ;;
        --existing-manifest)
            EXISTING_MANIFEST="$2"
            shift 2
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 1
            ;;
    esac
done

if [ ! -d "$FILES_DIR" ]; then
    echo "Error: $FILES_DIR does not exist" >&2
    exit 1
fi

NOW=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

# ── Helper: build a JSON entry for a single skill version ──
# Outputs JSON object to stdout; sets no global state.
build_entry() {
    local skill_name="$1"
    local version_dir="$2"
    local version
    version=$(basename "$version_dir")

    local main_file="$version_dir/SKILL.md"
    [ -f "$main_file" ] || return 1

    local sha256
    sha256=$(sha256sum "$main_file" | cut -d' ' -f1)
    local size_bytes
    size_bytes=$(stat -c%s "$main_file" 2>/dev/null || stat -f%z "$main_file" 2>/dev/null)

    # Extract description from YAML frontmatter
    local description
    description=$(sed -n '/^---$/,/^---$/{/^description:/{s/^description:\s*//;s/^"\(.*\)"$/\1/;p;q}}' "$main_file" 2>/dev/null)
    if [ -z "$description" ]; then
        description="System skill: $skill_name"
    fi

    local url="$BASE_URL/.system/files/$skill_name/$version/SKILL.md"

    # Build files array for resource files (non-SKILL.md files)
    local files_json=""
    local files_first=true
    while IFS= read -r -d '' resource_file; do
        local rel_path="${resource_file#"$version_dir/"}"
        [ "$rel_path" = "SKILL.md" ] && continue

        local file_sha256
        file_sha256=$(sha256sum "$resource_file" | cut -d' ' -f1)
        local file_size
        file_size=$(stat -c%s "$resource_file" 2>/dev/null || stat -f%z "$resource_file" 2>/dev/null)
        local file_url="$BASE_URL/.system/files/$skill_name/$version/$rel_path"

        if [ "$files_first" = true ]; then
            files_first=false
        else
            files_json="$files_json,"
        fi

        files_json="$files_json
        {
          \"path\": \"$rel_path\",
          \"sha256\": \"$file_sha256\",
          \"sizeBytes\": $file_size,
          \"url\": \"$file_url\"
        }"
    done < <(find "$version_dir" -type f -print0 | sort -z)

    # Emit JSON
    if [ -n "$files_json" ]; then
        cat <<ENTRY_EOF
    {
      "name": "$skill_name",
      "version": "$version",
      "minimumDaemonVersion": "0.1.0",
      "sha256": "$sha256",
      "sizeBytes": $size_bytes,
      "url": "$url",
      "category": null,
      "description": "$description",
      "files": [$files_json
      ]
    }
ENTRY_EOF
    else
        cat <<ENTRY_EOF
    {
      "name": "$skill_name",
      "version": "$version",
      "minimumDaemonVersion": "0.1.0",
      "sha256": "$sha256",
      "sizeBytes": $size_bytes,
      "url": "$url",
      "category": null,
      "description": "$description"
    }
ENTRY_EOF
    fi
}

# ── Scan all local skill versions ──

# latest_entries: JSON entries for the highest version per skill (for "skills" array)
# all_entries: JSON entries for every local version (for "allVersions" array)
LATEST_ENTRIES=""
ALL_ENTRIES=""
LATEST_FIRST=true
ALL_FIRST=true

# Also build the upload list
> "$UPLOAD_LIST_PATH"

for skill_dir in "$FILES_DIR"/*/; do
    [ -d "$skill_dir" ] || continue
    skill_name=$(basename "$skill_dir")

    # Collect all version directories, sorted by version (highest last)
    versions=()
    for vdir in "$skill_dir"*/; do
        [ -d "$vdir" ] && [ -f "$vdir/SKILL.md" ] && versions+=("$vdir")
    done
    [ ${#versions[@]} -eq 0 ] && continue

    # Sort versions using sort -V on directory basenames
    sorted_versions=()
    while IFS= read -r v; do
        sorted_versions+=("$skill_dir$v/")
    done < <(for v in "${versions[@]}"; do basename "$v"; done | sort -V)

    # Latest is the last sorted entry
    latest_dir="${sorted_versions[${#sorted_versions[@]}-1]}"

    # Build entry for latest version (goes into "skills" array)
    entry=$(build_entry "$skill_name" "$latest_dir")
    if [ -n "$entry" ]; then
        if [ "$LATEST_FIRST" = true ]; then
            LATEST_FIRST=false
        else
            LATEST_ENTRIES="$LATEST_ENTRIES,"
        fi
        LATEST_ENTRIES="$LATEST_ENTRIES
$entry"
    fi

    # Build entries for ALL versions (go into "allVersions" array)
    for vdir in "${sorted_versions[@]}"; do
        entry=$(build_entry "$skill_name" "$vdir")
        if [ -n "$entry" ]; then
            if [ "$ALL_FIRST" = true ]; then
                ALL_FIRST=false
            else
                ALL_ENTRIES="$ALL_ENTRIES,"
            fi
            ALL_ENTRIES="$ALL_ENTRIES
$entry"
        fi

        # Add files to upload list
        version=$(basename "$vdir")
        while IFS= read -r -d '' f; do
            rel="${f#"$FILES_DIR/"}"
            echo "$f .system/files/$rel" >> "$UPLOAD_LIST_PATH"
        done < <(find "$vdir" -type f -print0 | sort -z)
    done
done

# ── Merge with existing manifest (cumulative history) ──

# Track which name+version pairs we have locally so we can detect historical-only entries
EXISTING_HISTORICAL=""
if [ -n "$EXISTING_MANIFEST" ] && [ -f "$EXISTING_MANIFEST" ]; then
    if command -v python3 >/dev/null 2>&1; then
        # Extract allVersions entries from existing manifest that are NOT in our local scan.
        # This preserves historical versions that have been removed from git.
        # We pass the local entries as stdin JSON array for comparison.
        EXISTING_HISTORICAL=$(python3 -c "
import json, sys

try:
    with open('$EXISTING_MANIFEST') as f:
        existing = json.load(f)

    # Build set of (name, version) pairs from local scan
    local_pairs = set()
    all_versions = existing.get('allVersions', [])
    # Also check skills array if allVersions doesn't exist (first migration)
    if not all_versions:
        all_versions = existing.get('skills', [])

    # Read local pairs from the upload list to know what we scanned locally
    import os
    upload_list = '$UPLOAD_LIST_PATH'
    if os.path.exists(upload_list):
        with open(upload_list) as ul:
            for line in ul:
                parts = line.strip().split(' ', 1)
                if len(parts) == 2:
                    # .system/files/skill-name/version/SKILL.md
                    key_parts = parts[1].split('/')
                    if len(key_parts) >= 4:
                        local_pairs.add((key_parts[2], key_parts[3]))

    # Find entries that exist in published manifest but not locally
    historical = []
    for entry in all_versions:
        pair = (entry.get('name', ''), entry.get('version', ''))
        if pair not in local_pairs:
            historical.append(entry)

    if historical:
        # Output as comma-prefixed JSON entries for direct concatenation
        for i, h in enumerate(historical):
            print(',' + json.dumps(h, indent=6))
except Exception as e:
    print(f'# merge warning: {e}', file=sys.stderr)
" 2>/dev/null || true)
    else
        echo "Warning: python3 not found — cannot merge existing manifest history" >&2
    fi
fi

# Append historical entries to allVersions
ALL_ENTRIES="$ALL_ENTRIES$EXISTING_HISTORICAL"

# ── Write manifest ──

cat > "$MANIFEST_PATH" << EOF
{
  "schemaVersion": 1,
  "feedType": "system",
  "updatedAt": "$NOW",
  "skills": [$LATEST_ENTRIES
  ],
  "allVersions": [$ALL_ENTRIES
  ]
}
EOF

SKILL_COUNT=$(echo "$LATEST_ENTRIES" | grep -c '"name"' || echo 0)
ALL_COUNT=$(echo "$ALL_ENTRIES" | grep -c '"name"' || echo 0)
UPLOAD_COUNT=$(wc -l < "$UPLOAD_LIST_PATH" | tr -d ' ')
echo "Generated $MANIFEST_PATH with $SKILL_COUNT latest skill(s), $ALL_COUNT total version(s), $UPLOAD_COUNT file(s) to upload"
