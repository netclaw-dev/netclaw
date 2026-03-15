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
# Skills use flat directory layout (version comes from YAML frontmatter):
#   files/skill-name/SKILL.md  (+ optional references/, scripts/, assets/)

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

# ── Helper: extract version from YAML frontmatter ──
# Reads metadata.version from the SKILL.md file
extract_version() {
    local file="$1"
    # Look for version in metadata block or top-level
    # Handles: version: "1.0.0" or version: 1.0.0
    sed -n '/^---$/,/^---$/{/^\s*version:/{s/^.*version:\s*//;s/^"\(.*\)"$/\1/;s/'"'"'\(.*\)'"'"'/\1/;p;q}}' "$file" 2>/dev/null
}

# ── Helper: build a JSON entry for a single skill ──
build_entry() {
    local skill_name="$1"
    local skill_dir="$2"
    local main_file="$skill_dir/SKILL.md"

    [ -f "$main_file" ] || return 1

    local version
    version=$(extract_version "$main_file")
    if [ -z "$version" ]; then
        echo "Warning: no metadata.version in $main_file — skipping" >&2
        return 1
    fi

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
        local rel_path="${resource_file#"$skill_dir/"}"
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
    done < <(find "$skill_dir" -type f -print0 | sort -z)

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

# ── Scan all local skills ──

LATEST_ENTRIES=""
ALL_ENTRIES=""
LATEST_FIRST=true
ALL_FIRST=true

# Build the upload list
> "$UPLOAD_LIST_PATH"

for skill_dir in "$FILES_DIR"/*/; do
    [ -d "$skill_dir" ] || continue
    [ -f "$skill_dir/SKILL.md" ] || continue
    skill_name=$(basename "$skill_dir")

    # Read version from frontmatter
    version=$(extract_version "$skill_dir/SKILL.md")
    if [ -z "$version" ]; then
        echo "Warning: no metadata.version in $skill_dir/SKILL.md — skipping" >&2
        continue
    fi

    # Build entry (goes into both "skills" and "allVersions" arrays)
    entry=$(build_entry "$skill_name" "$skill_dir")
    if [ -n "$entry" ]; then
        if [ "$LATEST_FIRST" = true ]; then
            LATEST_FIRST=false
        else
            LATEST_ENTRIES="$LATEST_ENTRIES,"
        fi
        LATEST_ENTRIES="$LATEST_ENTRIES
$entry"

        if [ "$ALL_FIRST" = true ]; then
            ALL_FIRST=false
        else
            ALL_ENTRIES="$ALL_ENTRIES,"
        fi
        ALL_ENTRIES="$ALL_ENTRIES
$entry"
    fi

    # Add files to upload list (R2 key uses version from frontmatter)
    while IFS= read -r -d '' f; do
        rel="${f#"$skill_dir"}"
        echo "$f .system/files/$skill_name/$version/$rel" >> "$UPLOAD_LIST_PATH"
    done < <(find "$skill_dir" -type f -print0 | sort -z)
done

# ── Merge with existing manifest (cumulative history) ──

EXISTING_HISTORICAL=""
if [ -n "$EXISTING_MANIFEST" ] && [ -f "$EXISTING_MANIFEST" ]; then
    if command -v python3 >/dev/null 2>&1; then
        # Extract allVersions entries from existing manifest that are NOT in our local scan.
        # This preserves historical versions that have been removed from git or superseded
        # by a version bump in frontmatter.
        EXISTING_HISTORICAL=$(python3 -c "
import json, sys, os

try:
    with open('$EXISTING_MANIFEST') as f:
        existing = json.load(f)

    # Build set of (name, version) pairs from local scan via upload list
    local_pairs = set()
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

    # Check allVersions first, fall back to skills array (first migration)
    all_versions = existing.get('allVersions', [])
    if not all_versions:
        all_versions = existing.get('skills', [])

    # Find entries that exist in published manifest but not locally
    historical = []
    for entry in all_versions:
        pair = (entry.get('name', ''), entry.get('version', ''))
        if pair not in local_pairs:
            historical.append(entry)

    if historical:
        for h in historical:
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
