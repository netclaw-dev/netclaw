# Native Interactive Smoke Tapes

This directory holds VHS tape scripts that drive netclaw's interactive
CLI surface (Spectre prompts, wizard flows, TUI menus) against the
**real native binary** — no Docker. They exist to catch regressions
like the netclaw/knit interactive-wizard bug that the non-interactive
smoke scenarios (`tests/smoke/scenarios/*.sh`) cannot reach.

These tapes are **end-to-end test scenarios**, not screenshot fodder.
The pretty-screenshots tapes live in
`netclaw-website/screenshots/tapes/` and are completely separate.

This is the native successor to `tests/smoke-interactive/tapes/`, which
runs everything inside a Docker container. The two systems run in
parallel during a bake-in period; the Docker one is retired in a later
phase.

## Running locally

```bash
# Light suite (all tapes + non-interactive scenarios).
./scripts/smoke/run-smoke.sh light

# Full suite.
./scripts/smoke/run-smoke.sh full

# Single tape, fast iteration.
./scripts/smoke/run-smoke.sh init-wizard
```

`run-smoke.sh` publishes the binary (or uses `NETCLAW_SMOKE_CLI` /
`NETCLAW_SMOKE_DAEMON` if exported), installs `vhs` if missing, starts a
native `ollama serve`, and pulls the smoke models automatically.

## Authoring conventions

Tapes here MUST follow these rules. A reviewer should reject anything
that breaks them.

1. **No literal `Sleep`** for synchronization. Use `Wait+Screen
   /pattern/` to wait on stable substrings from the wizard's view
   source (`src/Netclaw.Cli/Tui/Wizard/Steps/*StepView.cs`).
   Sleep-based synchronization is the single biggest source of CI
   flakiness. The only acceptable timeout is the outer one inside
   `run-native-tape.sh`. (The short `Sleep 300ms` Termina-relayout
   guards in the provider tapes are a known, documented exception.)

2. **Tapes do not reset state.** The wrapper sets a per-tape
   `NETCLAW_HOME` and clears it before the tape runs. Tape bodies do
   not `rm -rf`, do not depend on prior tape runs.

3. **Tapes do not produce screenshots.** They MAY emit a debug GIF
   (`Output /tmp/tape-<name>.gif`) so failures have a visual artifact —
   but no `Screenshot` directives in the body.

4. **Terminal sizing is in pixels, not columns.** VHS interprets
   `Set Width` / `Set Height` as pixel dimensions (minimum 120x120).
   The preamble sets a sensible default (1400x800 at FontSize 14, which
   gives ~95 cols × 50 rows).

5. **Anchor regexes at every step.** After every `Type` / `Enter` /
   `Down` / etc. that changes the visible state, immediately
   `Wait+Screen /…/` for an anchor in the next view.

6. **Pair each non-trivial tape with an assertion.** Place a sibling
   script at `tests/smoke/assertions/<tape-name>.sh`. The wrapper
   invokes it with `NETCLAW_HOME` and `NETCLAW_SMOKE_CLI` exported.

## Anatomy

Each tape body is concatenated *after* `preamble.tape`, which sets up a
plain bash session on the host with a fresh `NETCLAW_HOME` and a
deterministic prompt (`TAPE$ `). The combined tape is what `vhs` sees;
the prompt anchor `/TAPE\$/` is the safe "back at shell" wait.

The substitution placeholders the wrapper fills in:

| Placeholder            | Replaced with                                  |
|------------------------|------------------------------------------------|
| `__NETCLAW_HOME__`     | per-tape `NETCLAW_HOME` directory (host FS)    |
| `__NETCLAW_BIN_DIR__`  | directory containing `netclaw` + `netclawd`    |
| `__TAPE_NAME__`        | tape short name                                |

VHS v0.11 does not support escaped quotes inside `Type "..."`, so the
binary directory and home paths are burned into the preamble at
substitution time rather than typed as quoted literals.

## Adding a new tape

1. Identify the interactive flow you're covering.
2. Read the relevant `*StepView.cs` (or other TUI source) to harvest
   stable strings for `Wait+Screen` anchors.
3. Author the tape body following the rules above.
4. Author a sibling assertion script at
   `tests/smoke/assertions/<name>.sh`.
5. Add the tape's short name to `LIGHT_TAPES` / `FULL_TAPES` in
   `scripts/smoke/run-smoke.sh`.
6. Run `./scripts/smoke/run-smoke.sh <name>` until it's stable across
   at least 3 consecutive runs. Treat any flake as a bug.
