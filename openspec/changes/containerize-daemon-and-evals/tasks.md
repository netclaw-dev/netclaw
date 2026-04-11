## 1. NetclawPaths env var support (prerequisite for CLI isolation)

- [x] 1.1 Edit `src/Netclaw.Configuration/NetclawPaths.cs:86-92` to read `NETCLAW_HOME` as a fallback in the constructor, ordered after explicit `basePath` and before the `UserProfile` default
- [x] 1.2 Add unit tests in `src/Netclaw.Configuration.Tests/` covering the three precedence cases: explicit arg wins over env var, env var wins over default, unset env var falls back to default
- [x] 1.3 Run `dotnet test src/Netclaw.Configuration.Tests src/Netclaw.Cli.Tests src/Netclaw.Daemon.Tests` to confirm no existing caller regresses

## 2. Release-grade Dockerfile

- [x] 2.1 Create `docker/Dockerfile` based on `ubuntu:24.04`
- [x] 2.2 Install runtime dependencies via `apt-get install --no-install-recommends`: `ca-certificates curl wget procps git jq sqlite3 python3 python3-pip python3-venv gh`
- [x] 2.3 `COPY publish/cli/netclaw` and `publish/daemon/netclawd` into `/opt/netclaw/{cli,daemon}/`, then symlink both into `/usr/local/bin/`
- [x] 2.4 Declare `VOLUME /root/.netclaw`, `EXPOSE 5199`, and `ENV NETCLAW_DAEMON_PATH=/opt/netclaw/daemon/netclawd`
- [x] 2.5 Add OCI image labels (`org.opencontainers.image.source`, `.title`, `.description`, `.version`, `.licenses`) using `ARG NETCLAW_VERSION`
- [x] 2.6 Set `ENTRYPOINT ["/usr/local/bin/netclawd"]`
- [x] 2.7 Add a brief comment at the top of the file cross-referencing `docker/smoke/Dockerfile` so contributors know this is the release image and smoke is the test fixture

## 3. Local build script

- [x] 3.1 Create `scripts/docker/build-image.sh` with positional version arg (default `dev`), `IMAGE_REPO` env default `ghcr.io/aaronontheweb/netclawd`, and `NO_BUILD` escape hatch
- [x] 3.2 Script invokes `dotnet publish` for CLI and Daemon (self-contained, single-file, linux-x64) into `./publish/{cli,daemon}` unless `NO_BUILD=1`
- [x] 3.3 Script asserts `./publish/cli/netclaw` and `./publish/daemon/netclawd` exist before `docker build`, exits non-zero with an error pointing at the missing path if not
- [x] 3.4 Script invokes `docker build -f docker/Dockerfile -t "$IMAGE_REPO:$VERSION" --build-arg NETCLAW_VERSION=$VERSION .`
- [x] 3.5 Mark executable (`chmod +x`), commit with the Dockerfile
- [x] 3.6 Test locally: `scripts/docker/build-image.sh dev` succeeds on a clean tree, `docker images` shows the new tag

## 4. Dedicated Docker validation workflow (validate_docker_image.yml)

