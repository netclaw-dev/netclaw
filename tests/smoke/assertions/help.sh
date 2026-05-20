#!/usr/bin/env bash
# help.tape post-tape assertion.
#
# help.tape's purpose is to smoke-test the harness itself, not the CLI.
# vhs exit code (non-zero on any Wait+Screen timeout) is the only signal
# we need. This script intentionally does nothing.

set -euo pipefail
echo "help: no post-tape assertion (vhs exit code is the test)"
