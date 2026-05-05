#!/usr/bin/env bash
# Build the netclawd release Docker image locally.
#
# This script is the single entrypoint for building the release image.
# Contributors, the PR validation workflow, and the release publish workflow
# all invoke it — no parallel `docker build` code paths.
#
# Usage:
#   scripts/docker/build-image.sh                       # builds :dev
#   scripts/docker/build-image.sh v0.11.1                # builds :v0.11.1
#   IMAGE_REPO=ghcr.io/myuser/netclawd \
#     scripts/docker/build-image.sh v0.11.1              # custom repo
#   NO_BUILD=1 scripts/docker/build-image.sh v0.11.1     # skip dotnet publish,
#                                                        # reuse existing ./publish output
#
# Environment:
#   IMAGE_REPO   Registry + image name (default: ghcr.io/netclaw-dev/netclaw)
#   NO_BUILD     Set to 1 to skip `dotnet publish` (binaries must already exist)
#   RID          Runtime identifier (default: linux-x64)
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

VERSION="${1:-dev}"
PUBLISH_ONLY=false
if [[ "${2:-}" == "--publish-only" ]]; then
    PUBLISH_ONLY=true
fi
IMAGE_REPO="${IMAGE_REPO:-ghcr.io/netclaw-dev/netclaw}"
IMAGE_TAG="${IMAGE_REPO}:${VERSION}"
RID="${RID:-linux-x64}"
NO_BUILD="${NO_BUILD:-0}"

# `dotnet publish /p:Version=…` requires a valid .NET assembly version.
# Accept tags like `v0.11.1`, `0.11.1`, or `0.11.1-alpha.1` — strip a
# leading `v` if present. For development tags like `dev` that don't
# parse, fall back to the repo's VersionPrefix from Directory.Build.props
# so the produced assembly is still tagged correctly.
if [[ "$VERSION" =~ ^v?[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
    ASSEMBLY_VERSION="${VERSION#v}"
else
    ASSEMBLY_VERSION=""
fi

echo "→ Netclaw Docker image build"
echo "  Version:          $VERSION"
echo "  Assembly version: ${ASSEMBLY_VERSION:-<Directory.Build.props default>}"
echo "  Tag:              $IMAGE_TAG"
echo "  RID:              $RID"
echo "  NO_BUILD:         $NO_BUILD"

publish_args=(
    -c Release -r "$RID" --self-contained true
    /p:PublishSingleFile=true
    /p:EnableCompressionInSingleFile=true
    /p:IncludeNativeLibrariesForSelfExtract=true
)
if [[ -n "$ASSEMBLY_VERSION" ]]; then
    publish_args+=(/p:Version="$ASSEMBLY_VERSION")
fi

if [[ "$NO_BUILD" != "1" ]]; then
    echo "→ Publishing CLI ($RID)..."
    dotnet publish src/Netclaw.Cli/Netclaw.Cli.csproj \
        "${publish_args[@]}" \
        -o ./publish/cli

    echo "→ Publishing Daemon ($RID)..."
    dotnet publish src/Netclaw.Daemon/Netclaw.Daemon.csproj \
        "${publish_args[@]}" \
        -o ./publish/daemon
else
    echo "→ NO_BUILD=1 — skipping dotnet publish"
fi

# Fail loudly if the binaries aren't where the Dockerfile expects them.
if [[ ! -x ./publish/cli/netclaw ]]; then
    echo "ERROR: ./publish/cli/netclaw is missing or not executable." >&2
    echo "       Run without NO_BUILD=1 or publish the CLI binary first." >&2
    exit 1
fi
if [[ ! -x ./publish/daemon/netclawd ]]; then
    echo "ERROR: ./publish/daemon/netclawd is missing or not executable." >&2
    echo "       Run without NO_BUILD=1 or publish the daemon binary first." >&2
    exit 1
fi

# Map RID to Docker TARGETARCH so the Dockerfile's arch-suffixed COPY works.
case "$RID" in
    linux-x64)  DOCKER_ARCH="amd64" ;;
    linux-arm64) DOCKER_ARCH="arm64" ;;
    *) echo "ERROR: unsupported RID '$RID' for Docker build" >&2; exit 1 ;;
esac

# Hard-link copy the single-arch publish output to the arch-suffixed paths
# the Dockerfile expects (publish/cli-{arch}/, publish/daemon-{arch}/).
# Symlinks don't survive Docker Buildx context transfer reliably.
rm -rf "publish/cli-${DOCKER_ARCH}" "publish/daemon-${DOCKER_ARCH}" 2>/dev/null || true
cp -rl publish/cli "publish/cli-${DOCKER_ARCH}"
cp -rl publish/daemon "publish/daemon-${DOCKER_ARCH}"

if [[ "$PUBLISH_ONLY" == "true" ]]; then
    echo "✓ Publish complete (--publish-only). Skipping Docker build."
    exit 0
fi

echo "→ Building image $IMAGE_TAG..."
docker build \
    -f docker/Dockerfile \
    -t "$IMAGE_TAG" \
    --build-arg "NETCLAW_VERSION=$VERSION" \
    .

echo "✓ Built $IMAGE_TAG"
