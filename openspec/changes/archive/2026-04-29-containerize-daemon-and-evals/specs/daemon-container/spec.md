## ADDED Requirements

### Requirement: Release-grade Docker image published on tag

The project SHALL publish a `netclawd` Docker image to the GitHub Container
Registry (`ghcr.io/netclaw-dev/netclaw`) on every release tag. The image
SHALL be tagged with the exact version (`{{version}}`), major.minor
(`{{major}}.{{minor}}`), and `latest`. The image SHALL be built from
`docker/Dockerfile` via the shared `scripts/docker/build-image.sh` entrypoint
so that PR validation and release publishing share one code path.

#### Scenario: Tag push publishes all three tag aliases

- **GIVEN** the release workflow runs on a tag push of `v0.12.0`
- **WHEN** the `publish-docker` job succeeds
- **THEN** `ghcr.io/netclaw-dev/netclaw:v0.12.0`, `:v0.12`, and `:latest`
  all reference the same image digest

#### Scenario: PR validation and release use the same build script

- **GIVEN** both the `validate-docker-build` and `publish-docker` jobs run
  on a release commit
- **WHEN** the release workflow builds its image
- **THEN** the image is produced by invoking `scripts/docker/build-image.sh`
  identically to how the PR validation job invokes it — no inline
  `docker build` commands in the workflow YAML

### Requirement: Image entrypoint auto-starts netclawd

The image SHALL declare `ENTRYPOINT ["/usr/local/bin/netclawd"]`. Starting
the container via `docker run` SHALL launch the daemon as PID 1 without
requiring any additional command, matching the Docker idiom for published
service images (Postgres, Redis, Elasticsearch).

#### Scenario: docker run starts the daemon

- **GIVEN** the image is present locally
- **WHEN** an operator runs `docker run -d --rm <image>` with the minimum
  valid configuration env vars and an identity bind-mount
- **THEN** `netclawd` is PID 1 inside the container
- **AND** the daemon binds its HTTP port within 60 seconds

### Requirement: Minimal valid configuration reaches healthy state

The image SHALL reach a healthy state within 60 seconds of container start
when provided with a minimal valid configuration: a `NETCLAW_Daemon__Host`
override of `0.0.0.0` (so the HTTP listener accepts non-loopback requests
from port-mapped or host-networked clients) and either provider
configuration env vars or the daemon's built-in default provider. The
`/api/health/ready` endpoint SHALL return `"healthy"` once the daemon
finishes startup.

#### Scenario: Minimal valid config reaches healthy state

- **GIVEN** a container started with `NETCLAW_Daemon__Host=0.0.0.0` and the
  host forwarding a port to 5199
- **WHEN** the daemon finishes startup
- **THEN** `GET /api/health/ready` returns `"healthy"` within 60 seconds
- **AND** the container is still running

#### Scenario: Eval-style minimal config reaches healthy state

- **GIVEN** a container started with `NETCLAW_Daemon__Host=0.0.0.0`, a
  provider env-var triple (`NETCLAW_Providers__<name>__Type`, `__Endpoint`,
  and matching `NETCLAW_Models__Main__Provider`/`__ModelId`), and a
  read-only identity bind-mount
- **WHEN** the daemon finishes startup
- **THEN** `GET /api/health/ready` returns `"healthy"` within 60 seconds

### Requirement: Env-var configuration surface

The image SHALL accept all daemon configuration via environment variables
prefixed with `NETCLAW_`, using `__` as the `IConfiguration` section
separator (e.g. `NETCLAW_Daemon__Port=5299`,
`NETCLAW_Providers__eval__Endpoint=http://127.0.0.1:1234/v1`). No config
file baked into the image SHALL supply provider credentials or model
selection — these MUST come from the operator at `docker run` time.

#### Scenario: Env vars override defaults

- **GIVEN** `NETCLAW_Daemon__Port=5299` is passed to `docker run`
- **WHEN** the daemon starts
- **THEN** the HTTP listener binds on port 5299, not the default 5199

#### Scenario: No image-baked provider credentials

- **GIVEN** the freshly built image
- **WHEN** `docker image inspect` examines the image layers
- **THEN** no layer contains a `config/netclaw.json` or `config/secrets.json`
  file with a non-empty `Providers` section

### Requirement: Operator state mounts at /root/.netclaw

The image SHALL declare `VOLUME /root/.netclaw` so operators can mount a
host directory (or anonymous volume) to persist identity files, session
state, SQLite DB, and logs. The image SHALL NOT pre-populate this path
with identity files, config, or secrets — real operators are expected to
produce these via `netclaw init` before starting the container.

#### Scenario: Operator bind-mounts an initialized home

- **GIVEN** an operator has previously run `netclaw init` on the host and
  has a populated `~/.netclaw/`
- **WHEN** they run `docker run -v ~/.netclaw:/root/.netclaw <image>`
- **THEN** the container daemon reads their identity and config from the
  mounted directory
- **AND** writes new session state back to the host path

