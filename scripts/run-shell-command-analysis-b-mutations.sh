#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exec "$repo_root/scripts/run-shell-command-analysis-mutations.sh" --part b
