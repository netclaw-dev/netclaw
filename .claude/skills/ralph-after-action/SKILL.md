/---
name: ralph-after-action
description: Orchestrates post-run evaluation: system diagnostics + adversarial output review. Writes postmortem, updates IMPLEMENTATION_PLAN.md with NOW fix-its, parks non-urgent items, and proposes skill/tooling evolutions.
user-invocable: true
---

# RALPH After-Action Review (Orchestrator)

## Goal
After a RALPH run, produce an evidence-based postmortem, repair the plan with high-priority fix-its,
and evolve skills/tooling so the system improves over time.

This skill coordinates TWO distinct reviews:
1) System-performance diagnostics (did the agent loop behave correctly?)
2) Output-quality adversarial review (is the produced work actually correct/complete?)

## Inputs

**Primary (Git Branch):**
- Run branch: (e.g., `ralph/...`)
- Git commit range: `git log dev..HEAD` (all commits on this branch)
- Git diff: `git diff dev..HEAD` (aggregate code changes)

**Secondary (Run Artifacts):**
- Run ID: `<run-id>` (if known; otherwise derive from branch name or find all runs)
- All `.ralph/runs/*/` folders on this branch (there may be multiple due to crashes/restarts)
- `IMPLEMENTATION_PLAN.md`
- `BACKLOG_PARKING_LOT.md` (or create if missing)
- Relevant skills: `testing-strategy`, `ralph-loop`, `ui-smoke-validation` (if present)

**Why Git-First:**
Sessions can crash, restart, or span multiple invocations. Run folders only capture one
session's view. The git branch captures ALL work across all sessions.

**Multi-Run Aggregation:**
If multiple `.ralph/runs/*/` folders exist for this branch:
1. Sort by run ID (timestamp)
2. Build a unified timeline of iterations across all runs
3. Identify orphaned commits (in git but not in any iteration log)
4. Note gaps or overlaps in coverage

## Output Artifacts (commit-friendly)
- `.ralph/runs/<run-id>/postmortem.md`
- Updates to:
  - `IMPLEMENTATION_PLAN.md` (adds a **Fix-it (Postmortem Findings) — NOW** block at top, if needed)
  - `BACKLOG_PARKING_LOT.md` (parks non-urgent findings)
  - skills/tooling docs as appropriate (prefer skills/templates/tooling docs over bloating constitutions)

## Non-Negotiables
- Separate verdicts: diagnostics vs output-quality
- Evidence-based: if verification is claimed, it must be logged in iteration files ("log or it didn't happen")
- Keep IMPLEMENTATION_PLAN.md lean:
  - Only add NOW fix-its that block milestone or violate policy
  - Everything else goes to BACKLOG_PARKING_LOT.md
  - De-dupe fix-its by extracting skills when the issue is procedural
- **Do not re-review commits already covered by mid-loop reviews**

## Procedure

### Step 0: Git Branch Analysis (REQUIRED FIRST)

**This is now the primary source of truth.**

1. Determine the run branch (current branch or specified branch)
2. Get ALL commits on this branch since it diverged from `dev`:
   ```bash
   git log dev..HEAD --oneline
   ```
3. Get aggregate diff to understand scope of changes:
   ```bash
   git diff dev..HEAD --stat
   ```
4. Create a commit ledger:

```markdown
## Commit Ledger (Git Branch: {branch})

| Commit | Message | Files Changed | Covered By Run |
|--------|---------|---------------|----------------|
| abc123 | feat: add webhook service | 5 | 20260131-100711 |
| def456 | test: add webhook tests | 3 | 20260131-100711 |
| 789abc | fix: routing issue | 2 | (orphaned - no iter log) |
```

5. Identify **orphaned commits** - commits in git but not covered by any iteration log:
   - Check each commit against all `.ralph/runs/*/iter-*.md` files
   - Orphaned commits indicate work done outside the RALPH loop or during session recovery
   - These MUST be explicitly reviewed (they had no adversarial oversight)

### Step 1: Collect All Run Artifacts

- Find ALL `.ralph/runs/*/` folders (there may be multiple for this branch)
- For each run folder, collect:
  - `run.md` (run metadata)
  - All `iter-*.md` files (iteration logs)
  - All `review-after-iter-*.md` files (mid-loop reviews)
- Build a unified timeline across all runs
- **Find all `review-after-iter-*.md` files** (sorted by iteration number)
- Build a review ledger:

```markdown
## Mid-Loop Review Ledger

| Review File | Iterations | Commit Range | Verdict | Open Issues |
|-------------|------------|--------------|---------|-------------|
| review-after-iter-03.md | 1-3 | abc123..def456 | PASS | None |
| review-after-iter-07.md | 4-7 | def456..789abc | PARTIAL | Missing cluster roles |
```

- Determine the **last reviewed commit** from the most recent mid-loop review
- Calculate **unreviewed commit range**: `{last_reviewed_commit}..HEAD`

### Step 2: Run System Diagnostics
Invoke skill: `ralph-run-diagnostics`
- Inputs: `<run-id>`, run branch
- Capture its findings and verdict

### Step 3: Run Output Adversarial Review (Incremental)
Invoke skill: `ralph-output-adversarial-review`
- Inputs: `<run-id>`, run branch
- **IMPORTANT**: Pass the unreviewed commit range, NOT the full run range
- The adversarial review skill will:
  - Only review commits in the unreviewed range
  - Check if any PARTIAL issues from prior reviews were resolved
  - Aggregate findings from all reviews for final verdict

