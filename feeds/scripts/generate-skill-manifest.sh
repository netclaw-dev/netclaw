#!/usr/bin/env bash
# generate-skill-manifest.sh — Builds manifest.json from skill files under feeds/skills/.system/files/
# Usage: ./feeds/scripts/generate-skill-manifest.sh
# Outputs: feeds/skills/.system/manifest.json
#
# All skills use directory-based layout:
#   files/skill-name/1.0.0/SKILL.md  (+ optional references/, scripts/, assets/)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
FEEDS_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SYSTEM_DIR="$FEEDS_ROOT/skills/.system"
FILES_DIR="$SYSTEM_DIR/files"
MANIFEST_PATH="$SYSTEM_DIR/manifest.json"

if [ ! -d "$FILES_DIR" ]; then
    echo "Error: $FILES_DIR does not exist" >&2
    exit 1
fi

# Start building the JSON
NOW=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

# Collect skill entries
ENTRIES=""
FIRST=true

for skill_dir in "$FILES_DIR"/*/; do
    [ -d "$skill_dir" ] || continue
    skill_name=$(basename "$skill_dir")

    # Find the highest version directory containing SKILL.md
    latest_dir=""
    for vdir in "$skill_dir"*/; do
        [ -d "$vdir" ] && [ -f "$vdir/SKILL.md" ] && latest_dir="$vdir"
    done

    [ -z "$latest_dir" ] && continue

    version=$(basename "$latest_dir")
    main_file="$latest_dir/SKILL.md"

    sha256=$(sha256sum "$main_file" | cut -d' ' -f1)
    size_bytes=$(stat -c%s "$main_file" 2>/dev/null || stat -f%z "$main_file" 2>/dev/null)

    # Extract description from YAML frontmatter
    description=$(sed -n '/^---$/,/^---$/{/^description:/{s/^description:\s*//;s/^"\(.*\)"$/\1/;p;q}}' "$main_file" 2>/dev/null)
    if [ -z "$description" ]; then
        description="System skill: $skill_name"
    fi

    url="https://feeds.netclaw.dev/skills/.system/files/$skill_name/$version/SKILL.md"

    if [ "$FIRST" = true ]; then
        FIRST=false
    else
        ENTRIES="$ENTRIES,"
    fi

    # Build files array for resource files (non-SKILL.md files)
    FILES_JSON=""
    FILES_FIRST=true
    while IFS= read -r -d '' resource_file; do
        rel_path="${resource_file#"$latest_dir"}"
        [ "$rel_path" = "SKILL.md" ] && continue

        file_sha256=$(sha256sum "$resource_file" | cut -d' ' -f1)
        file_size=$(stat -c%s "$resource_file" 2>/dev/null || stat -f%z "$resource_file" 2>/dev/null)
        file_url="https://feeds.netclaw.dev/skills/.system/files/$skill_name/$version/$rel_path"

        if [ "$FILES_FIRST" = true ]; then
            FILES_FIRST=false
        else
            FILES_JSON="$FILES_JSON,"
        fi

        FILES_JSON="$FILES_JSON
        {
          \"path\": \"$rel_path\",
          \"sha256\": \"$file_sha256\",
          \"sizeBytes\": $file_size,
          \"url\": \"$file_url\"
        }"
    done < <(find "$latest_dir" -type f -print0 | sort -z)

    # Build the entry JSON
    if [ -n "$FILES_JSON" ]; then
        ENTRIES="$ENTRIES
    {
      \"name\": \"$skill_name\",
      \"version\": \"$version\",
      \"minimumDaemonVersion\": \"0.1.0\",
      \"sha256\": \"$sha256\",
      \"sizeBytes\": $size_bytes,
      \"url\": \"$url\",
      \"category\": null,
      \"description\": \"$description\",
      \"files\": [$FILES_JSON
      ]
    }"
    else
        ENTRIES="$ENTRIES
    {
      \"name\": \"$skill_name\",
      \"version\": \"$version\",
      \"minimumDaemonVersion\": \"0.1.0\",
      \"sha256\": \"$sha256\",
      \"sizeBytes\": $size_bytes,
      \"url\": \"$url\",
      \"category\": null,
      \"description\": \"$description\"
    }"
    fi
done

cat > "$MANIFEST_PATH" << EOF
{
  "schemaVersion": 1,
  "feedType": "system",
  "updatedAt": "$NOW",
  "skills": [$ENTRIES
  ]
}
EOF

echo "Generated $MANIFEST_PATH with $(echo "$ENTRIES" | grep -c '"name"' || echo 0) skill(s)"
