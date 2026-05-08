# Interactive Smoke Tapes

This directory holds VHS tape scripts that drive netclaw's interactive
CLI surface (Spectre prompts, wizard flows, TUI menus) inside the smoke
compose stack. They exist to catch regressions like the netclaw/knit
interactive-wizard bug that the non-interactive smoke (`scripts/smoke/check.sh`)
cannot reach.

These tapes are **end-to-end test scenarios**, not screenshot fodder.
The pretty-screenshots tapes live in
`netclaw-website/screenshots/tapes/` and are completely separate.

## Running locally

```bash
# Light suite (PR-gating subset).
./scripts/smoke/run-tapes.sh light

# Full suite (nightly).
./scripts/smoke/run-tapes.sh full

# Single tape, fast iteration.
./scripts/smoke/run-tapes.sh init-wizard --keep-stack

# Reuse a stack you already have running:
./scripts/smoke/run-tapes.sh init-wizard --no-up --keep-stack
```

`run-tapes.sh` will install `vhs` if missing (Linux/x86_64) and bring the
smoke compose stack up automatically.

## Authoring conventions

Tapes here MUST follow these rules. A reviewer should reject anything
that breaks them.

1. **No literal `Sleep`.** Use `Wait+Screen /pattern/` to wait on stable
   substrings from the wizard's view source
   (`src/Netclaw.Cli/Tui/Wizard/Steps/*StepView.cs`). Sleep-based
   synchronization is the single biggest source of CI flakiness. The
   only acceptable timeout is the outer one inside `run-tape.sh`.

2. **Tapes do not reset state.** The wrapper sets a per-tape
   `NETCLAW_HOME=/tmp/tape-<name>` and clears it before the tape runs.
   Tape bodies do not `rm -rf`, do not `clear`, do not depend on prior
   tape runs.

3. **Tapes do not produce screenshots.** They MAY emit a debug GIF
   (`Output /tmp/tape-<name>.gif`) so failures have a visual artifact —
   but no `Screenshot` directives in the body. Visual diffs are not how
   we assert anything here.

4. **Terminal sizing is in pixels, not columns.** VHS interprets
   `Set Width` / `Set Height` as pixel dimensions (minimum 120x120).
   The preamble sets a sensible default (1400x800 at FontSize 14, which
   gives ~95 cols × 50 rows). Don't override unless a specific tape
   needs more vertical room — and even then, prefer scrolling-friendly
   anchors over taller terminals.

5. **Anchor regexes at every step.** After every `Type` / `Enter` /
   `Down` / etc. that changes the visible state, immediately
   `Wait+Screen /…/` for an anchor in the next view. If a step has no
   stable anchor, fix the production code to add one rather than
   loosening the regex.

6. **Pair each non-trivial tape with an assertion.** Place a sibling
   script at `tests/smoke-interactive/assertions/<tape-name>.sh`. The
   wrapper invokes it with `PROJECT_NAME`, `COMPOSE_FILE`, `TAPE_NAME`,
   and `NETCLAW_HOME_IN` exported. The assertion is the source of truth
   for "did the tape do the right thing" — vhs's exit code only proves
   the screen reached the expected states.

## Anatomy

Each tape body is concatenated *after* `preamble.tape`, which sets up
the docker-exec session into `netclaw-sandbox` with a fresh
`NETCLAW_HOME` and a deterministic prompt (`TAPE$ `). The combined
tape is what `vhs` sees; the prompt anchor `/TAPE\$ $/` is the safe
"back at shell" wait.

The substitution placeholders the wrapper fills in:

| Placeholder         | Replaced with                                |
|---------------------|----------------------------------------------|
| `__PROJECT__`       | docker compose project name                   |
| `__COMPOSE_FILE__`  | absolute path to `docker-compose.smoke.yml`  |
| `__TAPE_NAME__`     | tape short name (used for `NETCLAW_HOME`)    |

## Adding a new tape

1. Identify the interactive flow you're covering.
2. Read the relevant `*StepView.cs` (or other TUI source) to harvest
   stable strings for `Wait+Screen` anchors.
3. Author the tape body following the rules above.
4. Author a sibling assertion script that validates the artifacts the
   tape produced (config fields, `*-list --json` entries,
   `netclaw doctor` exit code).
5. Add the tape's short name to `LIGHT_TAPES` (PR gate) or `FULL_TAPES`
   (nightly) in `scripts/smoke/run-tapes.sh`.
6. Run `./scripts/smoke/run-tapes.sh <name> --keep-stack` until it's
   stable across at least 3 consecutive runs. Treat any flake as a
   bug, not a flake.
