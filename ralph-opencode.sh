#!/bin/bash
# RALPH Loop Runner - OpenCode Edition (with Flight Recorder)
#
# Usage:
#   ./ralph-opencode.sh                      # Run (5 iterations default)
#   ./ralph-opencode.sh 10                   # Run 10 iterations
#   ./ralph-opencode.sh --model <m>          # Run with model override
#   ./ralph-opencode.sh --postmortem-model <m>
#   ./ralph-opencode.sh --review-interval 3  # Run adversarial review every 3 iterations
#   ./ralph-opencode.sh --model <m> 10
#   RALPH_MODEL=openai/gpt-4.5 ./ralph-opencode.sh
#   ./ralph-opencode.sh --model openai/gpt-5.2-codex --postmortem-model github-copilot/claude-opus-4.5 9
#   Postmortem runs automatically after the loop.
#
# Each iteration is a FRESH OpenCode context window.
# Progress lives in files + git + .ralph flight recorder logs.

set -euo pipefail

# Ensure Ctrl+C kills the whole loop, not just the current child process.
# Without this, the child CLI catches SIGINT and exits 0, so the `if !` block
# doesn't fire and the loop continues to the next iteration.
trap 'echo ""; echo "RALPH loop interrupted."; exit 130' INT TERM

PLAN_FILE="IMPLEMENTATION_PLAN.md"
ITERATIONS=5
MODEL="${RALPH_MODEL:-github-copilot/claude-opus-4.5}"
POSTMORTEM_MODEL="${RALPH_POSTMORTEM_MODEL:-$MODEL}"
REVIEW_INTERVAL="${RALPH_REVIEW_INTERVAL:-0}"  # 0 = disabled
L3_GATE_ENABLED="${RALPH_L3_GATE:-true}"       # Block commits without L3 evidence
L3_GATE_BYPASS=false

