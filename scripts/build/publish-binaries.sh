#!/usr/bin/env bash
# Canonical publish for Netclaw self-contained single-file binaries.
#
# This script is the SINGLE SOURCE OF TRUTH for the `dotnet publish` flags
# used to produce shipped binaries. The release pipeline
# (.github/workflows/publish_release_binaries.yml), the Docker image build
# (scripts/docker/build-image.sh), and the smoke-test harness all call this
# script — so the binaries they each produce are identical, and a
# platform-specific publish bug is caught by smoke before it ships.
#
# Per-platform publish flags live in the `case "$RID"` block below. Do not
# add a second copy of these flags anywhere else.
#
# Usage:
#   scripts/build/publish-binaries.sh --rid <rid> [--component cli|daemon|all]
#                                     [--output-dir <dir>] [--version <ver>]
#
#   --rid          required: linux-x64 | linux-arm64 | win-x64 | osx-arm64
#   --component    cli | daemon | all   (default: all)
#   --output-dir   base output dir; components land in <dir>/cli and
#                  <dir>/daemon   (default: ./publish)
#   --version      assembly version; when omitted, no -p:Version is passed
#                  and the Directory.Build.props VersionPrefix is used
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

RID=""
COMPONENT="all"
OUTPUT_DIR="./publish"
VERSION=""

usage() {
  sed -n '2,23p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-2}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid)        RID="${2:-}"; shift 2 ;;
    --component)  COMPONENT="${2:-}"; shift 2 ;;
    --output-dir) OUTPUT_DIR="${2:-}"; shift 2 ;;
    --version)    VERSION="${2:-}"; shift 2 ;;
    -h|--help)    usage 0 ;;
    *)            echo "ERROR: unknown argument '$1'" >&2; usage ;;
  esac
done

if [[ -z "$RID" ]]; then
  echo "ERROR: --rid is required" >&2
  usage
fi

case "$COMPONENT" in
  cli|daemon|all) ;;
  *) echo "ERROR: --component must be cli, daemon, or all (got '$COMPONENT')" >&2; exit 2 ;;
esac

# Flags common to every platform.
#
# MSBuild properties MUST use the `-p:` form, not `/p:`. On the Windows
# release runner this script executes under Git Bash, whose MSYS argument
# conversion strips the leading `/` from `/p:Name=value` — MSBuild then
# receives a bare `p:Name=value` token and aborts with MSB1008 ("Only one
# project can be specified"). `-p:` is immune to that conversion and is
# accepted identically by `dotnet` on every shell and platform.
COMMON=(
  -c Release
  -r "$RID"
  --self-contained true
  -p:PublishSingleFile=true
  -p:IncludeNativeLibrariesForSelfExtract=true
)

# Per-platform single-file compression. Kept centralized here so the release
# pipeline and the smoke harness publish with identical flags.
case "$RID" in
  osx-arm64)
    # dotnet/runtime #123324: EnableCompressionInSingleFile corrupts memory
    # via the MAP_JIT RW->RWX transition on Apple Silicon during single-file
    # self-extract — surfaces as a fatal AccessViolationException (netclaw
    # #1036). Upstream fix dotnet/runtime #127355 ships in .NET 11; re-converge
    # this arm with the others when it backports to .NET 10 servicing.
    COMPRESS=false ;;
  linux-x64|linux-arm64|win-x64) COMPRESS=true ;;
  *) echo "ERROR: unsupported RID '$RID'" >&2; exit 2 ;;
esac

EXTRA=(-p:EnableCompressionInSingleFile="$COMPRESS")
if [[ -n "$VERSION" ]]; then
  EXTRA+=(-p:Version="$VERSION")
fi

publish_component() {
  local name="$1" project="$2"
  echo "→ Publishing ${name} (${RID})..."
  dotnet publish "$project" \
    "${COMMON[@]}" \
    "${EXTRA[@]}" \
    -o "${OUTPUT_DIR}/${name}"
}

if [[ "$COMPONENT" == "cli" || "$COMPONENT" == "all" ]]; then
  publish_component cli src/Netclaw.Cli/Netclaw.Cli.csproj
fi
if [[ "$COMPONENT" == "daemon" || "$COMPONENT" == "all" ]]; then
  publish_component daemon src/Netclaw.Daemon/Netclaw.Daemon.csproj
fi

echo "✓ publish-binaries.sh: ${COMPONENT} (${RID}) → ${OUTPUT_DIR}"
