#!/usr/bin/env bash
# generate-skill-manifest.sh — Builds manifest.json from skill files under feeds/skills/.system/files/
# Usage: ./feeds/scripts/generate-skill-manifest.sh
# Outputs: feeds/skills/.system/manifest.json

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

    # Find the highest version file (sort by semver — simple lexicographic works for x.y.z format)
    latest_file=$(ls -1 "$skill_dir"*.md 2>/dev/null | sort -V | tail -n 1)
    [ -z "$latest_file" ] && continue

    version=$(basename "$latest_file" .md)
    sha256=$(sha256sum "$latest_file" | cut -d' ' -f1)
    size_bytes=$(stat -c%s "$latest_file" 2>/dev/null || stat -f%z "$latest_file" 2>/dev/null)

    # Extract description from <!-- description: ... --> comment
    description=$(grep -oP '<!--\s*description:\s*\K.+?(?=\s*-->)' "$latest_file" 2>/dev/null || echo "System skill: $skill_name")

    # Build the relative URL for CDN
    url="https://feeds.netclaw.dev/skills/.system/files/$skill_name/$version.md"

    if [ "$FIRST" = true ]; then
        FIRST=false
    else
        ENTRIES="$ENTRIES,"
    fi

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
