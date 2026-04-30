## Context

Netclaw today ships as a pair of self-contained single-file binaries
(`netclaw`, `netclawd`) published via
`.github/workflows/publish_release_binaries.yml` to GitHub Releases and
Cloudflare R2. Operators install via the install script and run `netclaw init`
to populate `~/.netclaw/{identity,config,skills,…}`. The daemon reads its
config from `~/.netclaw/config/netclaw.json` with `NETCLAW_`-prefixed env
var overrides supported out of the box via
`IConfiguration.AddEnvironmentVariables`.

The behavioral eval suite (`./evals/run-evals.sh`) currently invokes
`netclaw -p` against the operator's own running daemon. This is the cheapest
wiring but causes two-way state contamination (issue #569): eval runs
mutate real memories and identity-adjacent state, and the operator's real
memories bias eval assertions. A minimal ephemeral-daemon approach
(`NETCLAW_HOME=$tmpdir netclawd` on the host) was considered in the
original plan, but spawning a second host-level daemon duplicates the
operator's install and still shares process-level resources.

The user-chosen path is to run `netclawd` **inside a Docker container**. The
repo already has one working proof point: `docker/smoke/Dockerfile` +
`docker-compose.smoke.yml` wire a test sandbox around `netclawd` using
env-var-driven config, and the CI smoke job runs a meaningful subset of CLI
commands against it. This change promotes that pattern to a first-class,
publishable release artifact and rewires `run-evals.sh` to use it.

**Current constraints:**

- `netclawd` assumes `/root/.netclaw/` (inside container) or
  `~/.netclaw/` (on host) is writable and populated with identity files.
  Missing identity is *silently* tolerated today (`FileSystemPromptProvider`
  uses `TryReadFile`), which violates the "No silent fallbacks" rule but
  is preserved in this change so eval identity-bind-mounts are the only
  thing adding identity to the container.
- The daemon's single-instance lock file (`LockFilePath`) is path-scoped —
  two daemons with disjoint `BasePath` values can coexist, which is why
  the container-isolated approach works without special treatment on the
  host.
- The existing `NETCLAW_` env var precedent (`NETCLAW_DAEMON_ENDPOINT` on
  the CLI side, `NETCLAW_Daemon__Port` / `NETCLAW_Providers__…` on the
  daemon side) is already proven by `docker-compose.smoke.yml`. No new
  config plumbing is needed — this change leans entirely on that existing
  surface, plus a single one-line addition to `NetclawPaths` so the CLI
  side can also be redirected during an eval.
- CI runs on a self-hosted `arc-netclaw` runner. Docker daemon is already
  present. GHCR push uses `secrets.GITHUB_TOKEN`.

**Stakeholders:**

- Operators running the eval suite locally (primary beneficiary — no more
  contamination).
- Future CI users running evals against a remote LLM endpoint (enabled by
  this change, but wiring deferred).
- Future operators deploying `netclawd` via Docker on a remote host (the
  release artifact story starts here).

## Goals / Non-Goals

**Goals:**

- Produce a release-grade `ghcr.io/netclaw-dev/netclaw` image on every
  tag, published alongside existing binary archives.
- PR-gate the Dockerfile via `.github/workflows/pr_validation.yml` so
  image regressions fail before merge.
- Make `./evals/run-evals.sh` isolate itself from the operator's host
  state: fresh DB, fresh sessions, fresh memories, every run.
- Allow operators to run evals against arbitrary LLM endpoints (including
  Tailscale-only hosts) without modifying the host network configuration.
- Require explicit eval-target credentials every invocation — never
  silently default to a provider.
- Keep one build path for the image: contributors, PR validation, and
  release publishing all invoke the same `scripts/docker/build-image.sh`.

**Non-Goals:**

- Windows or ARM image variants (aligns with the disabled matrix entries
  in `publish_release_binaries.yml` — revisit when runners exist).
- CI execution of the eval suite itself (needs a remote LLM endpoint
  secret and runtime budget — separate follow-up).
- New compaction eval cases (`NETCLAW_EVAL_CONTEXT_WINDOW` is plumbed as
  a capability, but test cases are deferred).
- Committed identity fixture under `evals/fixtures/identity/` for
  headless CI (the current change targets local-dev isolation, where
  copying from `~/.netclaw/identity` is sufficient).
- Fixing `FileSystemPromptProvider`'s silent identity tolerance (separate
  follow-up — fixing it here would break eval runs that skip the identity
  bind-mount during development).
