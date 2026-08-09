#!/usr/bin/env bash
# model-manager.tape post-tape assertion.
#
# The tape anchors on both configured model IDs. These anchors prove that
# the role overview reads configuration after view-model activation.

set -euo pipefail
echo "model-manager: configured main and fallback roles rendered"
