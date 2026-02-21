---
name: ralph-output-adversarial-review
description: Adversarially reviews the artifacts produced by a RALPH run (code/tests/UI/config) against repo policy (testing-strategy, architecture). Produces fix-it tasks (NOW vs PARKED) with objective DoD and verification levels.
---

# RALPH Output Adversarial Review (Artifact-Quality)

## Goal
Critically evaluate the OUTPUT of the RALPH run: code, tests, UI, configuration.
This is a correctness/completeness review against repo policy and architecture.

This is NOT a process compliance review (that's diagnostics).

## Core Principle: Flag It, Fix It

If an issue is worth mentioning in a review, it must land in an actionable location:
- **NOW** → written to `IMPLEMENTATION_PLAN.md` (agent fixes next iteration)
- **PARK** → written to `BACKLOG_PARKING_LOT.md` (needs human decision on non-blocking item)
- **FIX INLINE** → fixed during this review (trivial items, < 5 min)

There is no "noted for awareness" category. Reviews that flag issues without writing them to actionable locations are waste.

## Inputs
- Run ID: `<run-id>`
- Run directory: `.ralph/runs/<run-id>/` (logs + evidence)
- Run branch + commit range to review
- `IMPLEMENTATION_PLAN.md` (what was claimed done)
- Related `openspec/specs/*/spec.md` and `openspec/changes/*` artifacts

---

## Review Stack Protocol (CRITICAL)

RALPH runs use incremental adversarial reviews. Each review only examines commits
since the last review. This prevents redundant work and creates a clear audit trail.

### Review Stack Structure

```
.ralph/runs/<run-id>/
├── run.md                        # Contains: run start commit, last_review_commit
├── iter-01.md
├── iter-02.md
├── iter-03.md
├── review-after-iter-03.md       # Review 1: start_commit..commit_after_iter_03
├── iter-04.md
├── iter-05.md
├── iter-06.md
├── review-after-iter-06.md       # Review 2: commit_after_iter_03..commit_after_iter_06
├── iter-07.md
├── iter-08.md
├── iter-09.md
├── iter-10.md
└── postmortem.md                 # Final: commit_after_iter_06..HEAD (only unreviewed)
```

### Step 1: Determine Your Review Boundaries

**For mid-loop reviews:**
1. Read `run.md` to get the run start commit
2. Find all existing `review-after-iter-*.md` files (sorted by iteration number)
3. Read the LAST review file to find its ending commit
4. Your review range: `{last_review_end_commit}..{current_HEAD}`

**For final postmortem:**
1. Same as above - find the last mid-loop review's end commit
2. Your review range: `{last_review_end_commit}..{current_HEAD}`
3. Also: summarize all prior reviews (don't re-review, just aggregate)

### Step 2: Read Prior Reviews for Context

For each prior `review-after-iter-*.md`, extract:
- Commit range reviewed
- Verdict (PASS/PARTIAL/FAIL)
- Issues flagged (especially PARTIAL/FAIL items)
- Whether issues were marked as "needs follow-up"

### Step 3: Handle Open Issues from Prior Reviews

If prior reviews flagged issues:

**PARTIAL issues:** Check if subsequent commits addressed them
- If YES: Note "Resolved in commit {hash}" - no need to re-flag
- If NO: Carry forward as "Previously flagged, still unresolved"

### Step 4: Write Your Review Header

Every review file MUST start with review metadata including run ID, review type,
iterations covered, commit range, and prior review count.

### Step 5: Record Your End Commit

At the end of your review, record the end commit so the next review knows where to start.

---

## Required Skills to Load

Before reviewing, **load these skills** to have the correct quality bar:

| Skill | Review Area |
|-------|-------------|
| `testing-strategy` (if present) | Test coverage, integration vs unit |
| `extend-only-design` (if present) | Schema/persistence compatibility |
| `slopwatch` | Reward hacking detection |

**Conditional skills** (load if relevant changes exist):

| Skill | When to Load |
|-------|--------------|
| Framework-specific testing patterns | If framework tests added/modified |
| UI validation skills | If UI changes made |

**Note:** Check CLAUDE.md for project-specific mandatory skills. The above are generic;
your project may have additional required skills (e.g., actor patterns, database patterns,
serialization rules). Always consult the project's mandatory trigger table.

## Output
- A section for postmortem: verdict + findings
- Concrete Fix-it items:
  - NOW (plan top) vs PARK (backlog)
- Where appropriate: proposed new skills/templates/tooling docs

---

## Review Areas (Adversarial)

**Only review commits in YOUR commit range. Do not re-review prior ranges.**

### A) Checkbox Integrity
For each task marked complete IN YOUR COMMIT RANGE:
- Do the code changes actually satisfy each "Done when" checkbox?
- Are any boxes ticked without real behavior present?
- Are acceptance criteria ambiguous or misinterpreted?

### A2) OpenSpec Integrity
- Do code changes align with referenced OpenSpec capabilities/scenarios?
- If a task references an OpenSpec change, was the change task list updated?
- Are spec/implementation changes synchronized (no undocumented behavior drift)?

### A3) OpenSpec Workflow Compliance
- Were OpenSpec artifacts (specs, changes, proposals, delta specs, design docs)
  created through the `/opsx-*` skills, or were they manually edited?
- Manual edits to `openspec/` files (other than task checkbox updates) are a
  **PARTIAL** finding — the agent MUST use the OpenSpec skills:
  - `/opsx-new` for new changes
  - `/opsx-continue` or `/opsx-ff` for artifact creation
  - `/opsx-sync` for syncing delta specs to main specs
  - `/opsx-archive` for archiving completed changes
- Check git diff for manually-created spec files that bypassed the workflow.

### B) Testing Strategy Compliance
Using `testing-strategy` (if present):
- If code coordinates DB/HTTP/actors/external services:
  - Integration tests must exist and validate real behavior
- If endpoints added/changed:
  - Integration tests must cover request/response behavior
- If UI changed or UI dependencies changed:
  - Screenshots in `.ralph/runs/<run-id>/screenshots/` must exist
- Auth in tests:
  - Must use dev auth method (never real OAuth)

### C) Architecture / Layering Compliance
Check for:
- endpoints calling internal services directly when a service layer is required
- business logic in UI layer
- missing DI registration or misconfigured services
- duplication or obvious smell introduced

### D) Framework/Library Compliance
Using mandatory skills from CLAUDE.md (if present):

Check that framework-specific patterns are followed correctly. Examples (apply equivalents for your project's stack):
- Cluster/distributed patterns configured correctly
- Persistence/serialization follows extend-only design
- Props/factories created via DI, not manual instantiation

### E) UI Sanity (if applicable)
If UI was impacted:
- Are there likely runtime errors? (null states, missing guards)
- Are empty/error/loading states handled?
- Would a user journey plausibly break?
- **Are there screenshots in `.ralph/runs/<run-id>/screenshots/`?** (L3 evidence)
- Is there documented manual click testing in the iteration log?

### E2) UI Screenshot Gate Enforcement

For each iteration that touched UI files (e.g., `*.razor`, `*.css`, `*.tsx`, `*.vue`, `*.svelte`), the reviewer MUST:

1. **Detect which iterations touched UI files** — scan `git diff` for UI file changes
2. **Verify verification level is L3+** — L2 for UI work is an automatic **FAIL**
3. **Verify screenshot files exist on disk** in `.ralph/runs/{RUN_ID}/screenshots/`
4. **Actually read the screenshot images** and check for obvious visual problems (broken layouts, empty pages, error screens)
5. **Check mockup comparison** if the task's done-when criteria specify it

**Hard fail criterion:** UI files modified + (verification < L3 OR no screenshots on disk) = **FAIL**

### F) Regression Risk
- missing negative tests / edge cases
- N+1 query patterns
- unsafe assumptions
- telemetry gaps (if required)

### G) Slopwatch (Mandatory)
**Run slopwatch checks on files changed in YOUR commit range.**

Check for reward hacking patterns:
- Disabled or skipped tests
- Suppressed warnings
- Empty catch blocks that swallow errors
- Hardcoded values that should be configurable
- TODO comments marking incomplete work as done

---

## Hard Fail Criteria (MANDATORY)

The following issues result in automatic **FAIL** verdict. No exceptions.

### Verification Fraud

**FAIL if:**
- Verification level claimed (L3/L4) but evidence is missing from iteration log
- "Can be performed manually later" without actual manual evidence
- "Deferred to follow-up" for L3 verification that should have been done
- Routes claimed navigable but no navigation evidence recorded

### False Positive Prevention (MANDATORY)

Before flagging ANY finding as FAIL or PARTIAL, you MUST verify the issue exists with evidence.

**Before flagging a file as missing:**
- Use `Glob` or `ls` to verify the file doesn't exist on disk
- File naming may differ from what you expect — check for alternate names

**Before flagging imports/usings as dead code:**
- Search for type names in the codebase (not just direct references)
- Types resolved at runtime (DI, reflection, actor instantiation) won't show as direct references

**Every FAIL/PARTIAL finding must include:**
- The specific command/search you ran to verify the issue
- The output of that command
- No finding without evidence

### Missing Required Tests

**PARTIAL minimum if:**
- New UI components added without screenshots
- New endpoints added without integration tests
- New services with I/O added without integration tests

### Deferred Core Functionality

**FAIL if:**
- Error messages set but never displayed to users
- Error handling code paths that silently fail
- Form validation that never surfaces to UI

---

## Detection Heuristics

### H) Brazen Duplication

Flag as **PARTIAL** when:
> "A bug fix or feature change would require making the same change in multiple files to keep them consistent."

### I) Useless Tests

Flag as **PARTIAL** when tests provide no actual coverage of OUR code.

**Rule:** "Test YOUR code's behavior, not the libraries you depend on."

### J) L3 Evidence Audit

For any task with L3/L4 verification level, explicitly verify:

1. **Did the agent run the application?**
2. **Did the agent navigate to the routes?**
3. **Did the agent check console errors?**
4. **Is there runtime evidence, not just compile-time?**

### K) L3+ Artifact Verification (MANDATORY)

For any task claiming L3 or L4 verification, verify that screenshot artifacts exist on disk.

---

## Must-Check Section

Before issuing a PASS verdict, explicitly confirm:

- [ ] For any L3+ task: Screenshot files exist in `.ralph/runs/{RUN_ID}/screenshots/` (verified with ls/Glob)
- [ ] For any L3+ task: Iteration log lists specific screenshot filenames
- [ ] For UI tasks: Agent actually navigated to new routes (not just compiled)
- [ ] For any L3+ task: Runtime verification evidence exists in iteration log
- [ ] For UI file iterations: verification level is L3+ (L2 = automatic FAIL)
- [ ] For UI file iterations: screenshots reviewed for obvious visual problems
- [ ] For new tests: Tests call at least one method from OUR codebase (verified with grep)
- [ ] For any FAIL/PARTIAL finding: Included command output as evidence
- [ ] No patterns requiring synchronized multi-file changes
- [ ] All findings written to actionable locations (IMPLEMENTATION_PLAN.md or BACKLOG_PARKING_LOT.md)

**If ANY checkbox is unchecked and applies to the commits reviewed, the verdict cannot be PASS.**

---

## Triage: NOW vs PARK vs FIX INLINE

Every finding MUST have a disposition. There is no "flagged for awareness" category.

**NOW** (insert fix-it at top of `IMPLEMENTATION_PLAN.md`) — the agent can fix this:
- Bugs, dead code, missing tests, nitpicks, pattern violations
- Architecture violations the agent can correct
- OpenSpec synchronization issues the agent can correct

**PARK** (append to `BACKLOG_PARKING_LOT.md`) — only for items needing a human decision:
- Design decisions, scope decisions, product direction questions

**FIX INLINE** — trivial items (< 5 min) the reviewer fixes during the review itself. Commit the fix.

**If an item doesn't fit any of these, don't flag it.**

---

## Review Output: Write Directly to Plan and Parking Lot

**Every review finding MUST be written to an actionable location before the review is complete.**

**For NOW items:** Insert a fix-it block **at the very top of `IMPLEMENTATION_PLAN.md`** (before any phase or task), so it is the FIRST incomplete task the loop finds.

When the finding concerns OpenSpec drift, also write a matching actionable item to
the relevant `openspec/changes/<name>/tasks.md` file.

**For PARK items:** Append to `BACKLOG_PARKING_LOT.md` with title, source, issue, and what decision is needed.

**For FIX INLINE:** Fix during the review, commit, note in the review file.

---

## Process Improvement Authority (Reviews Can Self-Improve)

Reviews are not just read-only audits. When a review identifies a process gap, it has authority to fix certain process files **additively** (never removing existing rules).

### Autonomous Edits (No Human Approval Needed)

The review may make **additive** edits to these files:

| File | Allowed Changes |
|------|----------------|
| `.claude/skills/ralph-loop.md` | Add new gates, checklist items, mandatory steps |
| `.claude/skills/ralph-output-adversarial-review.md` | Add new review areas, hard-fail criteria, must-check items |
| `.claude/skills/testing-strategy.md` | Add new test type guidance, patterns, examples |
| `TOOLING.md` | Document new tools/capabilities discovered |

**New skill creation** is also allowed.

### Requires Human Approval

| File | Why |
|------|-----|
| `CLAUDE.md` / `AGENTS.md` | Constitution — changes affect all agents, not just RALPH |

### NEVER Allowed (Even With Human Approval Via Review)

- **Removing** existing rules, gates, or checks from any file
- **Lowering** verification levels or quality bars
- **Weakening** hard-fail criteria
- **Reducing** required evidence

The review can tighten the screws but never loosen them.

---

## Deliverable Format

### For Mid-Loop Reviews

Include: review metadata, commits reviewed, findings, verdict, open issues, and review boundary (end commit).

### For Final Postmortem

Include: review metadata, review ledger (all reviews), aggregated findings, final verdict, fix-it tasks, parked items, and skill/tooling evolution proposals.

### Actions Taken (REQUIRED at end of every review)

At the end of every review, output a structured section listing what was written where:

```markdown
## Actions Taken
- **NOW:** Task C.1 "Fix dead code in UserService" → inserted at top of IMPLEMENTATION_PLAN.md
- **PARK:** "Extract hardcoded values to config" → appended to BACKLOG_PARKING_LOT.md
- **FIX INLINE:** Removed stale import (commit abc123)
```

**Rules:**
- Every finding in the review MUST appear in this section with its disposition
- If a finding has no action, it shouldn't be in the review
