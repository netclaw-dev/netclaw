---
name: ralph-run-diagnostics
description: Evaluates whether the RALPH loop followed policy: correct task selection, verification level selection, required tests, skill usage, logging completeness, and justification for deviations.
---

# RALPH Run Diagnostics (System-Performance)

## Goal
Determine whether the agent system behaved correctly: followed RALPH rules, used required skills,
chose correct verification levels, and produced adequate evidence.

This is NOT a code correctness review. It's a process compliance review.

## Inputs
- Run ID: `<run-id>`
- Run directory: `.ralph/runs/<run-id>/`
- Run branch + commits
- `IMPLEMENTATION_PLAN.md` diffs across the run
- `openspec/changes/*/tasks.md` diffs across the run
- `openspec validate --all` output (if available in logs)
- Skills: `ralph-loop`, `testing-strategy` (if present), UI validation skills (if present)

## Output
- A diagnostics section (to be pasted into postmortem)
- A verdict: PASS / PARTIAL / FAIL
- Concrete recommendations:
  - RALPH prompt tightening
  - logging rules
  - missing skills / unclear tooling docs

## Audit Checklist

### A) Evidence Completeness ("Log or it didn't happen")
For each iteration log `.ralph/runs/<run-id>/iter-*.md`, verify it includes:
- Selected task (exact task title)
- Surface area classification
- Verification level chosen (L0–L4) + reason
- Skills consulted (at minimum: testing-strategy when code changes)
- Commands run (explicit) + outcome
- Deviations/skips + justification

If any of these are missing, mark as a diagnostic failure.

### B) Task Selection Discipline
- Did each iteration work on ONE "### Task:" block?
- Did it complete ALL Done-when checkboxes for that task before moving on?
- Were checkboxes ticked without matching artifacts/evidence?

### B2) OpenSpec Synchronization Discipline
- If task referenced `OpenSpec Changes`, were corresponding
  `openspec/changes/<name>/tasks.md` checkboxes updated?
- Was `openspec validate --all --no-interactive` run and logged before commit?
- Do implementation changes keep capabilities/spec deltas consistent?

### B3) OpenSpec Workflow Discipline
- Were OpenSpec artifacts created through the `/opsx-*` skills?
  - `/opsx-new` for new changes
  - `/opsx-continue` or `/opsx-ff` for artifact creation
  - `/opsx-sync` for syncing delta specs
  - `/opsx-archive` for archiving completed changes
- Manual creation/editing of files under `openspec/` (except task checkbox
  updates) indicates a workflow violation.
- Check iteration logs for evidence of skill invocation when OpenSpec work was
  required.

### C) Verification Level Discipline
- Was verification level appropriate for surface area?
  - I/O changes (db/http/actors/external) → L2+ (integration tests)
  - UI changes or UI dependency changes → L3+ (manual click testing via Playwright MCP)
- If level downgraded, was the reason valid and documented?

### C2) L3/L4 Evidence Verification
For any iteration claiming L3 or L4 verification:
- Did they start the application (start command in Commands Run)?
- Did they navigate to routes (listed in log)?
- Did they take screenshots (files in `.ralph/runs/<run-id>/screenshots/`)?
- Did they check console errors (documented in log)?
- Did they perform click testing (actions documented)?

**Note:** We do NOT want Playwright test code (.cs/.ts files). We want evidence of
manual click testing using Playwright MCP tools, with screenshots as artifacts.

### D) Skill Usage Discipline
- If new services/endpoints/actors were changed, did the agent consult `testing-strategy`?
- If UI impacted, did it consult UI validation skills (if available)?
- If not, did it log why?

### E) System Evolution Discipline
- Did the run propose or update any skills/templates/tooling docs when a workflow was non-obvious?
- If no, check iteration logs for "weirdness" that SHOULD have triggered evolution.
  - If found, recommend specific skill extraction.

## Verdict Rules
- PASS: all iterations have complete evidence; verification levels appropriate; no checkbox fraud; deviations justified.
- PARTIAL: minor evidence gaps or 1–2 questionable verification decisions, but overall usable.
- FAIL: missing logs, repeated skipping of required verification/testing, or checked boxes without evidence.

## Deliverable
Write a concise diagnostics summary with:
- Verdict
- Top 3 systemic failures (if any)
- Concrete patch recommendations (RALPH prompt, logging format, required gates)
- Proposed skill/tooling additions (names + intent)