- Runbook updates for operator Docker deployment (the image is the
  release artifact; docs can land in a follow-up PR).

## Decisions

### Decision 1: Ubuntu 24.04 base image, not chiseled runtime

**Choice**: `FROM ubuntu:24.04`, with `curl`, `wget`, `ca-certificates`,
`procps`, `git`, `jq`, `sqlite3`, `python3`, `python3-pip`,
`python3-venv`, `gh` pre-installed.

**Alternatives considered:**

1. `mcr.microsoft.com/dotnet/runtime-deps:8.0-jammy-chiseled` — ~50 MB,
   distroless, no package manager. Rejected: the autonomous agent's
   `shell_execute` tool is expected to install tools at runtime under
   high-permissions use, and chiseled images have no `apt`/`dpkg`.
2. `debian:12-slim` — similar package availability, slightly smaller.
   Rejected: team familiarity with Ubuntu; `docker/smoke/Dockerfile` is
   already Ubuntu 24.04, so staying consistent reduces the surprise area
   for contributors.
3. Alpine — smallest footprint with `apk`. Rejected: .NET self-contained
   single-file binaries need musl-compatible builds or a full glibc
   runtime, and the project's `dotnet publish` targets `linux-x64`, not
   `linux-musl-x64`.

**Trade-off**: the image lands around 300-500 MB instead of 50 MB. For a
long-running autonomous agent container this is acceptable; we're not
building a bursty serverless workload.

### Decision 2: ENTRYPOINT = netclawd (auto-start)

**Choice**: `ENTRYPOINT ["/usr/local/bin/netclawd"]`. No `CMD`, no
supervisor script, no init wait loop. The daemon starts as PID 1 and
reports `/api/health/ready` = `"healthy"` within 60 seconds once bound
to a listening port. Operators supply provider/model configuration via
`NETCLAW_`-prefixed environment variables and identity files via a
bind-mount or pre-populated volume on `/root/.netclaw`.

**Alternatives considered:**

1. `ENTRYPOINT ["sleep", "infinity"]` (smoke pattern) — container stays
   alive; operator explicitly runs `netclaw daemon start` via
   `docker exec`. Rejected: this is a test-fixture pattern, not a release
   pattern. Published images are expected to be service images, not
   long-running shell containers.
2. Supervisor script that checks `/root/.netclaw/identity/SOUL.md` and
   waits for it if missing. Rejected: hides configuration errors and
   would need to grow into an init system over time.
3. Split images (`netclaw` CLI image + `netclawd` daemon image). Rejected:
   the `complex_diagnose_self` eval case runs `netclaw doctor` via
   `shell_execute` inside the daemon container, so both binaries need to
   be present. Single image matches the smoke pattern and simplifies the
   publish path.

**Known gap — fail-fast is a follow-up, not this change's responsibility.**
The current daemon is *lenient* about missing configuration: with no
provider env vars, `Program.cs:427-430` falls back to a default
`local-ollama` provider; with no identity files, `FileSystemPromptProvider`
returns null layers that `SystemPromptAssembler.Assemble` happily tolerates.
The daemon reaches `/api/health/ready` = `"healthy"` even without any
operator-supplied config, because the health check does not exercise the
provider or identity paths. This violates the "No silent fallbacks" rule
from `CLAUDE.md` in spirit, but the fix is out of scope for this change —
it would require coordinated changes to `FileSystemPromptProvider`, the
provider loader, and the startup validation gate, which is a separate PR.
Tracked as follow-up *feat(daemon): fail loudly on empty identity and
missing provider config at startup*.

**Implication for PR validation**: the PR validation job only tests the
happy path (minimal valid config → healthy within 60s). A "fail-fast on
empty config" assertion would fail against the current daemon and is
therefore deferred until the fail-fast behavior lands.

**Failure mode** (once fail-fast is implemented): container exits
non-zero, `docker logs <name>` shows the daemon's startup error.
Recovery: operator fixes config and restarts the container. Maps cleanly
to `systemd.service` restart policies or Kubernetes `restartPolicy:
on-failure`.