### Step 4: Synthesize Postmortem
Write postmortem to appropriate location. If multiple runs exist, write to the last run's folder.

```markdown
# RALPH Branch Postmortem

## Branch Metadata
- Branch: {branch}
- Full Commit Range: {first_commit}..{HEAD}
- Total Commits: {N}
- Date: {date}

## Commit Ledger (Git is Source of Truth)

| Commit | Message | Covered By | Disposition |
|--------|---------|------------|-------------|
| abc123 | feat: add webhook service | iter-01 (run 100711) | Reviewed |
| def456 | test: add webhook tests | iter-02 (run 100711) | Reviewed |
| 789abc | fix: routing issue | (orphaned) | **NEEDS REVIEW** |
| ghi012 | feat: add billing page | iter-01 (run 130413) | Reviewed |

### Orphaned Commits (Require Special Review)

The following commits were made outside the RALPH loop or during recovery.
They did NOT receive adversarial review oversight.

| Commit | Message | Why Orphaned | Manual Review |
|--------|---------|--------------|---------------|
| 789abc | fix: routing issue | Session crashed mid-iteration | **Reviewed in this postmortem** |

## Runs on This Branch

| Run ID | Iterations | Commits Covered | Status |
|--------|------------|-----------------|--------|
| 20260131-100711 | 1-5 | abc123..def456 | Completed |
| 20260131-130413 | 1-3 | ghi012..jkl345 | Crashed |
| 20260131-141838 | 1-5 | mno678..pqr901 | Completed |

## Mid-Loop Review Summary (Aggregated)
| Review | Run | Iterations | Verdict | Key Findings |
|--------|-----|------------|---------|--------------|
| review-after-iter-03 | 100711 | 1-3 | PASS | None |
| review-after-iter-07 | 141838 | 4-7 | PARTIAL | Missing cluster roles |
| Final (this review) | - | remaining | PASS | Cluster roles task created |

## Verdicts
- Diagnostics: PASS / PARTIAL / FAIL
- Output Quality (Aggregated): PASS / PARTIAL / FAIL
- Orphaned Commit Review: PASS / PARTIAL / FAIL

## Aggregated Findings
{Combine findings from ALL reviews - mid-loop + final + orphaned commit review}

## Issue Resolution Tracking
| Issue | Found In | Status | Resolution |
|-------|----------|--------|------------|
| Missing cluster roles | iter-07 review | Resolved | Task 5.7d created in iter-10 |

## Root Causes
{Process vs implementation issues}

## Actions
- NOW fix-its: {list or "None"}
- Cleanup sprint tasks: {list or "None" - bug fixes, investigations added to IMPLEMENTATION_PLAN.md}
- Parked items: {list or "None" - only items needing human decision}
- Skill/tooling proposals: {list or "None"}
```

### Step 5: Update IMPLEMENTATION_PLAN.md (Lean Fix-it block)
If there are launch-blocking issues or policy violations, add at the very top:

## Fix-it (Postmortem Findings) — NOW
> Generated from RALPH run: <run-id> (YYYY-MM-DD)

Add **only** tasks that:
- break core journeys / UI is broken / console errors
- miss required tests per `testing-strategy`
- violate architecture invariants (service layer pattern, etc.)
- create data integrity or security risk
- represent “checkbox fraud” (marked done without evidence)

Each Fix-it task must have objective Done-when checkboxes and explicit Verification level.

### Step 6: Triage Non-Blocking Items

**Bug fixes and straightforward improvements → Add to IMPLEMENTATION_PLAN.md as Cleanup Sprint**

If an item is a bug fix, investigation, or clear improvement that doesn't need human decision-making:
- Add it to a "Cleanup Sprint" section immediately after the most recently completed phase
- Use "Task C.N" numbering (C for Cleanup)
- These get done before starting the next major phase

Example cleanup sprint tasks:
- Bug fixes discovered during testing
- Investigations ("check if X pattern exists elsewhere")
- Small refactors with clear scope
- Test coverage gaps

**Only park items that genuinely need human input:**

Append to `BACKLOG_PARKING_LOT.md` ONLY for items that require:
- Architecture decisions (human must choose approach)
- Priority/scope clarification (human must decide if worth doing)
- External dependencies (waiting on third party)

The parking lot should NOT be used for:
- Procrastination on known bugs
- "Nice to have" improvements that are easy to do
- Speculative work ("might be an issue someday")

**Anti-pattern:** If the parking lot keeps growing, you're parking too much. Most items should either be done immediately, added to a cleanup sprint, or deleted.

### Step 7: Evolve the System (Skills + Tooling)
If a failure mode occurred ≥2 times in this run (or appears recurrent):
- Draft/update a skill under `.claude/skills/` (preferred)
- Or add a template under `agents/templates/` if output-format related
- Or update `TOOLING.md` if a tool workflow was unclear
Do NOT bloat AGENTS.md / CLAUDE.md.

## Completion Criteria
Complete only when:
- postmortem.md is written
- plan is updated with necessary NOW fix-its (or explicitly none)
- non-urgent items are parked
- at least one concrete system-evolution proposal exists if meaningful gaps were observed