# --- arg parsing (allow: [--model X] [--review-interval N] [iterations]) ---
while [[ $# -gt 0 ]]; do
  case "$1" in
    --model|-m)
      if [[ $# -lt 2 ]]; then
        echo "Missing value for $1"
        echo "Usage: $0 [--model <model>] [--postmortem-model <model>] [--review-interval N] [--skip-l3-gate] [iterations]"
        exit 1
      fi
      MODEL="$2"
      POSTMORTEM_MODEL="${RALPH_POSTMORTEM_MODEL:-$MODEL}"
      shift 2
      ;;
    --postmortem-model)
      if [[ $# -lt 2 ]]; then
        echo "Missing value for $1"
        echo "Usage: $0 [--model <model>] [--postmortem-model <model>] [--review-interval N] [--skip-l3-gate] [iterations]"
        exit 1
      fi
      POSTMORTEM_MODEL="$2"
      shift 2
      ;;
    --review-interval)
      if [[ $# -lt 2 ]]; then
        echo "Missing value for $1"
        echo "Usage: $0 [--model <model>] [--postmortem-model <model>] [--review-interval N] [--skip-l3-gate] [iterations]"
        exit 1
      fi
      REVIEW_INTERVAL="$2"
      shift 2
      ;;
    --skip-l3-gate)
      L3_GATE_BYPASS=true
      shift
      ;;
    *)
      # if it's a number, treat as iterations
      if [[ "$1" =~ ^[0-9]+$ ]]; then
        ITERATIONS="$1"
        shift
      else
        echo "Unknown arg: $1"
        echo "Usage: $0 [--model <model>] [--postmortem-model <model>] [--review-interval N] [--skip-l3-gate] [iterations]"
        exit 1
      fi
      ;;
  esac
done

# Check if opencode is installed
if ! command -v opencode &> /dev/null; then
  echo "Error: opencode is not installed or not in PATH"
  exit 1
fi

# Run ID and log directory
RUN_ID="${RUN_ID:-$(date +%Y%m%d-%H%M%S)}"
RUN_DIR=".ralph/runs/${RUN_ID}"
mkdir -p "$RUN_DIR"

echo "=========================================="
echo "  RALPH Loop (OpenCode)"
echo "  Model: $MODEL"
echo "  Postmortem Model: $POSTMORTEM_MODEL"
echo "  Review Interval: $REVIEW_INTERVAL (0=disabled)"
echo "  L3 Gate: $L3_GATE_ENABLED (bypass=$L3_GATE_BYPASS)"
echo "  Iterations: $ITERATIONS"
echo "  Run ID: $RUN_ID"
echo "  Run Dir: $RUN_DIR"
echo "=========================================="
echo ""

# Ensure we're on the right branch
BRANCH=$(git branch --show-current)
echo "Current branch: $BRANCH"

if [[ "$BRANCH" == "dev" || "$BRANCH" == "main" || "$BRANCH" == "master" ]]; then
  NEW_BRANCH="ralph/opencode-${RUN_ID}"
  echo "On protected branch, creating feature branch: $NEW_BRANCH"
  git checkout -b "$NEW_BRANCH"
  BRANCH=$(git branch --show-current)
  echo "Now on: $BRANCH"
fi
echo ""

START_COMMIT=$(git rev-parse HEAD)
LAST_REVIEW_COMMIT="$START_COMMIT"
{
  echo "Run ID: $RUN_ID"
  echo "Branch: $BRANCH"
  echo "Model: $MODEL"
  echo "Postmortem model: $POSTMORTEM_MODEL"
  echo "Review interval: $REVIEW_INTERVAL"
  echo "Run start commit: $START_COMMIT"
  echo "Started: $(date)"
  echo ""
} | tee "$RUN_DIR/run.md" >/dev/null

# Function to run mid-loop adversarial review
run_mid_review() {
  local iteration=$1
  local current_commit
  current_commit=$(git rev-parse HEAD)

  echo ""
  echo "=========================================="
  echo "  Mid-Loop Adversarial Review (after iteration $iteration)"
  echo "  Reviewing commits: ${LAST_REVIEW_COMMIT}..${current_commit}"
  echo "=========================================="
  echo ""

  local review_log="${RUN_DIR}/review-after-iter-$(printf "%02d" "$iteration").md"

  # Find prior review files for cumulative context
  local prior_reviews=""
  for f in "$RUN_DIR"/review-after-iter-*.md; do
    [[ -f "$f" ]] && prior_reviews="$prior_reviews\n- $f"
  done

  if ! opencode run --model "$POSTMORTEM_MODEL" "Run a full adversarial review using the adversarial review skill.

## Context
- RUN_ID: $RUN_ID
- RUN_DIR: $RUN_DIR
- Iteration just completed: $iteration
- Commit range to review: ${LAST_REVIEW_COMMIT}..${current_commit}
- Review log to write: $review_log
- Prior review files (read these FIRST for cumulative context):
${prior_reviews:-  (none — this is the first review)}

## Instructions

1. **REQUIRED:** Load .claude/skills/ralph-output-adversarial-review.md and execute the FULL review protocol.
   This is NOT optional. Do not perform a lightweight review.

2. **Cumulative review protocol:**
   - Read ALL prior review files listed above
   - For each prior review's open issues: check if resolved in commits since that review
   - If resolved: note resolution with commit hash
   - If NOT resolved: carry forward as open issue in THIS review
   - If a prior review missed something you now see: add it as a NEW finding

3. **Full review areas (from skill):**
   - A) Checkbox Integrity — do changes satisfy Done-when criteria?
   - B) Testing Strategy Compliance — integration tests for I/O, screenshots for UI
   - C) Architecture Compliance — follow constraints from AGENTS.md/CLAUDE.md
   - D) Framework compliance per CLAUDE.md
   - E) UI Sanity — error states, loading states, runtime errors
   - E2) UI Screenshot Gate — UI files must have L3+ with screenshots
   - F) Regression Risk — edge cases, N+1 queries, unsafe assumptions
   - G) Slopwatch — reward hacking patterns
   - H-K) Duplication, useless tests, L3 evidence audit, L3+ artifact verification

4. **Write findings to: $review_log** using the skill's deliverable format for mid-loop reviews.

5. **Write actionable items:**
   - NOW items: insert at top of IMPLEMENTATION_PLAN.md
   - PARK items: append to BACKLOG_PARKING_LOT.md
   - Every finding MUST have a disposition (NOW, PARK, or FIX INLINE)

6. **Process improvement authority (additive only):**
   - If you identify a recurring pattern that should be a skill: create it in .claude/skills/
   - If you identify a gap in ralph-loop.md or testing-strategy.md: add the missing check/gate
   - You may ONLY add rules, never remove or weaken existing ones
   - You may NOT edit CLAUDE.md (constitution requires human approval)
   - Log all process edits under '## Process Improvements Applied' in the review file

7. **Verdict:**
   - Output 'VERDICT: FAIL' if hard fail criteria met (pauses the loop)
   - Output 'VERDICT: PARTIAL' if issues found but not blocking
   - Output 'VERDICT: PASS' if clean
"; then
    echo "Mid-loop review failed to execute"
    return 1
  fi

  # Check if the review log contains FAIL verdict
  if [[ -f "$review_log" ]] && grep -q "VERDICT: FAIL" "$review_log"; then
    echo ""
    echo "=========================================="
    echo "  ADVERSARIAL REVIEW FAILED"
    echo "  Review found issues requiring human attention."
    echo "  See: $review_log"
    echo "=========================================="
    return 1
  fi

  # Update last review commit
  LAST_REVIEW_COMMIT="$current_commit"
  echo "Mid-loop review passed, continuing..."
  echo ""
  return 0
}