**Current observable failure mode**: daemon starts, binds to
`$NETCLAW_Daemon__Host:$NETCLAW_Daemon__Port`, writes a daily-rotating
log file to `/root/.netclaw/logs/daemon-YYYY-MM-DD.log`, and runs
indefinitely. LLM calls fail at request time with provider-specific
error messages. Acceptable for eval use cases (the eval script supplies
valid config) and for operators who know what they're doing.

### Decision 3: Single build script shared by contributors, PR, and release

**Choice**: `scripts/docker/build-image.sh` is the only supported path for
building the image. The PR validation job and the release publish job both
invoke it — no inline `docker build` anywhere in the workflows.

**Alternatives considered:**

1. Inline `docker/build-push-action@v6` in the release workflow and a
   separate inline `docker build` in PR validation. Rejected: two paths
   drift. A contributor who tests locally with `docker build -f
   docker/Dockerfile` may succeed while CI fails because CI uses
   different flags.
2. `dotnet publish /t:PublishContainer` (SDK-native container support).
   Rejected: the existing `publish_release_binaries.yml` already does
   self-contained single-file publishes; driving the Docker build from
   `dotnet publish` would require reorganizing the binary publish flow
   and would lose the `build-image.sh` escape hatch for contributors
   without the .NET SDK flavor CI uses.

**Script contract:**

- Positional arg 1: image version tag (default `dev`).
- `IMAGE_REPO` env: image repository (default
  `ghcr.io/netclaw-dev/netclaw`).
- `NO_BUILD=1`: skip `dotnet publish`, reuse binaries already in
  `./publish/{cli,daemon}` (for CI jobs that publish binaries in a
  prior step, though this change's release job re-runs publish for
  simplicity).
- Exits non-zero if `./publish/cli/netclaw` or
  `./publish/daemon/netclawd` is missing at the `docker build` step.

### Decision 4: GHCR as the registry

**Choice**: `ghcr.io/netclaw-dev/netclaw`.

**Alternatives considered:**

1. Docker Hub — free for public images, wider discoverability. Rejected:
   adds a new credential surface (`DOCKERHUB_TOKEN`); first push requires
   manual user setup.
2. Cloudflare R2 as an OCI registry — keeps release artifacts in one
   place. Rejected: R2 doesn't natively speak the OCI registry protocol
   (would need a compatibility layer), and `docker pull` UX is worse.

**Implementation note**: first push creates a private GHCR package. The
repo owner must flip visibility to public under GitHub → Packages →
Settings before external users can pull without `docker login`. Call out
in the PR description.

### Decision 5: Eval script copies identity, does not mount prod directly

**Choice**: `run-evals.sh` copies `~/.netclaw/identity/` to `$EVAL_HOME/identity/`
at the start of each run, then bind-mounts `$EVAL_HOME/identity` into the
container read-only. The copy is small (~1 KB of markdown) and makes the
container completely decoupled from in-place host mutation.

**Alternatives considered:**

1. Bind-mount `~/.netclaw/identity:/root/.netclaw/identity:ro` directly.
   Rejected: if the operator edits `SOUL.md` mid-run (e.g. via a
   concurrent `netclaw init`), the eval sees the mutation. `ro` prevents
   writes *from* the container but not *into* the source.
2. Bake default identity files into the image. Rejected: the user
   explicitly wants real operators to go through `netclaw init` — image
   defaults would make it too easy to run a production daemon without
   thinking about identity.

### Decision 6: Host networking for the eval container

**Choice**: `docker run --network host` for the eval daemon container.

**Alternatives considered:**

1. Default bridged network with `-p 5299:5199`. Rejected: Tailscale
   MagicDNS hostnames (e.g. `my-gpu-server.tailnet.ts.net`) don't resolve
   inside bridged containers on Linux because MagicDNS is exposed via
   the host's `systemd-resolved` entries, not propagated into container
   DNS. `--network host` shares the host's resolver and loopback,
   sidestepping this entirely.
2. Use `--add-host` or `--dns` to inject specific Tailscale hostnames.
   Rejected: brittle, operator-specific, requires pre-knowing the
   endpoint name.

**Trade-offs:**

- Port `$NETCLAW_EVAL_PORT` (default `5299`) must be free on the host.
  The script fails loudly if it isn't — no silent fallback to a random
  port.
- The container has full access to the host's loopback stack (it can
  reach anything the operator can reach on `127.0.0.1`). Acceptable for
  a local dev tool; not acceptable for untrusted execution, but evals
  are operator-run.
