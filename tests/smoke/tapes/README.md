# Native Interactive Smoke Tapes

This directory holds VHS tape scripts that drive netclaw's interactive
CLI surface (Spectre prompts, wizard flows, TUI menus) against the
**real native binary** — no Docker. They exist to catch regressions
like the netclaw/knit interactive-wizard bug that the non-interactive
smoke scenarios (`tests/smoke/scenarios/*.sh`) cannot reach.

The **flow tapes** in this directory (`help.tape`, `init-wizard.tape`,
`provider-add.tape`, …) are **end-to-end test scenarios**, not screenshot
fodder — they emit only a debug GIF, never a `Screenshot`.

The **screenshot-regression tapes** under `tapes/screenshots/` ARE the
screenshot-capture mechanism for the native harness — see the
[Screenshot regression](#screenshot-regression) section below. They are
distinct from the pretty marketing screenshots in
`netclaw-website/screenshots/tapes/`.

## Running locally

```bash
# Light suite (all tapes + non-interactive scenarios).
./scripts/smoke/run-smoke.sh light

# Full suite.
./scripts/smoke/run-smoke.sh full

# Single tape, fast iteration.
./scripts/smoke/run-smoke.sh init-wizard

# Screenshot regression (see the Screenshot regression section below).
./scripts/smoke/run-smoke.sh screenshots
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

3. **Flow tapes do not produce screenshots.** A flow tape (anything in
   this directory other than `tapes/screenshots/`) MAY emit a debug GIF
   (`Output /tmp/tape-<name>.gif`) so failures have a visual artifact —
   but no `Screenshot` directives in the body. `Screenshot` directives
   belong exclusively to the screenshot-regression tapes under
   `tapes/screenshots/` (see below).

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

## Screenshot regression

The screenshot-regression tapes live under `tapes/screenshots/` and ARE
the screenshot-capture mechanism for the native harness. Each one drives
the TUI to a settled state and emits a `Screenshot "/tmp/shot-<frame>.png"`
directive. The `screenshots` profile of `run-smoke.sh` compares each
captured PNG **byte-for-byte** against the committed baseline in
`tests/smoke/screenshots/<frame>.approved.png`.

```bash
./scripts/smoke/run-smoke.sh screenshots
```

How it differs from the flow tapes:

- They use `screenshot-preamble.tape`, not `preamble.tape`. The
  screenshot preamble adds determinism pins — `Set CursorBlink false` and
  an explicit `Set Theme "Catppuccin Mocha"` — so a captured PNG is
  byte-stable given the pinned VHS version + theme + geometry + font size.
- They DO emit `Screenshot` directives — that is their whole purpose.
- They have **no post-tape assertion**. The PNG comparison is the test.
- The harness points `run-native-tape.sh` at them via the `TAPE_PREAMBLE`
  and `TAPE_BODY_DIR` env vars.

Every `Screenshot` is preceded by an anchored `Wait+Screen` (we are on the
right screen) and **bracketed by `Sleep 1s`** — one before so the TUI has
finished painting, one after so the next keystroke cannot leak into the
capture. This is the one place a literal `Sleep` is correct: the no-`Sleep`
rule above is about flow-tape step *synchronization*, whereas a screenshot
needs the frame to be visually *settled*, and a settled static screen
captured at +1s is still deterministic. Never screenshot a screen with a
version string, timestamp, spinner, or token counter — pick a different
settled frame instead.

### Baseline workflow

Baselines are **not** generated locally — they come from a CI run:

1. A `screenshots` run with **no baseline** for a frame, or a frame that
   **differs** from its baseline, **fails** and uploads the captured PNG
   as `<frame>.actual.png` (plus a `<frame>.diff.png` when ImageMagick's
   `compare` is available) into the smoke-logs artifact.
2. A human downloads the artifact and reviews the `*.actual.png`.
3. If the change is correct, commit/update the baseline at
   `tests/smoke/screenshots/<frame>.approved.png`. If it is a regression,
   fix the code.

`tests/smoke/screenshots/` ships with a `.gitkeep` until the first
baselines land.

### Adding a screenshot frame

1. Add (or extend) a tape under `tapes/screenshots/` with a
   `Screenshot "/tmp/shot-<frame>.png"` after an anchored `Wait+Screen`.
2. Add `<frame>` to `SHOT_FRAMES` (and the tape short name to
   `SHOT_TAPES`) in `scripts/smoke/run-smoke.sh`.
3. Run `./scripts/smoke/run-smoke.sh screenshots`; review the uploaded
   `<frame>.actual.png` and commit it as the approved baseline.
