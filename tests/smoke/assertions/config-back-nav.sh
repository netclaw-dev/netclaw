#!/usr/bin/env bash
# config-back-nav.tape post-tape assertion.
#
# The tape's Wait+Screen anchors are the primary regression detectors: each
# "Settings Areas" anchor after an Esc proves the embedded Provider/Model
# manager returned to the dashboard instead of quitting the config app. If the
# app had quit, vhs would fail those anchors against the shell prompt and exit
# non-zero. This script intentionally does nothing further.

set -euo pipefail
echo "config-back-nav: no post-tape assertion (vhs exit code is the test)"