- `--network host` is Linux-only. On Docker Desktop (macOS/Windows) it
  degrades to bridge mode and the Tailscale-MagicDNS case stops working.
  Documented in `evals/README.md`; macOS/Windows operators must set
  `NETCLAW_EVAL_PROVIDER_ENDPOINT` to a literal IP or non-MagicDNS
  hostname, or use `host.docker.internal`.

### Decision 7: Credential prompting is interactive, never persisted

**Choice**: `run-evals.sh` checks for the three required env vars. If
set, use them. If not set and stdin is a terminal, prompt interactively
for provider type, endpoint, and model id (read each with `read -p`).
If not set and stdin is not a terminal, fail loudly with the list of
missing vars.

The script SHALL NOT write prompted values to disk — operators who want
persistence should `export` in their shell rc file.

**Alternatives considered:**

1. Persist prompted values to `$HOME/.netclaw/evals.env`, source on next
   run. Rejected: introduces a plaintext credential file with no
   encryption story; eval endpoint URLs often include API keys.
2. Read the provider config from the host's `netclaw.json` and use the
   same provider for the eval. Rejected: host config lives in JSON with
   encrypted secrets that require the host keys directory — copying
   those into the container would leak secrets to the daemon log and
   complicate the bootstrap flow. Evals benefit from a separate, eyes-on
   decision of what to evaluate against.
3. Fail loudly if env vars are unset, no prompt. Rejected: the user
   explicitly asked for a prompt — the "always require a decision"
   intent is preserved either way, but the prompt makes the happy path
   nicer for the primary local-dev workflow.

### Decision 8: Eval script captures daemon logs via bind-mounted logs directory

**Choice**: `evals/run-evals.sh` bind-mounts `$EVAL_HOME/logs` as
`/root/.netclaw/logs` (writable) in the eval container. The script reads
per-prompt daemon log entries from
`$EVAL_HOME/logs/daemon-$(date +%F).log`, using the same file-offset
pattern the pre-container version used.

**Alternatives considered:**

1. Use `docker logs <container>` to capture daemon output. Rejected: the
   daemon writes to its own file logger (`/root/.netclaw/logs/daemon-*.log`),
   not to stdout. `docker logs` returns nothing useful. Getting stdout
   logging would require changing the daemon's logging configuration via
   additional `NETCLAW_Logging__*` env vars — a wider surface than we
   need for this change.
2. `docker exec <container> tail -f /root/.netclaw/logs/...` on demand.
   Rejected: more fragile (requires `docker exec` per assertion) and
   harder to offset-track between prompts.
3. Tee the daemon's file log into the container's stdout via an
   entrypoint wrapper script. Rejected: introduces a shell layer around
   `netclawd`, complicates PID 1 signal handling, and breaks the
   "ENTRYPOINT is the daemon binary directly" contract.

**How it works:**

- Script creates `$EVAL_HOME/logs` before `docker run`.
- Container is started with
  `-v $EVAL_HOME/logs:/root/.netclaw/logs` so the daemon's log writes
  land on the host filesystem.
- `DAEMON_LOG="$EVAL_HOME/logs/daemon-$(date +%F).log"` is the canonical
  path; assertion helpers read it with the existing `wc -l` offset
  tracking.
- On Linux there is no UID-remap issue (container root writes to a
  user-owned directory with full permissions).

**Trade-off**: requires the `/logs` subdirectory to exist on the host
before the container starts. Script handles this with `mkdir -p` in
`start_eval_daemon`.

### Decision 9: NETCLAW_HOME env var on NetclawPaths

**Choice**: One-line fallback in the `NetclawPaths` constructor to read
`Environment.GetEnvironmentVariable("NETCLAW_HOME")` when no explicit
`basePath` is passed. Precedence: explicit arg → env var → default.

**Alternatives considered:**

1. Add an `IOptions<NetclawHomeOptions>` and wire through DI. Rejected:
   `NetclawPaths` is constructed before DI is available (used during
   bootstrap config loading in `Program.cs:49,398`), so DI is
   backwards.
2. Use `AppContext.BaseDirectory`-relative resolution. Rejected: unrelated
   to operator intent; wouldn't help evals anyway.
3. Skip this change and rely on `NETCLAW_DAEMON_ENDPOINT` alone.
   Rejected: during an eval `netclaw -p` still reads `secrets.json`,
   `client/config.json`, and `keys/` via path resolution. Without
   `NETCLAW_HOME`, the CLI still touches `~/.netclaw/` state during
   eval runs — a subtle but real contamination path.