- [x] 4.1 Create standalone `.github/workflows/validate_docker_image.yml` with `validate-docker-build` job, `runs-on: arc-netclaw`, `timeout-minutes: 20`. Separate from `pr_validation.yml` (tests + slopwatch) because image construction is its own concern with its own failure mode.
- [x] 4.2 Trigger on `push` + `pull_request` to dev/main/master, scoped to `paths:` that actually affect the image (Dockerfile, build script, src/**, global.json, Directory.Build.props, the workflow itself). Saves runner minutes on unrelated PRs.
- [x] 4.3 Job checks out the repo, installs .NET SDK from `global.json`, sets up `docker/setup-buildx-action@v3`
- [x] 4.4 Job runs `scripts/docker/build-image.sh` with an ephemeral `pr-NNN` / `ci-RUNID` tag
- [~] 4.5 ~~Add "verify fail-fast on empty config" step~~ — deferred, not enforced by current daemon (follow-up issue)
- [x] 4.6 Add "verify happy path" step that starts the container with `-p 5399:5199` + ollama provider env vars (no API key needed), polls `GET http://127.0.0.1:5399/api/health/ready` for 60s, fails on timeout
- [x] 4.7 Ensure both steps `docker stop` the container on success and on failure; no leaked containers between PR runs on the self-hosted runner
- [ ] 4.8 Test by intentionally breaking `docker/Dockerfile` in a scratch branch and confirming the workflow fails at the build step

## 5. Release workflow: publish-docker

- [x] 5.1 Add `publish-docker` job to `.github/workflows/publish_release_binaries.yml` after `publish-binaries`
- [x] 5.2 Job needs `contents: read` and `packages: write` permissions; `runs-on: arc-netclaw`, `timeout-minutes: 30`
- [x] 5.3 Checkout + install .NET SDK + setup Buildx + `docker/login-action@v3` to `ghcr.io` using `secrets.GITHUB_TOKEN`
- [x] 5.4 Run `scripts/docker/build-image.sh "${{ github.ref_name }}"` with `IMAGE_REPO=ghcr.io/aaronontheweb/netclawd`
- [x] 5.5 Add follow-up `docker tag` step that adds `:latest` and `:${major}.${minor}` aliases (strip leading `v` for semver math)
- [x] 5.6 `docker push --all-tags ghcr.io/aaronontheweb/netclawd`
- [x] 5.7 Confirm the existing `publish-binary-manifest` and `publish-skills` jobs still chain correctly (they depend on `publish-binaries`, not on `publish-docker`)
- [x] 5.8 Note in the PR description that the first GHCR push creates a private package; owner must flip visibility to public once

## 6. Rewrite evals/run-evals.sh bootstrap and lifecycle

- [x] 6.1 Replace configuration block with the new env-var surface: `NETCLAW_IMAGE`, `NETCLAW_EVAL_PORT`, `NETCLAW_EVAL_PROVIDER_TYPE`, `NETCLAW_EVAL_PROVIDER_ENDPOINT`, `NETCLAW_EVAL_MODEL_ID`, `NETCLAW_EVAL_FALLBACK_MODEL_ID`, `NETCLAW_EVAL_COMPACTION_MODEL_ID`, `NETCLAW_EVAL_CONTEXT_WINDOW`, plus `EVAL_HOME=$(mktemp -d …)`
- [x] 6.2 Rewrite `check_prerequisites` to verify `docker` is available, verify `~/.netclaw/identity/SOUL.md` exists, warn on missing `sqlite3`, and set up the `cleanup_eval_env` EXIT trap
- [x] 6.3 Add credential prompt fallback: if any of the three required eval env vars is unset AND stdin is a terminal, prompt via `read -p`; if unset AND stdin is not a terminal, print missing vars and exit non-zero
- [x] 6.4 Implement `start_eval_daemon` that copies `~/.netclaw/identity` → `$EVAL_HOME/identity`, bind-mounts identity + logs, runs `docker run --network host`, polls `/api/health/ready` with a 60s budget. (Note: identity is mounted writable, not `:ro`, because the daemon writes shadow index files under identity/tooling/shadow/ at startup. Isolation is preserved because the bind mount targets a throwaway copy in `$EVAL_HOME/identity`, not the operator's real `~/.netclaw/identity`.)
- [x] 6.5 If health check fails, capture daemon logs (both `docker logs` and the file log), stop the container, and exit non-zero with a clear message
- [x] 6.6 Rewrite `check_daemon_alive` to inspect `{{.State.Running}}` on the eval container instead of calling `netclaw daemon status`
- [x] 6.7 Rewrite `run_prompt` to: (a) snapshot `$DAEMON_LOG` line count as `DAEMON_LOG_LINES_BEFORE`; (b) export `NETCLAW_DAEMON_ENDPOINT=http://127.0.0.1:$EVAL_PORT` and `NETCLAW_HOME=$EVAL_HOME` for the `netclaw -p` invocation; (c) capture stdout to `$STDOUT_FILE`. Daemon log comes from the bind-mounted file, not `docker logs`, because the daemon writes to a file logger (not stdout).
- [x] 6.8 `daemon_log_tail` unchanged from pre-container version — it already reads from `$DAEMON_LOG` via `tail -n +$((DAEMON_LOG_LINES_BEFORE+1))`. Only `$DAEMON_LOG` now points at the bind-mounted file under `$EVAL_HOME/logs/`.
- [x] 6.9 Rewrite `main` to call `check_prerequisites`, `start_eval_daemon`, `init_db`, then delegate to the existing `run_all` and `finalize_db` flow; update the banner to show `$NETCLAW_IMAGE`, eval endpoint, provider/model, and `$EVAL_HOME`
- [~] 6.10 Bootstrap smoke-tested end-to-end (`check_prerequisites` + `start_eval_daemon` + `cleanup_eval_env` against a real container using ollama provider type). Full eval suite run against a real LLM deferred to PR review verification (requires ~20 min + a working LLM endpoint, better to verify on the reviewer's machine).
- [x] 6.11 Bug: daemon writes files as root inside bind-mount, host user can't `rm -rf` → fixed with `force_rmrf` helper that falls back to a throwaway root container for cleanup.

## 7. Verify isolation

- [x] 7.1 Design-level isolation verified: eval script copies identity to `$EVAL_HOME/identity`, bind-mounts only the throwaway directory, exports `NETCLAW_HOME=$EVAL_HOME` and `NETCLAW_DAEMON_ENDPOINT=http://127.0.0.1:$EVAL_PORT` for CLI calls. No code path reads or writes `~/.netclaw/netclaw.db`, `~/.netclaw/client/config.json`, or `~/.netclaw/secrets.json` during an eval run.
- [x] 7.2 Bootstrap smoke test confirms container start + health + file log capture + teardown works end-to-end on a fixture identity (see task 6.10).
- [x] 7.3 `force_rmrf` helper handles cleanup of root-owned files written by the container (verified by successful re-run of the smoke test against a UID-mismatched bind mount).
- [x] 7.4 Container name is `netclaw-eval-$$` (PID-scoped) and tagged `--rm`, so containers auto-remove on daemon exit even if the EXIT trap misses them.
- [ ] 7.5 Full end-to-end isolation verification (host state pre/post md5sum) deferred to PR review — requires a real LLM endpoint to exercise the full case suite.
- [ ] 7.6 Three-consecutive-runs stability check against memory-recall degradation from #569 — deferred to PR review for the same reason.

## 8. Documentation

- [x] 8.1 Update `evals/README.md` Quick Start to show the new `NETCLAW_EVAL_PROVIDER_TYPE=…` env var invocation
- [x] 8.2 Update the Environment Variables table to add all new eval-target vars (`NETCLAW_EVAL_PROVIDER_*`, `NETCLAW_EVAL_MODEL_*`, `NETCLAW_EVAL_CONTEXT_WINDOW`, `NETCLAW_EVAL_PORT`, `NETCLAW_IMAGE`) and remove the old `NETCLAW_HOME` / `NETCLAW_EVAL_DAEMON_LOG` entries
- [x] 8.3 Replace the Limitations (v1) section with v2 limitations: local LLM required, single-turn only, identity borrowed from host
- [x] 8.4 Add a short "How it works" subsection explaining the ephemeral container + identity bind-mount + env-var config flow
- [x] 8.5 Note that `--network host` is the default and that macOS/Windows operators need a different endpoint resolution strategy

## 9. Quality gates

- [x] 9.1 Run `dotnet slopwatch analyze` after all code changes land; address any new violations or baseline them with justification
- [x] 9.2 Run `dotnet test` for `Netclaw.Configuration.Tests`, `Netclaw.Cli.Tests`, `Netclaw.Daemon.Tests` and confirm green
- [ ] 9.3 Open the PR on a feature branch; confirm `pr_validation.yml` runs the `validate-docker-build` job and the job succeeds
- [ ] 9.4 After merge, push a release tag on a follow-up PR to confirm `publish-docker` runs end-to-end and lands the image at `ghcr.io/aaronontheweb/netclawd`

## 10. OpenSpec follow-up

- [ ] 10.1 After the PR merges, run `/opsx-verify` on `containerize-daemon-and-evals` to confirm the implementation matches the spec deltas
- [ ] 10.2 Run `/opsx-sync` to copy the new `daemon-container` spec to `openspec/specs/daemon-container/spec.md` and apply the `netclaw-cli` delta
- [ ] 10.3 Run `/opsx-archive` to move the change under `openspec/changes/archive/`
- [ ] 10.4 File the deferred follow-up issues listed in the plan: compaction eval cases, checkpoint-drain wait (#437 already open), CI fixture identity, fail-loudly-on-empty-identity, `EnvOverrideDoctorCheck`
- [x] 10.5 File follow-up issue for Docker Hub publishing alongside GHCR → filed as #602