### Requirement: Image includes common autonomous-agent tooling

The image SHALL include the shell tools that the autonomous agent commonly
invokes via its `shell_execute` tool: `curl`, `wget`, `git`, `jq`,
`sqlite3`, `python3`, and `gh`. The image SHALL also include the companion
`netclaw` CLI binary on PATH so in-container `shell_execute` calls can run
the CLI directly (for example, `netclaw doctor`).

#### Scenario: CLI on PATH inside container

- **GIVEN** a running container
- **WHEN** `docker exec <container> which netclaw` is invoked
- **THEN** it returns a path under `/usr/local/bin/` and exits 0

#### Scenario: Common shell tools available

- **GIVEN** a running container
- **WHEN** `docker exec <container> bash -c "command -v git jq sqlite3 python3 gh curl wget"` runs
- **THEN** every tool resolves to a non-empty path and exits 0

### Requirement: Base image permits runtime apt install

The image SHALL be based on an operating system that supports package
installation at runtime (Ubuntu, Debian, or similar). The image SHALL NOT
be based on a chiseled or distroless runtime that removes `apt`/`dpkg`,
because the autonomous agent's high-permissions use case requires the
ability to install additional tools on demand.

#### Scenario: apt-get update succeeds inside container

- **GIVEN** a running container with network access
- **WHEN** `docker exec <container> apt-get update` runs
- **THEN** the command exits 0

### Requirement: Local build script is the single build entrypoint

The project SHALL provide `scripts/docker/build-image.sh` as the only
supported path for building the release image. The script SHALL publish
self-contained `linux-x64` binaries for `netclaw` and `netclawd` to
`./publish/{cli,daemon}` and invoke `docker build` against
`docker/Dockerfile`. The script SHALL support an `IMAGE_REPO` environment
variable so contributors can push to a personal fork and a positional
version argument (default `dev`). The script SHALL exit non-zero if the
expected binary outputs are absent after `dotnet publish`.

#### Scenario: Default invocation builds :dev tag

- **WHEN** a contributor runs `scripts/docker/build-image.sh` with no arguments
- **THEN** the script builds `ghcr.io/netclaw-dev/netclaw:dev`
- **AND** `docker images` lists the tag

#### Scenario: Custom version and repo

- **WHEN** a contributor runs `IMAGE_REPO=ghcr.io/user/nc scripts/docker/build-image.sh v0.11.1`
- **THEN** the script builds `ghcr.io/user/nc:v0.11.1`

#### Scenario: Missing binaries fail loudly

- **GIVEN** `./publish/daemon/netclawd` has been deleted mid-run
- **WHEN** the script reaches the `docker build` step
- **THEN** the script prints an error naming the missing path
- **AND** exits with a non-zero status before running `docker build`

### Requirement: Dedicated Docker validation workflow

The project SHALL ship a standalone GitHub Actions workflow
(`.github/workflows/validate_docker_image.yml`) that builds and
smoke-tests the release Docker image on every pull request and on
pushes to `dev`/`main`/`master`. The workflow SHALL NOT be lumped
into `pr_validation.yml` (.NET test suites + slopwatch) or
`smoke_sandbox.yml` (Ollama-in-Docker end-to-end), because image
construction is an orthogonal concern with its own failure mode.

The workflow SHALL build the image via `scripts/docker/build-image.sh`
with no registry push, then start the image with a stub ollama
provider and verify that `GET /api/health/ready` returns `"healthy"`
within 60 seconds. The workflow SHALL NOT authenticate to any
container registry and SHALL NOT push any image.

#### Scenario: Broken Dockerfile fails a PR

- **GIVEN** a PR that changes `docker/Dockerfile` to reference a non-existent
  base image
- **WHEN** the `validate-docker-build` job runs
- **THEN** the job fails at the `docker build` step
- **AND** PR status shows the failure

#### Scenario: Missing binary fails a PR

- **GIVEN** a PR that changes the daemon `.csproj` so `dotnet publish` no
  longer produces `./publish/daemon/netclawd`
- **WHEN** the `validate-docker-build` job runs
- **THEN** `scripts/docker/build-image.sh` exits non-zero with a clear
  error identifying the missing binary
- **AND** the `docker build` step is not attempted

#### Scenario: Passing PR succeeds the health probe

- **GIVEN** a PR with no Dockerfile or build-script regressions
- **WHEN** the `validate-docker-build` job runs
- **THEN** `scripts/docker/build-image.sh` produces a tagged image
- **AND** starting the image with a minimal identity fixture and stub
  provider env vars causes `/api/health/ready` to return `"healthy"` within
  60 seconds
- **AND** the job succeeds without pushing to GHCR

### Requirement: Behavioral eval suite runs against ephemeral container

`evals/run-evals.sh` SHALL run each eval suite invocation against an
ephemeral `netclawd` container started from the published image. The
script SHALL NOT require, query, or modify the operator's running
development daemon. The container SHALL be named uniquely per run and
torn down on script exit (success, failure, or SIGINT). Eval state
(results DB, session logs, SQLite data) SHALL live inside a temporary
`$EVAL_HOME` directory that is removed on exit.