**Backward compat**: every existing caller that needs a sandbox path passes
it explicitly (see the ~30 `new NetclawPaths(_tempDir)` test sites). The
env var only fires in the zero-arg constructor path, which is used only by
the daemon's `Program.cs` bootstrap and the CLI's default initialization.
No existing test exercises the env-var code path.

## Risks / Trade-offs

- **[GHCR first-push is private]** → Documented in PR description;
  owner flips visibility to public once.
- **[`--network host` is Linux-only]** → Evals on macOS/Windows need a
  different endpoint resolution strategy. Documented in
  `evals/README.md`. Acceptable because the primary target (operator's
  local dev) is Linux.
- **[Image size 300-500 MB]** → Higher bandwidth cost per pull; not a
  problem for long-running deployments or CI pulls with layer caching.
- **[Identity bind-mount requires `netclaw init` on host]** → Scripts
  fail loudly if `~/.netclaw/identity/SOUL.md` is missing, with a
  pointer to run `netclaw init`. Clean error, not a silent fallback.
  Follow-up: committed identity fixture under `evals/fixtures/` for CI.
- **[FileSystemPromptProvider still silently tolerates missing
  identity]** → Means a misconfigured eval container can run to
  completion without any identity prompt, passing as "not netclaw". The
  assertion helpers would catch it (`identity_name` fails), but the
  root cause is hidden. Follow-up: make the daemon fail loudly on empty
  identity at startup.
- **[Daemon log capture via `docker logs`]** → `docker logs` is
  unbounded by default. For a full eval suite (~100 prompts) the stdout
  buffer grows steadily. `docker logs` pagination handles this fine for
  the suite duration, but a "live tail" eval would want `--since` or
  `--tail` flags. Not a blocker for current cases.
- **[Port 5299 collision]** → If another dev tool is bound to 5299, the
  eval fails loudly with a port-already-in-use error. Operator can
  override via `NETCLAW_EVAL_PORT`. Acceptable cost of host networking.
- **[Ephemeral container teardown on SIGINT]** → The script traps EXIT
  and calls `docker stop`. If the shell is killed with SIGKILL, the
  container leaks until the next eval run detects it by name. Mitigation:
  use `--rm` on `docker run` so Docker auto-removes on daemon exit, and
  name containers by PID so repeat runs don't collide.

## Migration Plan

No runtime migration — this is additive infrastructure.

**Rollout steps:**

1. Merge the change. Contributors immediately get `scripts/docker/build-image.sh`
   and can build locally without waiting for a release.
2. Next release tag triggers `publish-docker`, which pushes the first image
   to GHCR. The owner flips the package visibility to public.
3. Update `evals/README.md` lands with the change, so contributors who
   run evals next see the new flow.
4. Deprecate the old eval-against-host-daemon path by removing the
   `check daemon is running via `netclaw daemon status`` block from
   `check_prerequisites`. No deprecation window needed — the new flow is
   strictly better and contributors pull the change before their next
   run.

**Rollback**: revert the PR. The release workflow falls back to publishing
only binaries (existing behavior), the PR validation job is removed, and
`run-evals.sh` reverts to the host-daemon flow. No state to unwind.

## Open Questions

- **Image entrypoint user**: should the container run as a non-root user
  by default? The smoke container runs as root; the autonomous agent's
  `shell_execute` tool is happier with root because it can `apt install`
  tools at runtime. Non-root would require a named user in the image and
  may surface permission issues on bind-mounted host directories. Leaning
  toward keeping root for now and revisiting when the operator-deployment
  runbook is written.
- **Image signing**: should we cosign-sign the image like we minisign the
  release manifest? Not a blocker, deferred to a follow-up hardening pass.
- **Release workflow re-publish vs artifact reuse**: the release publish
  job currently re-runs `dotnet publish` inside its own step rather than
  reusing the binaries from the `publish-binaries` job. This is simpler
  (one container build path in the script, no cross-job artifact
  wrangling) but doubles the publish time. Acceptable because the binary
  publish is fast (~30s on the self-hosted runner). If it ever becomes a
  bottleneck, add `NO_BUILD=1` support in the release job and wire up an
  `actions/upload-artifact` + `download-artifact` pair.
