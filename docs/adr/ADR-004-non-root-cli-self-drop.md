# ADR-004: Non-Root CLI Self-Drop in the Container

**Date:** 2026-06-04
**Status:** Accepted
**Context:** Container identity model — the daemon runs non-root, but operators drive the CLI via `docker exec` / `kubectl exec` (which default to root)

## Decision

`/usr/local/bin/netclaw` in the official image is a **self-dropping launcher**
(`docker/netclaw-cli-launcher.sh`), not a bare symlink to the CLI binary. When
invoked as root (uid 0) it re-execs itself as the unprivileged `netclaw` user via
`gosu`; when already running as `netclaw` it execs the real binary directly:

```sh
if [ "$(id -u)" = 0 ]; then
    export HOME=/home/netclaw
    exec gosu netclaw /opt/netclaw/cli/netclaw "$@"
fi
exec /opt/netclaw/cli/netclaw "$@"
```

`netclawd` stays a plain symlink: it is only ever launched by the supervised
`entrypoint.sh`, which has already dropped to `netclaw`, so it never hits the
root path. The behaviour is locked in by `scripts/docker/test-nonroot-cli.sh`
(wired into `validate_docker_image.yml`).

## Context

The image runs the daemon as a dedicated non-root user (`netclaw`, uid 1654) for
defense-in-depth — see the entrypoint's root-then-`gosu`-drop sequence. The image
`USER` stays `root` only so the entrypoint can repair bind-mount ownership before
dropping.

A consequence: `docker exec <container> netclaw <cmd>` and
`kubectl exec ... -- netclaw <cmd>` default to the image `USER`, i.e. **root**.
And `netclaw init` — the documented first-run setup — is invoked exactly that way.
Running the CLI as root in this image breaks two things:

1. **Single-file bundle extraction (EACCES).** The CLI is a .NET single-file
   binary. On startup the apphost extracts its bundled files into a per-`$HOME`
   directory (`$HOME/.net/netclaw/<hash>/`) and, by .NET's design, locks that
   directory to the invoking user at mode `700` so no other user can tamper with
   the extracted executables. Extracted by root, the directory is `root:root`;
   the non-root daemon (and its `shell_execute` children, which run as `netclaw`)
   can then no longer extract the CLI and fail with:

   ```
   Failure processing application bundle.
   Failed to create directory [/home/netclaw/.net/netclaw/<hash>] ... Error code: 13
   ```

   (`EACCES`, process exit 160). I.e. one root-context CLI invocation
   permanently breaks the agent's own ability to call its CLI until ownership is
   repaired or the (ephemeral) dir is recreated.

2. **Root-owned config/state.** `netclaw init` and other config-writing commands
   persist identity, config, and secrets under `NETCLAW_HOME`
   (`/home/netclaw/.netclaw`). Written as root, the non-root daemon cannot read
   its own configuration.

This is not hypothetical: it surfaced in production when an operator (and
automated tooling) ran `netclaw` commands via `kubectl exec` to inspect and set
model assignments, after which the agents could no longer execute their own CLI.

## Alternatives considered

- **Document "always `docker exec -u netclaw`".** Pushes a non-obvious
  requirement onto every operator and every orchestrator; one forgetful
  `netclaw init` re-breaks the agent. Rejected as the *primary* mechanism (it
  remains valid and now composes — see below).
- **`USER netclaw` in the image.** Would make `docker exec` default to `netclaw`,
  but the image starts as root deliberately so the entrypoint can repair
  ownership of operator bind mounts (`docker run -v /host/dir:/home/netclaw/.netclaw`).
  Dropping root-at-start would regress that feature for Docker users.
- **Per-deployment `runAsUser: 1654` (Kubernetes).** Fixes K8s exec, but does
  nothing for `docker run`/`docker exec` users — the majority of the install
  base. A project-level defect needs a project-level fix in the image. (It still
  composes: with `runAsUser` set, the launcher's `id -u` check is simply false.)
- **Set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a fixed path.** Both uids still
  collide on the same locked subdirectory, and it does nothing for the root-owned
  config problem. Insufficient.

## Consequences

- `docker exec -- netclaw <cmd>` and `kubectl exec -- netclaw <cmd>` "just work"
  for operators regardless of orchestrator, with no `gosu`/`-u` knowledge needed.
  `netclaw init` writes netclaw-owned config; the CLI bundle extracts
  netclaw-owned.
- A one-line breadcrumb is printed to **stderr** on drop (stdout stays clean for
  scripted callers parsing `netclaw --version` etc.).
- Composes with `runAsUser`/`-u netclaw`: when the caller is already `netclaw`,
  the launcher execs directly (no second drop).
- The CLI never needs root in this image, so auto-dropping has no functional
  downside. If a future tool genuinely required root, it would need an explicit
  escape hatch — none exists today by design.