#### Scenario: Script spawns and tears down its own container

- **WHEN** `./evals/run-evals.sh` completes (pass or fail)
- **THEN** no `netclaw-eval-*` container remains running
- **AND** the temporary `$EVAL_HOME` directory is removed

#### Scenario: Host dev daemon state is untouched

- **GIVEN** the host's `~/.netclaw/netclaw.db` is snapshotted before the run
- **WHEN** a full eval suite completes
- **THEN** `~/.netclaw/netclaw.db` is byte-identical to the pre-run snapshot

### Requirement: Host networking for Tailscale and loopback resolution

`evals/run-evals.sh` SHALL start the eval container with `--network host`
by default. This inherits the host's DNS resolver so Tailscale MagicDNS
hostnames (e.g. `my-gpu-server.tail...ts.net`) resolve inside the container, and
it inherits the loopback namespace so `http://127.0.0.1:<port>`-style LLM
endpoints are reachable without port mapping. The script SHALL fail loudly
if `$NETCLAW_EVAL_PORT` is already bound on the host, rather than silently
falling through to a different port.

#### Scenario: Tailscale-only endpoint resolves inside container

- **GIVEN** the operator's LLM endpoint is `http://my-gpu-server.tailnet.ts.net:1234/v1`
- **AND** the host has a working Tailscale connection with MagicDNS enabled
- **WHEN** the eval script starts the container and routes prompts through
  the eval daemon
- **THEN** the daemon resolves the hostname and completes LLM calls
  successfully

#### Scenario: Port conflict fails fast

- **GIVEN** the host already has a process bound to `$NETCLAW_EVAL_PORT`
- **WHEN** the eval script starts the container
- **THEN** `docker run` or the subsequent `/api/health/ready` probe fails
- **AND** the script exits with a non-zero status and a clear message
- **AND** no partially-started container is left behind

### Requirement: Eval-target credentials are never silent

`evals/run-evals.sh` SHALL require explicit eval-target credentials on
every invocation. If all three of `NETCLAW_EVAL_PROVIDER_TYPE`,
`NETCLAW_EVAL_PROVIDER_ENDPOINT`, and `NETCLAW_EVAL_MODEL_ID` are set in
the environment, the script SHALL use them non-interactively. Otherwise,
the script SHALL prompt the operator interactively on stdin for the
missing values before starting the container. The script SHALL NOT fall
back to a hard-coded provider, endpoint, or model under any circumstances.

#### Scenario: All env vars set runs non-interactively

- **GIVEN** `NETCLAW_EVAL_PROVIDER_TYPE`, `_ENDPOINT`, and `_MODEL_ID` are
  exported in the caller's environment
- **WHEN** `./evals/run-evals.sh` starts
- **THEN** the script does not read from stdin
- **AND** the container starts with the provided values as env vars

#### Scenario: Missing env vars trigger interactive prompt

- **GIVEN** none of the three required env vars are set
- **AND** the script is attached to a terminal
- **WHEN** the script starts
- **THEN** it prompts for provider type, endpoint, and model id on stdin
- **AND** proceeds only after all three have been entered non-empty

#### Scenario: Missing env vars in non-interactive context fail loudly

- **GIVEN** none of the three required env vars are set
- **AND** stdin is not a terminal (e.g. the script runs under `ssh` with
  `-T` or inside a pipeline)
- **WHEN** the script starts
- **THEN** the script prints an error naming the missing env vars
- **AND** exits with a non-zero status before invoking `docker run`

### Requirement: Identity bind-mount is read-only

`evals/run-evals.sh` SHALL bind-mount the operator's identity files
(`~/.netclaw/identity/`) into the container at `/root/.netclaw/identity`
with read-only semantics (`:ro`). The script SHALL copy the identity files
to `$EVAL_HOME/identity` before mounting to decouple the container from
any in-place host mutation during the run.

#### Scenario: Eval run cannot modify host identity

- **GIVEN** the host's `~/.netclaw/identity/SOUL.md` is snapshotted
- **WHEN** a full eval suite completes
- **THEN** `~/.netclaw/identity/SOUL.md` is byte-identical to the snapshot

### Requirement: CLI endpoint override points at the eval daemon

During an eval run, `evals/run-evals.sh` SHALL export
`NETCLAW_DAEMON_ENDPOINT=http://127.0.0.1:${NETCLAW_EVAL_PORT}` for every
`netclaw -p` invocation, so the CLI client connects to the containerized
eval daemon rather than any daemon the operator may have running for
their real work. The script SHALL NOT modify any client config file on
the host to achieve this.

#### Scenario: Host CLI is unaffected after the run

- **GIVEN** the host's `~/.netclaw/client/config.json` is snapshotted
- **WHEN** a full eval suite completes
- **THEN** `~/.netclaw/client/config.json` is byte-identical to the
  snapshot
