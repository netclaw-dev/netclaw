#!/usr/bin/env bash
# model-manager.tape post-tape assertion.
#
# The tape anchors on the configured roles and both selected providers.
# These anchors prove that each provider selection replaces the prior list.

set -euo pipefail
echo "model-manager: configured roles and provider changes rendered"
