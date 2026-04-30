#!/usr/bin/env bash
# generate-release-manifest.sh — Builds releases/manifest.json from checksum artifacts
#
# Usage: ./feeds/scripts/generate-release-manifest.sh <version> <checksums-dir> <base-url>
#   version       — release version tag (e.g. "0.2.0")
#   checksums-dir — directory containing checksum files from CI matrix legs
#   base-url      — base URL for binary downloads (e.g. "https://releases.netclaw.dev")
#
# Input: checksum files named checksums-<rid>.txt containing lines like:
#   abc123def456  netclaw-0.2.0-linux-x64.tar.gz  12345678
#   fed321abc654  netclawd-0.2.0-linux-x64.tar.gz  87654321
#
# Output: feeds/releases/manifest.json

set -euo pipefail

if [ $# -lt 3 ]; then
    echo "Usage: $0 <version> <checksums-dir> <base-url>" >&2
    echo "Example: $0 0.2.0 ./checksums https://releases.netclaw.dev" >&2
    exit 1
fi

VERSION="$1"
CHECKSUMS_DIR="$2"
BASE_URL="${3%/}"  # strip trailing slash

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
FEEDS_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
MANIFEST_PATH="$FEEDS_ROOT/releases/manifest.json"

if [ ! -d "$CHECKSUMS_DIR" ]; then
    echo "Error: checksums directory '$CHECKSUMS_DIR' does not exist" >&2
    exit 1
fi

NOW=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

# Collect asset entries from all checksum files
ASSETS=""
FIRST=true

for checksums_file in "$CHECKSUMS_DIR"/checksums-*.txt; do
    [ -f "$checksums_file" ] || continue

    # Extract RID from filename: checksums-linux-x64.txt -> linux-x64
    rid=$(basename "$checksums_file" .txt)
    rid="${rid#checksums-}"

    while IFS= read -r line || [ -n "$line" ]; do
        [ -z "$line" ] && continue

        # Parse: <sha256>  <filename>  <size_bytes>
        sha256=$(echo "$line" | awk '{print $1}')
        filename=$(echo "$line" | awk '{print $2}')
        size_bytes=$(echo "$line" | awk '{print $3}')

        # Determine component from filename
        # netclaw-0.2.0-linux-x64.tar.gz -> netclaw
        # netclawd-0.2.0-linux-x64.tar.gz -> netclawd
        component=$(echo "$filename" | sed -E "s/^(netclawd?)-${VERSION}-.*/\1/")

        # Build download URL
        url="${BASE_URL}/${VERSION}/${filename}"

        if [ "$FIRST" = true ]; then
            FIRST=false
        else
            ASSETS="${ASSETS},"
        fi

        ASSETS="${ASSETS}
        {
          \"component\": \"${component}\",
          \"rid\": \"${rid}\",
          \"url\": \"${url}\",
          \"sha256\": \"${sha256}\",
          \"sizeBytes\": ${size_bytes}
        }"
    done < "$checksums_file"
done

if [ "$FIRST" = true ]; then
    echo "Error: no checksum files found in '$CHECKSUMS_DIR'" >&2
    exit 1
fi

# Check if an existing manifest has previous releases
EXISTING_RELEASES=""
if [ -f "$MANIFEST_PATH" ]; then
    # Extract existing releases that aren't the current version
    # Uses python if available, otherwise starts fresh
    if command -v python3 >/dev/null 2>&1; then
        EXISTING_RELEASES=$(python3 -c "
import json, sys
try:
    with open('$MANIFEST_PATH') as f:
        m = json.load(f)
    releases = [r for r in m.get('releases', []) if r['version'] != '$VERSION']
    if releases:
        print(',' + json.dumps(releases)[1:-1])
except:
    pass
" 2>/dev/null || true)
    fi
fi

# GitHub release notes URL
RELEASE_NOTES_URL="https://github.com/netclaw-dev/netclaw/releases/tag/${VERSION}"

mkdir -p "$(dirname "$MANIFEST_PATH")"
cat > "$MANIFEST_PATH" << EOF
{
  "schemaVersion": 1,
  "feedType": "releases",
  "updatedAt": "${NOW}",
  "latest": "${VERSION}",
  "releases": [
    {
      "version": "${VERSION}",
      "releasedAt": "${NOW}",
      "releaseNotesUrl": "${RELEASE_NOTES_URL}",
      "assets": [${ASSETS}
      ]
    }${EXISTING_RELEASES}
  ]
}
EOF

ASSET_COUNT=$(echo "$ASSETS" | grep -c '"component"' || echo 0)
echo "Generated $MANIFEST_PATH for v${VERSION} with ${ASSET_COUNT} asset(s)"
