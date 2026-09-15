# Tooling Inventory - Netclaw

## Runtime and Build

- `.NET SDK`: pinned by `global.json` (currently .NET 10 line)
- `dotnet` CLI: build, test, run, restore, local tool execution
- local tools configured in `.config/dotnet-tools.json`
- solution scaffold: `Netclaw.slnx` with `src/Akka.Agents` and
  `src/Netclaw.App`

## Planning and Spec Tooling

- `OpenSpec` CLI: installed and initialized in this repo
  - OpenCode command/skill files generated under `.opencode/`
  - repository artifacts under `openspec/`
- markdown docs under `docs/prd/`, `docs/spec/`, and `docs/ui/`
- RALPH loop infrastructure for iterative implementation
  - `ralph-opencode.sh`, `ralph.sh`
  - local Claude skills under `.claude/skills/`
  - flight recorder at `.ralph/runs/<run-id>/`

## Copyright Header Enforcement

| Command | Purpose |
|---------|---------|
| `scripts/Add-FileHeaders.ps1` | Add Petabridge copyright headers to all `.cs` files |
| `scripts/Add-FileHeaders.ps1 -Verify` | CI: check all files have headers (exit 1 if missing) |
| `scripts/Add-FileHeaders.ps1 -WhatIf` | Preview which files need headers |

## Focused Mutation Tests

The path-access, tool authorization, approval directory, reminder execution, and shell analysis mutation jobs run on each pull request, merge group, and `dev` push.
Each Linux job runs in parallel with the normal test matrix.

Focused mutation tests prove that deterministic tests reject a specific unsafe
change at a security or authority boundary. They do not measure general code
coverage. They do not replace positive and negative behavior tests.

### Current Targets

| Target | Protected claim | Expected mutants | Command |
|--------|-----------------|------------------|---------|
| `PathAccessPolicy.AddSessionRoots` | Only a Personal context receives shared session roots | 2 killed | `./scripts/run-path-access-mutations.sh` |
| `ToolAccessPolicy.AuthorizeMcpInvocation` | Server and tool audience grants precede approval | 2 killed | `./scripts/run-tool-authorization-mutations.sh` |
| `ToolAccessPolicy.AuthorizeShellInvocation` | A shell hard denial precedes approval | 1 killed | `./scripts/run-tool-authorization-mutations.sh` |
| Shell analysis, denial-only, tree effects, and reviewed-safe gates | Parser-proved regions and authored diagnostic syntax preserve hard denials; only bounded audited non-path values and consistent non-link-following tree facts can use reusable approval | 79 killed | `./scripts/run-shell-command-analysis-mutations.sh` |
| `ApprovalPatternMatching.EvaluateApprovalScope` | Folder grants require containment and reject link escape | 4 killed | `./scripts/run-approval-directory-mutations.sh` |
| `ReminderManagerActor.HandleExecutionOutcomeAsync` | Only the current attempt can settle; the manager replies after settlement | 2 killed | `./scripts/run-reminder-execution-mutations.sh` |
| `ActiveExecutionTracker.TryRemove` | Only the current owner can remove its guard; cleanup removes that guard | 2 killed | `./scripts/run-reminder-execution-mutations.sh` |

Run the path-access check locally:

```bash
./scripts/run-path-access-mutations.sh
```

The script tests two mutants in the shared session-root boundary.
The job fails unless both mutants die.
The local prototype took 1 minute 28 seconds after package restore.
A cold CI runner should take two to four minutes.

The harness uses xUnit 2 because Stryker's VSTest adapter does not support xUnit 3 correctly.
The script requires `perl` and `jq`, which the Linux CI image supplies.

### Tool Authorization Gate

Run the tool authorization gate:

```bash
./scripts/run-tool-authorization-mutations.sh
```

The script selects three conditions in `ToolAccessPolicy`.
Each mutant removes a logical negation.
The script requires one killed mutant at each selected source location and exactly three tested mutants overall.
The gate fails if a target is absent, survives, exceeds its time limit, or cannot compile.
Stryker can report unrelated compiler errors before it applies the source filter.
Those errors do not count as tested mutants.

