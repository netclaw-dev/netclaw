#!/usr/bin/env bash
# model-manager.tape post-tape assertion.
#
# The tape's Wait+Screen anchors on "Model Manager" and TAPE$ are
# the primary regression detectors — a rendering failure or crash exits
# vhs non-zero. This script intentionally does nothing further.

set -euo pipefail
echo "model-manager: no post-tape assertion (vhs exit code is the test)"
