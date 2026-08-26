#!/usr/bin/env bash
# Fail if any .cs file in the repo starts with a UTF-8 BOM (EF BB BF).
#
# Only byte offset 0 is checked. BOM bytes elsewhere in a file are string
# literal data (e.g. SkillScannerTests.cs embeds BOM-prefixed frontmatter as
# test input) and are legitimate, not encoding markers.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

bad=0
while IFS= read -r -d '' f; do
  if [ "$(head -c 3 "$f" | od -An -tx1 | tr -d ' \n')" = "efbbbf" ]; then
    echo "UTF-8 BOM found at start of: $f"
    bad=1
  fi
done < <(find . -name '*.cs' -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/.git/*' -print0)

if [ "$bad" -ne 0 ]; then
  echo "ERROR: one or more .cs files start with a UTF-8 BOM." >&2
  echo "Remove it, e.g.: sed -i '1s/^\xEF\xBB\xBF//' <file>" >&2
  exit 1
fi

echo "OK: no .cs file starts with a UTF-8 BOM."