# Function to verify L3 evidence if L3 was claimed
verify_l3_evidence() {
  local iter_log=$1

  # Skip if gate disabled or bypassed
  if [[ "$L3_GATE_ENABLED" != "true" ]] || [[ "$L3_GATE_BYPASS" == "true" ]]; then
    return 0
  fi

  # Check if iteration claimed L3 verification level
  if ! grep -q "Level: L3\|Level: L4" "$iter_log" 2>/dev/null; then
    # Not an L3/L4 task, no gate needed
    return 0
  fi

  echo ""
  echo "  L3/L4 Verification Gate"
  echo "  Checking for required evidence in: $iter_log"

  local missing_evidence=()

  # Check for application running evidence
  if ! grep -qi "aspire run\|dotnet run\|npm start\|yarn dev\|Application Started\|resources healthy\|Server started\|listening on" "$iter_log" 2>/dev/null; then
    missing_evidence+=("Application running (start command with evidence)")
  fi

  # Check for routes checked evidence
  if ! grep -qi "Routes Checked\|Routes checked\|Route.*200\|Route.*rendered" "$iter_log" 2>/dev/null; then
    missing_evidence+=("Routes navigated (Routes Checked section)")
  fi

  # Check for console errors evidence
  if ! grep -qi "Console errors: none\|Console errors:.*none\|no console errors" "$iter_log" 2>/dev/null; then
    # Also check if they documented errors (which is valid if they then fix them)
    if ! grep -qi "Console errors:" "$iter_log" 2>/dev/null; then
      missing_evidence+=("Console errors checked (Console errors: none)")
    fi
  fi

  # Check for viewport evidence
  if ! grep -qi "Viewport.*pass\|viewport check\|1024.*1280.*1920\|1024px\|viewport sanity" "$iter_log" 2>/dev/null; then
    missing_evidence+=("Viewport sanity check (1024/1280/1920)")
  fi

  if [[ ${#missing_evidence[@]} -gt 0 ]]; then
    echo ""
    echo "  ==========================================="
    echo "  L3 VERIFICATION GATE FAILED"
    echo "  ==========================================="
    echo ""
    echo "  The iteration claimed L3/L4 verification level but is missing required evidence:"
    echo ""
    for item in "${missing_evidence[@]}"; do
      echo "    - $item"
    done
    echo ""
    echo "  Per ralph-loop.md L3 Verification Checklist, these are MANDATORY when claiming L3."
    echo ""
    echo "  Options:"
    echo "    1. Fix the iteration to include proper L3 evidence"
    echo "    2. Downgrade to L2 with documented justification"
    echo "    3. Re-run with --skip-l3-gate (emergency bypass)"
    echo ""
    return 1
  fi

  echo "  L3 evidence verified: PASS"
  return 0
}

# Loop - each iteration is a fresh OpenCode context
for ((i=1; i<=ITERATIONS; i++)); do
  echo "=========================================="
  echo "  RALPH Iteration $i of $ITERATIONS"
  echo "  $(date)"
  echo "=========================================="
  echo ""

  # Check if there are any unchecked items
  if [[ ! -f "$PLAN_FILE" ]]; then
    echo "Missing required plan file: $PLAN_FILE"
    exit 1
  fi
  if ! grep -q '\- \[ \]' "$PLAN_FILE"; then
    echo "No unchecked items remaining in $PLAN_FILE"
    echo "RALPH loop complete!"
    break
  fi

  ITER_PAD=$(printf "%02d" "$i")
  ITER_LOG="${RUN_DIR}/iter-${ITER_PAD}.md"

  if ! opencode run --model "$MODEL" "You are running RALPH iteration $i.

## Run Metadata (MUST USE)
- RUN_ID: $RUN_ID
- RUN_DIR: $RUN_DIR
- ITERATION: $i
- ITER_LOG (write this file before commit): $ITER_LOG

## Bootstrap (Read these files FIRST)
1. AGENTS.md and/or CLAUDE.md - Constitution (authority, constraints, quality bar, routing)
2. PROJECT_CONTEXT.md - Current architecture and state (if present)
3. TOOLING.md - Available tools/services (if present)
4. IMPLEMENTATION_PLAN.md - Task breakdown
5. openspec/specs/README.md - Capability map

## Instructions (ONE TASK ONLY)

1) Find the next incomplete task in IMPLEMENTATION_PLAN.md:
    - Look for '### Task:' blocks with unchecked 'Done when:' items
    - Work on the FIRST incomplete task you find
    - A task is complete only when ALL its Done-when checkboxes are satisfied
    - Read task metadata fields: PRD, OpenSpec Capabilities, OpenSpec Changes
    - Open the referenced OpenSpec files before implementing

2) Determine MODE from Task Routing in AGENTS.md/CLAUDE.md (engineering/ux/marketing/ops/etc.)

3) Load relevant skills from .claude/skills/:
   - REQUIRED for code: testing-strategy.md (if present — integration vs unit; no fakes)
   - REQUIRED: ralph-loop.md (process discipline)
   - If UI impacted: ui-smoke-validation.md (or follow UI validation policy)
   - If schema/events touched: extend-only-design.md (if present)

