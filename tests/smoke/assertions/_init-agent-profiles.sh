#!/usr/bin/env bash
# Verify the profile files that the native init tape produces.

assert_init_agent_profiles() {
  local agent_home="${1:?A disposable tape home is required}"
  local canonical_asset="${2:?The canonical release asset is required}"
  local worker="${agent_home}/agents/task-worker.md"
  local specialist="${agent_home}/agents/summarizer.md"

  if [[ ! -f "$worker" || -L "$worker" ]] || ! cmp -s "$canonical_asset" "$worker"; then
    echo "FAIL: init did not seed the canonical worker asset." >&2
    return 1
  fi
  if [[ ! -f "$specialist" || -L "$specialist" ]] || ! cmp -s "$specialist" <(
    printf '%s\n' '---' 'name: summarizer' 'description: Operator smoke profile' \
      'modelRole: Compaction' '---' 'Preserve operator profile.'
  ); then
    echo "FAIL: init changed the operator specialist profile." >&2
    return 1
  fi
  echo "  ok  canonical worker and operator specialist profile"
}
