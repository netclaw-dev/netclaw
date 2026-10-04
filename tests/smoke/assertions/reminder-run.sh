#!/usr/bin/env bash
# reminder-run.tape post-tape assertion: the chat sent /run-reminder <id>.

set -euo pipefail

if ! grep -rqF -- '/run-reminder disk-check' "${NETCLAW_HOME}"; then
  echo "FAIL: no session record under ${NETCLAW_HOME} contains '/run-reminder disk-check'." >&2
  exit 1
fi

echo "reminder-run: the chat sent /run-reminder disk-check"
