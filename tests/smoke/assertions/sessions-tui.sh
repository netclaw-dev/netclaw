#!/usr/bin/env bash
# sessions-tui.tape post-tape assertion.
#
# The tape's Wait+Screen anchors on "Sessions" and TAPE$ are the
# primary regression detectors — a rendering failure or crash exits
# vhs non-zero. This script intentionally does nothing further.

set -euo pipefail
echo "sessions-tui: no post-tape assertion (vhs exit code is the test)"