The tests use the real dispatcher, MCP adapter, and shell policy coordinator.
Local probe tools count calls without an MCP connection or a host process.
Public and Team cases cover both MCP grant layers under Auto and one-time approval.
Personal cases cover shell hard denial under both modes.
Auto cases test forbidden calls before their permitted controls.
Approval cases first prove that the same approval keys permit the call.
Each denial must preserve the probe call count.

These tests preserve PRD-002 SEC-003 and PRD-006 MCP-003.
See [the ACL contract](openspec/specs/netclaw-acl/spec.md) and
[the approval contract](openspec/specs/tool-approval-gates/spec.md).
The gate covers authorization before dispatch. It does not prove MCP transport or native shell containment.

The final local run took 88 seconds after package restore.
The separate CI job retains a 10-minute timeout and uploads `tool-authorization-mutation-report`.
Its report directory is `artifacts/stryker/tool-authorization`.

### Approval Directory Gate

Run the approval directory gate:

```bash
./scripts/run-approval-directory-mutations.sh
```

The script reuses the xUnit 2 harness and selects `Netclaw.Security.csproj` as the mutation target.
It selects three source locations in `EvaluateApprovalScope`:

| Decision | Expected mutants |
|----------|------------------|
| Windows path containment | 1 killed: remove the logical negation |
| POSIX path containment | 1 killed: remove the logical negation |
| POSIX link rejection | 2 killed: force either conditional outcome |

The script requires these counts at their exact source locations and four tested mutants overall.
It fails if a target is absent, survives, exceeds its time limit, or cannot compile.
The source selector rejects an absent or duplicate boundary before Stryker starts.
This protects the gate when the authorization code and diagnostic code contain similar conditions.

Fifteen cases exercise the public typed approval matcher with real directories and links.
They cover the grant root, normal descendants, sibling prefixes, traversal, relative paths, and candidate scope that differs from cwd.
The link cases prove that the link reaches the sibling directory before they require denial.
Windows path cases cover case rules, drive boundaries, and traversal on every host.
The native filesystem cases select Bash on POSIX hosts and PowerShell on Windows.
The Linux mutation job does not mutate the Windows link branch; the ordinary Windows test job exercises that branch.