4) BEFORE coding: choose Verification Level (L0-L4) and state why:
   - I/O coordination (DB/HTTP/actors/external) => L2+ (integration tests required)
   - UI or UI dependency changed => L3+ (UI smoke / Playwright required)

5) Implement to satisfy ALL unchecked Done-when criteria for the chosen task.

6) Verify (must match chosen level):
    - Minimum: build + test (language-appropriate commands)
    - If Level >= L3: run UI smoke/Playwright and check for console errors
    - Follow any additional quality gates from AGENTS.md/CLAUDE.md
    - Run openspec validate --all --no-interactive before committing

7) FLIGHT RECORDER (MANDATORY):
   - Write $ITER_LOG BEFORE committing.
   - Include:
     - Task selected (exact title)
     - Surface area classification
     - Verification level chosen + reason
     - Skills consulted
     - Commands run + outcomes
     - Deviations/skips + justification
     - Follow-ups noticed but deferred + why
   - If you claim a command was run, it must appear in the log with outcome.
   - 'Log or it didn't happen.'

8) If verification passes:
    - Commit to the current feature branch with a descriptive message
    - Update IMPLEMENTATION_PLAN.md checkboxes in the SAME commit
    - Update referenced openspec/changes/*/tasks.md checkboxes in the SAME commit
    - Update TOOLING.md if you used or discovered a new tool/resource

9) Stop at checkpoints (UI approval, architecture decisions, credential setup) and ask the user if needed.

10) Exit - do NOT continue to additional tasks.

## Constraints (Constitution)
- ONE iteration = ONE task block
- Never commit to dev/main/master
- Follow constraints from AGENTS.md/CLAUDE.md
- Test against real infrastructure (per testing-strategy)
- Keep OpenSpec changes/specs synchronized with implementation
"; then
    EXIT_CODE=$?
    echo ""
    echo "OpenCode exited with code $EXIT_CODE"
    echo "RALPH loop paused at iteration $i"
    exit $EXIT_CODE
  fi

  echo ""
  echo "Iteration $i complete"

  # Run L3 verification gate if L3 was claimed
  if ! verify_l3_evidence "$ITER_LOG"; then
    echo "RALPH loop paused due to L3 verification gate failure at iteration $i"
    echo "See iteration log for details: $ITER_LOG"
    exit 1
  fi

  echo ""

  # Run mid-loop adversarial review if interval is set and we've hit it
  if [[ "$REVIEW_INTERVAL" -gt 0 ]] && (( i % REVIEW_INTERVAL == 0 )) && (( i < ITERATIONS )); then
    if ! run_mid_review "$i"; then
      echo "RALPH loop paused due to adversarial review failure at iteration $i"
      echo "Review the findings and fix issues before continuing."
      exit 1
    fi
  fi

  sleep 2
done

END_COMMIT=$(git rev-parse HEAD)
{
  echo "Finished: $(date)"
  echo "Run end commit: $END_COMMIT"
  echo "Commit range: ${START_COMMIT}..${END_COMMIT}"
} | tee -a "$RUN_DIR/run.md" >/dev/null

echo "=========================================="
echo "  RALPH Loop Finished"
echo "  Run ID: $RUN_ID"
echo "  Branch: $(git branch --show-current)"
echo "  Logs: $RUN_DIR"
echo "  Commit range: ${START_COMMIT}..${END_COMMIT}"
echo "=========================================="

echo ""
echo "Remaining incomplete tasks:"
grep -B5 '^\- \[ \]' "$PLAN_FILE" | grep '### Task:' | head -5 || echo "(none or legacy format)"

echo ""
echo "Running postmortem (skill): ralph-after-action"
# Note: OpenCode doesn't have OpenProse plugin, so we invoke the skill directly.
# This runs sequentially. For parallel execution, use ralph.sh with Claude Code.
if ! opencode run --model "$POSTMORTEM_MODEL" "/ralph-after-action RUN_ID=$RUN_ID RUN_DIR=$RUN_DIR branch=$(git branch --show-current)"; then
  POSTMORTEM_EXIT=$?
  echo ""
  echo "Postmortem exited with code $POSTMORTEM_EXIT"
  exit $POSTMORTEM_EXIT
fi
echo "Postmortem complete"
echo "Logs live at: $RUN_DIR"
