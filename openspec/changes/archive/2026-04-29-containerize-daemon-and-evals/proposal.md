## Why

The behavioral eval suite (`./evals/run-evals.sh`) runs `netclaw -p` against
the operator's live development daemon — the same `netclawd` that holds real
memories, identity, and session state. Running the suite mutates that shared
state: seeded eval documents land in the production SQLite DB, LLM-formed
memories accumulate across runs, and multi-run iterations destroy real user
memories when they reset. The inverse contamination is just as bad —
production memories compete with seeded eval documents for the 3 automatic
recall slots, which is why memory-score ran at 47/100 and dropped from 100%
→ 30% hit rate by the third sequential invocation (observed in #569).

Running `netclawd` inside an ephemeral Docker container resolves the
isolation problem cleanly and simultaneously delivers the
Docker-deployment-ready artifact the `exposure-modes` change flagged as a
blocker for remote-host operation. One build graph, two wins: eval
contamination disappears, and the daemon gains a first-class
`docker run ghcr.io/aaronontheweb/netclawd:latest` story.

## What Changes

- **Add** a release-grade `docker/Dockerfile` (Ubuntu-based, with the shell
  tools that high-permissions autonomous operation needs: `git`, `jq`,
  `sqlite3`, `python3`, `gh`, plus `netclaw` + `netclawd` binaries).
  ENTRYPOINT is `netclawd` — the daemon auto-starts on container spawn and
  fails loudly if required config is missing (no silent fallbacks).
- **Add** `scripts/docker/build-image.sh` as the single entrypoint for
  building the image. Contributors, the PR-validation job, and the
  release-publish job all call it — no parallel code paths.
- **Add** a `validate-docker-build` job to `pr_validation.yml` that runs
  `scripts/docker/build-image.sh` on every PR, then asserts two contracts
  against the built image: (1) empty-config → fast daemon exit; (2) minimal
  identity fixture + stub provider → `/api/health/ready` returns healthy
  within 60s. No GHCR push from PRs.
- **Add** a `publish-docker` job to `publish_release_binaries.yml` that
  reuses the same build script on release tags and pushes to GHCR with
  `:latest`, `:${version}`, and `:${major}.${minor}` tags.
- **Rewrite** `evals/run-evals.sh` bootstrap/teardown to spawn an ephemeral
  container per suite run, forward provider/model config via `NETCLAW_`
  env vars, bind-mount the host's `~/.netclaw/identity` read-only, capture
  daemon logs from `docker logs`, run the container with `--network host`
  so Tailscale MagicDNS hostnames (e.g. a local `my-gpu-server.tail...ts.net`
  inference endpoint) resolve inside the container, and tear down the
  container on exit. Case definitions, assertion helpers, and the run-loop
  are untouched.
- **Require** explicit eval-target credentials on every suite run. If
  `NETCLAW_EVAL_PROVIDER_TYPE`, `_ENDPOINT`, and `_MODEL_ID` env vars are
  set, use them non-interactively (CI / scripted path). If any of the
  three is missing, prompt the operator for the values interactively on
  stdin before starting the container. The script MUST NOT silently fall
  back to a default provider or model — the operator must consciously
  state which LLM they are evaluating against.
- **Add** `NETCLAW_HOME` env var support to `NetclawPaths` so CLI-side
  path resolution during an eval run is isolated from the operator's real
  `~/.netclaw` state. Single-line constructor change; every existing caller
  passes an explicit `basePath`, so no behavior change on existing paths.
- **Update** `evals/README.md` to document the new env-var surface
  (`NETCLAW_EVAL_PROVIDER_TYPE`, `_ENDPOINT`, `_MODEL_ID`,
  `_CONTEXT_WINDOW`, `NETCLAW_IMAGE`, `NETCLAW_EVAL_PORT`) and remove the
  "Local instance only — no isolation" limitation.

**In scope for MVP**: Linux/x64 Docker image only; published to GHCR; evals
run locally against a host-provided LLM endpoint (self-hosted or cloud).

**Out of scope / deferred**:

- Windows/ARM image variants (mirror the disabled entries in the existing
  binary matrix; revisit when self-hosted ARM/Windows runners exist).
- CI execution of the eval suite (needs remote LLM endpoint secret).
  Tracked as a follow-up.
- Compaction eval cases using `NETCLAW_EVAL_CONTEXT_WINDOW`. The
  infrastructure enables them; adding cases is a separate, low-risk PR.
- `#437` checkpoint-drain wait between memory formation and recall phases.
- Committed identity fixture at `evals/fixtures/identity/` (needed for CI,
  not for the local-dev use case this change targets).
- Self-bootstrapping identity inside the container on first run.

## Capabilities

### New Capabilities

- `daemon-container`: Packaging, publishing, validating, and running
  `netclawd` as a Docker image. Covers image structure and base choice,
  pre-installed tool contract, auto-start ENTRYPOINT behavior, fail-fast
  on missing config, env-var configuration surface inherited from the
  existing `NETCLAW_` prefix, volume mount contract for
  `/root/.netclaw`, release-workflow publishing to GHCR, PR-time build
  validation, and the behavioral eval suite's use of the image as an
  ephemeral isolation boundary.

### Modified Capabilities

- `netclaw-cli`: Add a requirement that `NetclawPaths` (used by both CLI
  and daemon binaries) honours a `NETCLAW_HOME` environment variable when
  no explicit base path is provided. This unblocks CLI-side path isolation
  during eval runs and matches the `NETCLAW_DAEMON_ENDPOINT` env var
  precedent already established by `DaemonApi.ResolveEndpoint`.

## Impact

- **Affected code**:
  - `src/Netclaw.Configuration/NetclawPaths.cs:86-92` — constructor reads
    `NETCLAW_HOME` env var as a fallback source for `BasePath`.
  - `evals/run-evals.sh` — bootstrap, prerequisites, `check_daemon_alive`,
    `run_prompt`, `daemon_log_tail`, and `main` rewritten for the container
    lifecycle. Case bodies unchanged.
  - `evals/README.md` — documentation refresh.
- **New files**:
  - `docker/Dockerfile` — release-grade image (distinct from
    `docker/smoke/Dockerfile`, which continues to serve the Ollama-in-Docker
    smoke sandbox).
  - `scripts/docker/build-image.sh` — shared build entrypoint.
- **Workflows**:
  - `.github/workflows/pr_validation.yml` — adds `validate-docker-build`
    job gated on PR events, self-hosted runner.
  - `.github/workflows/publish_release_binaries.yml` — adds `publish-docker`
    job after `publish-binaries`; publishes to `ghcr.io/aaronontheweb/netclawd`
    on tag push with semver + latest tags.
- **Release artifact surface**: new Docker image published to GHCR alongside
  the existing tar.gz binary archives and skills feed. First push will
  create a private GHCR package; owner must flip it to public before
  external users can pull without auth.
- **Dependencies**: No new NuGet packages. Ubuntu base image chosen over
  `runtime-deps:8.0-jammy-chiseled` because the autonomous agent use case
  requires `apt install`-capable tooling at runtime; accept ~300-500 MB
  image size as the cost.
- **Security impact**:
  - `NETCLAW_HOME` env var on `NetclawPaths` is backward-compatible and
    honors explicit `basePath` arguments first. No path-traversal surface
    added — the env var is read once at construction and Path.Combine is
    already the standard.
  - Daemon auto-start on container spawn fails loudly when required config
    is missing, preserving the "No silent fallbacks" rule from `CLAUDE.md`.
  - Container runs as root by default (matches smoke pattern). Operator is
    expected to pair this with host-level isolation (Docker's default
    namespaces + seccomp). Document in the Dockerfile and README; do not
    attempt to drop privileges inside the container since the
    shell_execute tool needs broad permissions.
  - Identity bind-mount during evals is read-only (`:ro`), preventing the
    eval run from mutating the operator's real identity files.
- **Operational impact**:
  - Running evals now requires `docker` on the host. Documented as a
    prerequisite in `evals/README.md`.
  - `./evals/run-evals.sh` no longer requires the host's dev daemon to be
    running; in fact, the host daemon is irrelevant to the eval run.
  - `--network host` is the default container network mode for evals. This
    inherits the host's DNS resolver (so Tailscale MagicDNS entries like
    `my-gpu-server.tail...ts.net` resolve automatically) and loopback namespace
    (so `http://127.0.0.1:1234/v1`-style LLM endpoints work without port
    mapping). It also means the container binds `$NETCLAW_EVAL_PORT`
    directly on the host — the script fails loudly if the port is already
    in use, and operators can override via `NETCLAW_EVAL_PORT`.
  - Interactive credential prompting: when the three required eval env
    vars are missing, the script prompts for provider type, endpoint, and
    model id on stdin. Operators who want persistence should `export`
    them in their shell rc file — the script does not write credentials
    to disk.
  - Operators deploying via Docker now have a supported image path;
    documented as a follow-up runbook update (deferred — not required for
    this change).
- **Related PRD/spec linkage**: complements the `exposure-modes` change
  (Docker deployments on remote hosts explicitly listed as a driver there);
  does not modify `daemon-exposure` requirements since that capability is
  still unarchived and targets tunnel validation, not image packaging.