The matcher shares `EvaluateApprovalScope` with `ToolApprovalActor` and shell approval evidence validation.
These tests preserve PRD-002 SEC-003 and
[the directory-root approval contract](openspec/specs/tool-approval-gates/spec.md#requirement-directory-root-approvals-for-shell_execute).
They prove folder-grant decisions. They do not prove native process containment or races between authorization and file access.

The final local run took 41 seconds after package restore.
The separate CI job retains a 10-minute timeout and uploads `approval-directory-mutation-report`.
Its report directory is `artifacts/stryker/approval-directory`.

### Reminder Execution Gate

Run the reminder execution gate:

```bash
./scripts/run-reminder-execution-mutations.sh
```

The script reuses the xUnit 2 harness and selects four mutations:

| Boundary | Expected mutant |
|----------|-----------------|
| Manager outcome ownership | Reverse the execution-ID comparison |
| Manager acceptance reply | Remove the reply from `finally` |
| Tracker removal ownership | Reverse the execution-ID comparison |
| Tracker guard cleanup | Remove the dictionary removal |

Each location must produce one killed mutant. The report must contain exactly four tested mutants.
The gate fails if a target is absent, ignored, survives, exceeds its time limit, or cannot compile.
The selector rejects absent or duplicate source markers before Stryker starts.
Unrelated compiler errors do not count as tested mutants.

Seven cases use the real manager, execution tracker, actor mailbox, definition store, and history store.
An isolated host supplies the local Akka.Reminders scheduler with an in-memory store.
The fixture observes execution IDs through the existing dispatch event and holds the session pipeline on a task.
Actor replies provide barriers. The tests use no delay or sleep to wait for state.

The stale-message cases complete attempt A, start attempt B, and replay A's completion or termination.
They require B's guard, history, failure count, and alerts to remain unchanged.
A current-termination control must record the failure and release its guard.
Success and failure cases require the correct acceptance ID, history, failure count, and subsequent dispatch.
A directory at the atomic-write path causes a real save failure; cleanup and the acceptance reply must still occur.

These tests preserve PRD-008 SCHED-005 and SCHED-007.
See [the execution and settlement contract](openspec/specs/netclaw-scheduling/spec.md) and
[the history contract](openspec/specs/reminder-execution-history/spec.md).
The fixture injects internal outcome messages and synthetic envelopes.
It does not prove the child outcome producer, durable scheduler settlement, restart recovery, or external delivery.

Stryker 5.0.0 omits statement removal when the statement contains an `out` keyword.
It also filters the complete `finally` deletion when the acceptance-reply mutant exists.
Thus, the cleanup mutant targets the tracker dictionary, not the manager's `TryRemove` call.
An explicit local deletion of that call must also make the cleanup cases fail before this gate changes.

The final local run took 95 seconds after package restore.
The separate CI job retains a 10-minute timeout and uploads `reminder-execution-mutation-report`.
Its report directory is `artifacts/stryker/reminder-execution`.

### Shell Analysis Gate

Run the shell analysis gate:

```bash
./scripts/run-shell-command-analysis-mutations.sh
```

The script tests 79 mutants across execution-region accounting, denial-only
matching, tree traversal and root correspondence, bounded non-filesystem
values, candidate extraction, approval mode, path facts, and reviewed-safe
policy. The job fails unless every mutant dies.

The final local run took about 6 minutes. CI allows 30 minutes for
hosted-runner variance and report upload. The report directory is
`artifacts/stryker/shell-command-analysis`.

### Scope Review

Review the target list after each security fix or authority policy change.
Also review it as part of each minor release.

Add one focused target when all these conditions apply:

- The code controls authorization, isolation, privacy, identity, destructive access, or execution ownership and cleanup.
- A plausible mutation represents a specific unsafe behavior.
- Deterministic tests reject that mutation.
- A narrow source span contains the relevant decision.
- Stryker produces stable, meaningful mutants for that span.
- The total mutation job stays below its configured CI timeout.

Use this procedure:

1. State the protected claim and the unsafe mutation.
2. Apply the mutation in a disposable worktree.
3. Confirm that the applicable tests fail for the expected reason.
4. Configure Stryker for the smallest source span that contains the decision.
5. Pin the expected mutant count and require each mutant to die.
6. Record the target, claim, count, command, and measured cost in this section.
7. Split the target into a parallel job if the total job approaches its timeout.

Do not add a broad project scan. Broad scans can produce equivalent mutants,
long runs, and invalid results from the current xUnit 3 adapter path.

Review these candidate boundaries before lower-risk code:

1. Additional native-tool and structured-path decisions in `ToolAccessPolicy`.
2. Additional approval scope decisions, including the native Windows link branch.
3. Shell hard-deny decisions in `ShellCommandPolicy`.
4. Slack, Discord, and Mattermost ACL decisions.
5. Device bearer token authentication.

## Interactive CLI Smoke Tests (Tape Harness)

The native smoke harness exercises the interactive Termina TUI surface
that the non-interactive scenarios cannot reach (Spectre-style prompts,
wizard flows, model/provider/webhook TUIs). It drives the **real native
binary** — no Docker. Tape bodies live at `tests/smoke/tapes/<name>.tape`;
sibling assertion scripts at `tests/smoke/assertions/<name>.sh` validate
the artefacts each tape produced. The same `run-smoke.sh` entrypoint runs
in CI (`smoke.yml`) and locally — agents working on
TUI code SHOULD run the harness before declaring a change done.

| Command | Purpose |
|---------|---------|
| `./scripts/smoke/run-smoke.sh light` | PR-gating subset: all flow tapes + non-interactive scenarios |
| `./scripts/smoke/run-smoke.sh full` | Full suite (placeholder: identical to light until backfilled) |
| `./scripts/smoke/run-smoke.sh <name>` | Single tape or scenario, e.g. `init-wizard` (fastest inner loop) |
| `./scripts/smoke/run-smoke.sh skill-sync` | Live daemon and RFC feed proof for immediate skill updates |
| `./scripts/smoke/run-smoke.sh screenshots` | Screenshot regression: capture + byte-compare against baselines |
| `./scripts/smoke/install-vhs.sh` | Idempotent VHS install (Linux/x86_64 + macOS via Homebrew) |

`run-smoke.sh` publishes the binary (or uses `NETCLAW_SMOKE_CLI` /
`NETCLAW_SMOKE_DAEMON` if exported), installs `vhs`, starts a native
`ollama serve`, and pulls the smoke models automatically.

Config-writing flow tapes (`init-wizard`, `provider-add`, `provider-rename`,
and `config-*`) must have executable semantic assertion scripts under
`tests/smoke/assertions/`. `run-native-tape.sh` fails these tapes when the
assertion is missing or non-executable.

The `skill-sync` scenario starts a mutable local RFC feed and the published
daemon. It runs `netclaw skill sync` twice and changes the feed between passes.
It checks the exact resource SHA-256, the live `/api/skills` inventory, and an
unchanged `netclaw.json` file. The scenario does not call a model.

When a tape fails, `smoke-logs/tapes/<name>/` collects: a debug GIF of the
last frame, the combined tape file, daemon logs, and the produced
`NETCLAW_HOME`. CI uploads the `smoke-logs` directory as a job artefact.

**Rerun this workflow in full, never with `--failed`.** The binary artefact
name ends with the attempt number
(`netclaw-native-binaries-linux-x64-<run-id>-1`). A partial rerun becomes
attempt 2 and looks for `...-2`, which the publish job never produced, so
`Download binary artifact` fails before a tape runs. Use
`gh run rerun <run-id>`, not `gh run rerun --failed <run-id>`.

The job installs Ollama from its release-pinned installer before it runs a
tape. A `curl: (35) Recv failure` at that step is a network failure that
reaches no test code. Confirm the failure is inside a tape before you
investigate the change under review.

**Authoring conventions are in `tests/smoke/tapes/README.md`**
— the short version: `Wait+Screen /pattern/` only (no `Sleep`), 1400×800
default surface, no `Screenshot` directives in flow tapes, pair every
non-trivial tape with an assertion script that re-validates `netclaw
doctor` and the
relevant `--json` output.

## Demo AppHost Smoke Test (Slow)

`samples/Netclaw.Demo.AppHost.IntegrationTests` is an Aspire-driven
end-to-end test that boots the demo AppHost (`samples/Netclaw.Demo.AppHost`),
waits for every resource — Mattermost container, Ollama container,
`qwen3.5:2b-q4_K_M` model, NetClaw daemon project — to reach healthy, posts a
Mattermost message via REST as the seeded test user, and asserts the
wiring routes the message through.

Opt-in by design: the test self-skips unless
`NETCLAW_RUN_DEMO_SMOKE=1` is set (same pattern as the Mattermost
integration tests). The `[Trait("Category", "SlowSmoke")]` trait is a
secondary filter for local-dev runs. A bare `dotnet test` on any CI
runner therefore reports the test as skipped, not failed. Invoke with:

```bash
NETCLAW_RUN_DEMO_SMOKE=1 \
  dotnet test samples/Netclaw.Demo.AppHost.IntegrationTests \
    --filter Category=SlowSmoke
```

Prerequisites: Docker daemon reachable, ~4GB of disk free on a cold
cache (Mattermost preview + Ollama image + `qwen3.5:2b-q4_K_M` weights). Warm
runs reuse cached images and the model volume.

The test's bot-reply wait is best-effort and configurable. On a
CPU-only host inference takes minutes; on GPU it's <30s. Override the
default 5-minute reply window:

```bash
NETCLAW_RUN_DEMO_SMOKE=1 \
NETCLAW_DEMO_TEST_REPLY_TIMEOUT_SECONDS=900 \
  dotnet test samples/Netclaw.Demo.AppHost.IntegrationTests --filter Category=SlowSmoke
```

If the timeout elapses without a reply, the test still passes — the
structural assertions (every resource healthy, message posted into
Mattermost) prove the wiring. The latency is printed to stdout so a CI
run can flag a slow-inference regression.

## Install Script Smoke Test

`scripts/smoke/install-smoke.sh` and `scripts/smoke/install-smoke.ps1` are
hermetic regression tests for the installers (`scripts/install.sh` and
`scripts/install.ps1`). They need no network, no `dotnet` build, and no
running daemon — each serves a generated manifest and stand-in archives
from `localhost`.

| Command | Purpose |
|---------|---------|
| `bash scripts/smoke/install-smoke.sh` | Smoke-test the `curl \| bash` installer (Linux/macOS) |
| `pwsh scripts/smoke/install-smoke.ps1` | Smoke-test the PowerShell installer (Windows) |

`install-smoke.sh` covers two layers:

- **Detection matrix** — runs `install.sh --dry-run` under `uname`/`sysctl`
  shims to assert every supported OS/arch resolves to the right RID
  (`linux-x64`, `linux-arm64`, `osx-arm64`) and that Intel Macs and
  unsupported OSes are rejected cleanly. This runs identically on any host.
- **Mechanical check** — one real install of a stand-in archive on the
  host's native RID, exercising download → checksum → `tar` extract → `cp`.

`install-smoke.ps1` is the Windows counterpart: a `-DryRun` resolution
check plus a real stand-in install exercising download → checksum →
`Expand-Archive` → copy.

The `install-smoke` job in `pr_validation.yml` runs these on
`ubuntu-latest`, `macos-latest`, and `windows-latest` on every PR. Both
installers also support `--dry-run` / `-DryRun` on their own — they report
which binary *would* be installed for the current platform without
touching the system.

## Logging Conventions

- Carry cross-cutting identity (channel/adapter, session, entity ids) as
  **structured context via `ILoggingAdapter.WithContext(...)`**, set once where
  the logger is created — not baked into each message template. Actors already
  do this (e.g. `Context.GetLogger().WithContext("Adapter", "slack")`).
- Do **not** prefix the channel/adapter into the message text
  (`slack_attachment_rejected …`). Emit a plain semantic event
  (`attachment_rejected …`); the `Adapter` context disambiguates the source.
  Prefixing double-stamps what the context already carries and forces shared
  code to take a channel-name parameter purely for logging.
- Shared/abstracted helpers that take an `ILoggingAdapter` should rely on the
  caller's enriched context rather than re-passing identity strings.
- For `Microsoft.Extensions.Logging` (`ILogger<T>`) call sites, the logger
  **category** already identifies the type; prefer that (or `BeginScope`) over
  repeating the class/channel name in the message.

See `getakka.net` → Utilities → Logging → "Context enrichment and scopes".

## Source Control and CI Signals

- `git` repository with active `dev` branch
- GitHub Actions workflows in `.github/workflows/`
- Azure pipeline templates in `.azure/`

## External Integrations (Planned for MVP)

- Slack Socket Mode
  - requires bot token and app token
  - no public inbound HTTP required for base interaction
- SQLite for Akka.Persistence journal and snapshots (in-memory for tests)
- MCP servers for external tool integration (MVP requirement)
- local Ollama endpoint can be used for optional smoke tests
  - local dev host: `my-gpu-server` on Tailscale (`http://my-gpu-server:11434`)
  - preferred model: `qwen3:30b` (fallback `qwen3:14b`)

## Security-Relevant Surfaces

- Slack inbound message events (untrusted input)
- tool execution surfaces (web, file read/write, shell)
- ACL configuration and policy evaluation
- system prompt and policy files loaded from disk

## Operator Interfaces (Planned)

- CLI for onboarding/config validation, policy diagnostics, and session
  operations
- management UI (ops console) for health, session inspection, ACL editing, and
  diagnostics

## Working Assumptions

- single-process architecture during MVP
- operator-controlled host and credentials
- default-deny policy with explicit per-channel and per-sender allow rules
- required CI tests do not depend on live model providers
- `my-gpu-server` Ollama access is local-dev only and not available in CI/CD
