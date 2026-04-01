---
name: ralph-loop
description: RALPH loop methodology for iterative, verifiable development. Activate when user says "ralph", "run ralph", or when working through IMPLEMENTATION_PLAN.md tasks.
---

# RALPH Loop Development

RALPH (Recursive Autonomous Loop for Programming Humans) is an iterative development methodology where progress lives in files + git, not LLM context. The loop is only “done” when the correct verification has been performed and recorded.

## When to Activate

Activate this skill when:
- User says "ralph", "run ralph", "ralph loop"
- Working through tasks in `IMPLEMENTATION_PLAN.md`
- User wants autonomous iterative development

## Core Principles

1. **One task per iteration** — Pick ONE `### Task:` block, complete it fully, then stop
2. **Progress in files** — All meaningful work persists in code, docs, and git
3. **Verification is mandatory** — Done means verified, not "seems right"
4. **Bottom-up order** — Schema → Data → API → Actors → UI (unless plan explicitly overrides)
5. **Flight Recorder** — Every iteration logs decisions + verification ("log or it didn't happen")
6. **Approval gates are hard stops** — If human approval is needed, exit the loop immediately
7. **PRD gates are hard stops** — Tasks must have a valid, approved PRD reference
8. **Evolve the system** — Repeated friction becomes a skill/template/tooling note (not a bigger constitution)

---

## Flight Recorder

The flight recorder captures iteration-level decisions and verification evidence. These are **local ephemeral files** for debugging and after-action review - they are NOT committed to git.

### Location

```
.ralph/runs/{RUN_ID}/
├── run.md           # Run metadata (start time, branch, plan snapshot)
├── iter-01.md       # First iteration log
├── iter-02.md       # Second iteration log
└── ...
```

**IMPORTANT:** The `.ralph/` directory is gitignored. Do NOT commit flight recorder files. They exist for:
- Debugging failed iterations
- After-action reviews
- Context recovery if session is interrupted

### Progressive Logging (Write Early, Write Often)

**CRITICAL:** Session termination can occur at any time. Write the iteration file **IMMEDIATELY** when starting the iteration, then update it progressively. Never hold findings in context only.

#### Step 1: Create File IMMEDIATELY on Iteration Start

As soon as you pick a task, create `iter-{NN}.md` with the skeleton:

```markdown
# RALPH Iteration {N} - {Task Title}

## Status: IN_PROGRESS ← REQUIRED: Indicates incomplete iteration

## Metadata
- Run ID: {RUN_ID}
- Started: {YYYY-MM-DD HH:MM}
- Task: {Task X.Y: exact task title from IMPLEMENTATION_PLAN.md}

## Investigation Log
<!-- Append findings as discovered - DO NOT hold in context -->

## Implementation Summary
{to be filled}

## Completion
- Status: {pending}
- Finished: {timestamp when done}
- Commits: {to be filled}
```

#### Step 2: Append Discoveries Immediately

Every significant finding gets appended to the Investigation Log **before continuing**:

```markdown
## Investigation Log
- [10:15] Identified: seeded users have placeholder refresh tokens
- [10:18] Root cause: ENCRYPTED_REFRESH_TOKEN_PLACEHOLDER cannot be decrypted
- [10:22] Fix identified: set GoogleRefreshToken = null for EmailProvider.None users
```

#### Step 3: Finalize on Completion

Replace the skeleton sections with full details and change status:

```markdown
## Status: COMPLETED  ← or BLOCKED, FAILED, INCOMPLETE
```

### Iteration Log Template (Final State)

Each `iter-{NN}.md` MUST capture the following sections. Missing sections indicate incomplete logging.

```markdown
# RALPH Iteration {N} - {Task Title}

## Status: COMPLETED

## Metadata
- Run ID: {RUN_ID}
- Started: {YYYY-MM-DD HH:MM}
- Finished: {YYYY-MM-DD HH:MM}
- Task: {Task X.Y: exact task title from IMPLEMENTATION_PLAN.md}

## Surface Area
- Classification: {domain | service | endpoint | actor | UI | cross-cutting}
- Files touched: {list main files}

## Verification Level
- Level: L{0-4}
- Reason: {why this level is appropriate for the surface area}

## Skills Consulted
- {skill-name.md} - {why loaded}
- {skill-name.md} - {why loaded}
- (or "None beyond testing-strategy" if only required skill)

## PRD Validation (if applicable)
- PRD reviewed: {yes/no}
- PRD accurate: {yes/no/n/a}
- If NO: {describe discrepancy and resolution}

## Investigation Log
- [{HH:MM}] {finding or action taken}
- [{HH:MM}] {finding or action taken}

## Implementation Summary
{2-3 sentences on what was done}

## Commands Run
| Command | Outcome |
|---------|---------|
| `dotnet build` | {0 errors, 0 warnings} |
| `dotnet test` | {X passed, Y failed, Z skipped} |
| {other commands} | {outcome} |

<!-- ═══ INCLUDE THIS SECTION IF VERIFICATION LEVEL IS L3 OR L4 ═══ -->
## L3 Verification Evidence

### Screenshots Captured
- `.ralph/runs/{RUN_ID}/screenshots/iter-{NN}-1024px-{route}.png` - {description}
- `.ralph/runs/{RUN_ID}/screenshots/iter-{NN}-1280px-{route}.png` - {description}
- `.ralph/runs/{RUN_ID}/screenshots/iter-{NN}-1920px-{route}.png` - {description}

### Application Started
- Command: `aspire run`
- Outcome: {All resources healthy | describe issues}

### Routes Checked
| Route | Auth | Screenshot | Result |
|-------|------|------------|--------|
| {/route} | {/dev-login | N/A} | {screenshot filename} | {200 - rendered correctly | describe issue} |

### Console Errors
- Console errors: none
<!-- OR list actual errors — ANY error is a failure, fix before proceeding -->

### Viewport Check
- 1024px: {pass | describe issue} (screenshot: iter-{NN}-1024px-{route}.png)
- 1280px: {pass | describe issue} (screenshot: iter-{NN}-1280px-{route}.png)
- 1920px: {pass | describe issue} (screenshot: iter-{NN}-1920px-{route}.png)

### Click Interactions
- {element clicked} → {outcome}
<!-- ═══ END L3 SECTION ═══ -->

## Commits
- `{short_hash}` - {commit message}

## Issues Discovered
{List any issues found during implementation that weren't in the original task}
- Issue: {description}
  - Root cause: {why this happened}
  - Resolution: {how resolved OR "Created Task X.Y" OR "Deferred to backlog"}

## Deviations / Skips
- {any deviations from plan or standard process, with justification}
- (or "None" if followed plan exactly)

## Follow-ups (Deferred)
EVERY item below MUST have one of these dispositions or this section is INCOMPLETE:
- {item} → Task X.Y  (task already exists or created in IMPLEMENTATION_PLAN.md)
- {item} → PARKED  (added to BACKLOG_PARKING_LOT.md — needs human decision)
- {item} → DISMISSED (reason)  (not worth pursuing, document why)
- (or "None" if no follow-ups)
```

### Logging Rules

1. **"Log or it didn't happen"** — If you claim a command was run, it must appear in Commands Run with outcome
2. **"Write early, write often"** — Create iter file IMMEDIATELY on iteration start; append findings as discovered
3. **Skills must be listed** — Even if just "testing-strategy.md (required)"
4. **Issues require root cause** — Don't just note issues, explain why they occurred
5. **Commits must be listed with actual hashes** — The Commits section MUST list actual commit hashes (e.g., `abc1234 - commit message`). Never write "See git log" or similar references. The hash is required for adversarial review traceability.
6. **PRD validation** — Note if PRD was checked and whether it was accurate
7. **Status field is mandatory** — Every iter file must have `## Status:` with value: `IN_PROGRESS`, `COMPLETED`, `BLOCKED`, or `FAILED`
8. **Follow-ups require disposition** — Each follow-up item must have `→ Task X.Y`, `→ PARKED`, or `→ DISMISSED` to prevent orphaned issues

### What Gets Committed

- **DO commit:** Code changes, test changes, `IMPLEMENTATION_PLAN.md` updates
- **DO NOT commit:** `.ralph/` flight recorder files (gitignored)

---

## Bootstrap (Session Start)

Before picking a task, read:

1. **`AGENTS.md` / `CLAUDE.md`** — Constitution (authority, constraints, routing, quality bar)
2. **`PROJECT_CONTEXT.md`** — Current architecture and state (if present)
3. **`TOOLING.md`** — Available tools/services/MCP capabilities (if present)
4. **`IMPLEMENTATION_PLAN.md`** — Task breakdown and progress

If any are missing, proceed using available evidence and note assumptions in the flight recorder.

### Recovery Check (REQUIRED)

**Before starting a new iteration, check for incomplete previous work:**

1. List files in `.ralph/runs/{RUN_ID}/`
2. For each `iter-{NN}.md`, check if `## Status:` is `IN_PROGRESS` or missing "COMPLETED"
3. If incomplete iteration found:
   - Read the Investigation Log for any partial findings
   - Check git log for any changes made but not recorded
   - **Resume from where the previous session left off** - do NOT restart from scratch
   - Update the existing iter file rather than creating a new one

```bash
# Quick check for incomplete iterations
grep -l "Status: IN_PROGRESS" .ralph/runs/*/iter-*.md 2>/dev/null
```

**Why this matters:** Sessions can terminate unexpectedly. Without recovery, the next session will duplicate completed investigation work.

### NOW Item Check (HARD STOP — IMMEDIATE)

Before starting any new task, check for NOW fix-it items:

1. Search `IMPLEMENTATION_PLAN.md` for `## Fix-it` or `## NOW` sections
2. If ANY NOW items exist:
   - **STOP** — do not proceed to the next planned task
   - Pick the FIRST NOW item in document order
   - Implement and verify the fix
   - Remove the NOW item once resolved
   - Return to this check (there may be multiple NOW items)
3. **No grace period.** NOW items must be addressed before ANY other work. They are the next iteration's task, period.

**Why:** Run 20260207-010903 carried 3 NOW items for 13-23 iterations each without action. NOW items represent blocking issues that compound into technical debt when ignored.

---

## RALPH Iteration Steps

### 1) Pick Task

Read `IMPLEMENTATION_PLAN.md`, find the FIRST incomplete `### Task:` block.

A task is complete only when **ALL** its "Done when" checkboxes are satisfied.

### 1.25) Check for Already-Done Work (Fast Path)

Before starting work, check if ALL done-when criteria are already satisfied:

1. Read each "Done when" checkbox
2. For each: does the file/feature/test already exist from a prior PR or iteration?
3. If **ALL** are satisfied:
   - Mark the task complete in `IMPLEMENTATION_PLAN.md`
   - Do NOT create a separate commit — include the checkbox update in the next substantive commit
   - Log as `## Status: COMPLETED (Already Done)` with evidence (PR numbers, commit hashes)
   - Move to the next task immediately
4. **Rule:** Commits must contain substantive code changes. Do not create commits that only update `IMPLEMENTATION_PLAN.md` checkboxes.
5. **Escalation:** If 3+ consecutive tasks are already done, STOP and report: "Phase may be finished by prior work. Reassess before continuing."

**Why:** Run 20260207-010903 spent 8 of 10 Phase 10 iterations creating checkbox-only commits for work already done by PR #290.

### Task Interpretation Rules

**Done-when criteria describe the end state, not the steps.**
- If a criterion says "Service X exists with methods A, B, C" — check if the service already exists with those methods
- Don't mechanically re-implement what already exists just because a checkbox says to create it

**Phase pre-check:** Before starting a new phase, scan ALL tasks in that phase against current code. If >50% of tasks are already satisfied by prior work, report this and ask whether to skip the phase or cherry-pick remaining items.

**Task granularity:** If a single task has >8 done-when checkboxes, it may be over-specified. The agent should group related checkboxes and verify them together, not one-by-one across multiple iterations.

---

### 1.5) Verify PRD Gate (HARD STOP)

Before proceeding, verify the task has a valid PRD:

1. Check task has `**PRD:** docs/prd/{name}.md` field
2. Verify PRD file exists
3. Verify PRD status is `approved` (not `draft` or `in-review`)

**If PRD is missing, doesn't exist, or not approved:**
- EXIT the iteration immediately
- Report: "BLOCKED: Task requires approved PRD. Use /plan to create."
- Do NOT proceed to implementation

This prevents work on undefined requirements.

---

### 2) Determine Mode

From `AGENTS.md` Task Routing, pick the correct MODE for this iteration.

If the task spans modes, execute sequentially:
1) engineering → 2) design → 3) marketing (unless constitution says otherwise).

---

### 3) Check for Approval Gates (HARD STOP)

Before implementing, check whether this task requires any human approval.

Examples:
- UI changes that require visual approval
- New/changed UX flows
- Architecture decisions that are hard to reverse
- Credential setup / secrets
- Production deployment

**If an approval gate applies:**
1) Create the minimal artifact needed for human review (mockup/doc/decision record).
2) Record evidence in the flight recorder.
3) EXIT the RALPH iteration immediately (do not proceed to implementation).
4) Do not advance to further tasks until the human approval is given.

---

### 3.5) Bail Check (Every Iteration)

At the start of every iteration, check for a bail file:

```bash
test -f .ralph/runs/{RUN_ID}/BAIL.md && echo "BAIL EXISTS"
```

If the bail file exists, **EXIT the run immediately**. Do not start a new iteration.

#### When to Write BAIL.md

Write `.ralph/runs/{RUN_ID}/BAIL.md` and exit the run when:

- **Requirements ambiguity:** The task's done-when criteria are unclear and you can't determine what "done" looks like
- **Missing direction:** The task requires architectural or product decisions that haven't been made
- **Unclear scope:** You don't know whether to implement option A or option B and the plan doesn't specify
- **Blocked by human input:** The task needs information only the human can provide

**Do NOT bail for:** Infrastructure issues (retry), build failures (debug), test failures (fix). These are solvable problems, not ambiguity.

#### BAIL.md Format

```markdown
# BAIL — Ralph Run {RUN_ID}

**Iteration:** {N}
**Task:** {Task X.Y: title}
**Reason:** {Clear description of what's ambiguous or unclear}
**What I need:** {What decision or clarification from the human would unblock this}
**Work completed so far:** {Brief summary of iterations 1 through N-1}
```

#### What Happens After Bail

1. The bail file is written
2. An adversarial review runs on all work done so far (commits up to this point)
3. The review writes any fix-it items to `IMPLEMENTATION_PLAN.md`
4. The loop exits
5. Human reads BAIL.md, provides direction, and starts a new Ralph run

**Why:** Bailing early prevents slop from accumulating. If the agent doesn't have clear direction, continuing will produce low-quality work that needs to be cleaned up later. It's cheaper to stop, review, and restart with clarity.

---

### 4) Gather Context (Skills & Policies)

#### 4a) Mandatory Skill Citation Checklist (REQUIRED)

**Before writing any code**, check the Mandatory Skill Consultation Triggers table in `CLAUDE.md`.

For EACH trigger that matches your task:
1. Load the skill
2. Read the relevant section
3. **Cite it in your iteration log** under "## Skills Consulted" with the format:
   ```
   - {skill-name} (mandatory trigger: {which trigger matched})
   ```

**Even if the pattern is familiar from prior work, you MUST cite the skill.** The citation is for traceability, not learning. The adversarial review checks for these citations.

**Common triggers to check:**
- Adding/modifying configuration → `microsoft-extensions-configuration`
- Creating/modifying actors → `akka-hosting-actor-patterns`
- Adding persistence events → `extend-only-design.md`
- Creating email templates → `mjml-email-templates`
- Adding EF Core queries/migrations → `efcore-patterns`
- Blazor services using DbContext → `blazor-server-dbcontext.md`
- Configuring cluster sharding/singletons → `akka-cluster-roles.md`
- Adding actor tests → `akka-net-testing-patterns`

**Why:** Run 20260210-165314 had 43% mandatory skill citation rate. The agent applied patterns correctly but didn't cite skills, making it impossible to audit whether skills were actually consulted.

#### 4b) Load Standard Skills

Load relevant skills based on surface area:

**MANDATORY before writing any code or test (no exception):**
- `.claude/skills/testing-strategy.md` — REQUIRED for ALL code changes
- Citation format: `- testing-strategy.md (required: {brief reason, e.g. "new endpoint", "new service", "auth middleware"})`
- Purpose: adversarial traceability, not learning. Even if the test strategy is obvious, the citation is required for audit.
- **Omitting this citation is a diagnostics finding.** Run 20260401-171023 had 0/20 iterations citing this skill despite correct testing choices throughout.

**If UI or UI dependencies change OR the task is UI-related:**
- `.claude/skills/ui-smoke-validation.md` — REQUIRED

**If schema/events:**
- `.claude/skills/extend-only-design.md` — wire compatibility rules (if present)

Also consider external/marketplace skills only when relevant:
- `/dotnet-skills:slopwatch`
- `/dotnet-skills:akka-net-testing-patterns`
- `/dotnet-skills:snapshot-testing`

---

### 5) Decide Verification Level (Do Not Skip)

Before implementing, classify the task surface area and choose a Verification Level:

- **L0**: docs-only
- **L1**: pure logic changes (build + unit tests)
- **L2**: I/O coordination (DB/HTTP/actors/external) → integration tests required
- **L3**: UI changes OR UI dependency changes → L2 + manual click testing via Playwright MCP (screenshots required; no downgrade to L2 for `*.razor` / `*.css` files)
- **L4**: cross-cutting/high-risk → L3 + golden path walkthrough with screenshots

You must state:
- Surface area classification
- Verification level chosen
- Reason

---

### 5.5) L3+ Artifact Storage (MANDATORY)

If verification level is L3 or L4, screenshots MUST be saved to disk in the ralph screenshots directory.

**Directory:** `.ralph/runs/{RUN_ID}/screenshots/`
**Naming:** `iter-{NN}-{viewport}-{route-slug}.png`
  - Example: `iter-08-1280px-webhooks-add-slack.png`

#### How to Capture Screenshots

**Option A: `browser_take_screenshot` with `filename` (PREFERRED)**
Use `mcp__playwright__browser_take_screenshot` with the `filename` parameter pointing to the ralph screenshots directory:
```
filename: .ralph/runs/{RUN_ID}/screenshots/iter-08-1280px-setup-sync.png
```
This saves directly to disk AND returns the image in conversation context for verification.

**Option B: `browser_run_code` with `page.screenshot()`**
```javascript
await page.screenshot({ path: '.ralph/runs/{RUN_ID}/screenshots/iter-08-1280px-inbox.png' });
```

#### Playwright MCP Subagent Rules

When delegating Playwright work to a subagent via the Task tool:
- **Use subagent type: `playwright-gopher`** — this is the ONLY valid type for browser automation
- **Specify `model: "haiku"`** — playwright-gopher agents default to parent model (Opus) otherwise, wasting cost
- **`browser-automation` does NOT exist** — never use it as a subagent type
- **Use HTTP, not HTTPS** for local dev URLs — Playwright does not trust the ASP.NET dev certificate (`ERR_SSL_PROTOCOL_ERROR`). Use `http://localhost:5000` instead of `https://localhost:5001`.

**Why subagent rules matter:** Run 20260211-130358 iter-08 tried `subagent_type=browser-automation` (doesn't exist), then tried HTTPS (SSL error), losing two iterations before the human intervened.

#### Screenshot Persistence Rules

- Screenshots MUST be saved to `.ralph/runs/{RUN_ID}/screenshots/` — NOT to project root, NOT to `.playwright-mcp/`
- Every screenshot filename MUST be listed in the iteration log under "## L3 Verification Evidence > ### Screenshots Captured"
- If no screenshot files exist in the screenshots directory for this iteration, the L3 verification is INVALID
- Do NOT delete screenshots after viewing them — the adversarial reviewer needs persistent evidence
- After capturing, use `Read` tool to visually confirm the screenshot content before proceeding

**Why:** Run 20260207-010903 iter-30 claimed L4 with zero artifacts on disk. Run 20260211-130358 iter-08 captured screenshots to the wrong location and then deleted them.

---

### 5.75) UI File Gate (MANDATORY)

**If ANY file touched in this iteration matches `*.razor` or `*.css`**, the following rules apply:

1. **Screenshots are MANDATORY** — verification level MUST be L3 or higher
2. **No L3→L2 downgrade for UI work** — this is a hard policy, not a suggestion
3. **If Aspire can't start:** the iteration is **BLOCKED**, not COMPLETED. Do not commit code without visual verification.

#### L3 Deferral for Unintegrated Components

If a new component is created (`*.razor`) but **cannot be visually tested** because it isn't integrated into any page yet, L3 may be deferred to the integration iteration — provided:

1. The **same RALPH run** has a subsequent task that integrates the component into a page
2. That integration task performs full L3 verification covering both the component and its styling
3. The creation iteration is marked **BLOCKED on L3** (not COMPLETED) until the integration iteration's L3 passes

If no integration task follows in the current run, the component must be integrated into a test page for L3 in the same iteration.

#### Comparison Rules

| Scenario | Screenshots Required? |
|----------|----------------------|
| `*.razor` + backend code | YES (for UI parts) |
| CSS-only changes | YES (CSS is visual by definition) |
| Shared component change | YES, primary page + document which pages use it |
| Layout component change | YES, at least 2 pages using the layout |
| `.razor.cs` only (no markup change) | Only if rendering behavior changes |
| New component, not yet integrated | DEFERRED (see L3 Deferral above) |

**With mockup:** Compare screenshots against `docs/design/mockups/` reference.
**Without mockup:** Existence proof — the page renders, is interactive, and has no console errors.

**Why:** RALPH run 20260210-165314 built 15 iterations of account deletion UI and never launched the app. The agent wrote E2E tests as a proxy for verification. Those tests were never executed. Screenshots force the agent to actually run the app.

---

### 6) Implement

Follow architecture rules from the constitution:
- service layer pattern
- endpoints thin
- UI thin
- actors not called directly by endpoints
- value objects preferred

---

### 7) Verify (Must Match Chosen Level)

Minimum quality bar:

```bash
dotnet build   # 0 errors, 0 warnings
dotnet test    # all pass
```

**Level-specific requirements:**

| Level | Required Verification |
|-------|----------------------|
| L0 | Docs build/render correctly |
| L1 | Build + unit tests pass |
| L2 | Build + unit + integration tests pass |
| L3 | L2 + UI smoke test (see L3 Verification Checklist) |
| L4 | L3 + golden path walkthrough with screenshots |

---

### L3 Verification Checklist (MANDATORY for UI Tasks)

**CRITICAL:** If you claim L3 verification level, you MUST complete ALL of the following.
Deferring L3 verification to "follow-up" or "can be performed manually later" is **FORBIDDEN**.

**FORMAT REQUIREMENT:** Your iteration log MUST use the `## L3 Verification Evidence` template from the Iteration Log Template above (the section between `═══ INCLUDE THIS SECTION IF VERIFICATION LEVEL IS L3 OR L4 ═══` markers). The `ralph.sh` L3 gate greps for these **exact patterns** — freeform prose will not pass:
- `Routes Checked` (subsection header — NOT buried in a table or bullet list)
- `Console errors: none` (literal string — or `Console errors:` with documented errors)
- `1024px` AND `1280px` AND `1920px` (all three viewports — one is not enough)

If your L3 section doesn't contain these exact strings, **the automated gate will reject the iteration**.

#### Required Evidence

Your iteration log MUST include explicit evidence of each item:

1. **Application Running**
   - `aspire run` command ran (or fallback documented per TOOLING.md)
   - Wait for health check: "api" resource healthy
   - Log the command and outcome

2. **Routes Navigated**
   - List specific routes checked (e.g., `/webhooks/add/slack`, `/webhooks/add/discord`)
   - For authenticated routes: used `/dev-login` first
   - Confirm each route rendered without 404/500

3. **Console Errors Checked**
   - Open browser dev tools (F12)
   - Navigate to each impacted route
   - Report: "Console errors: none" OR list actual errors
   - **ANY console error is a failure** - fix before proceeding

4. **Viewport Check (ALL THREE REQUIRED)**
   - Resize browser and screenshot at 1024px, 1280px, AND 1920px
   - No broken layouts, hidden CTAs, or content overflow
   - Record each viewport result individually (the gate checks for all three width strings)

5. **Click Interactions (for new/changed UI)**
   - Test primary interactions: buttons, links, modals, form submissions
   - Record what was clicked and outcome

#### If L3 Cannot Be Performed

**Non-UI tasks** (no `*.razor` or `*.css` files touched):
1. Downgrade to L2 is allowed with explicit justification
2. Add a follow-up task: "L3 verification for [feature]" with `→ Task X.Y`
3. Do NOT claim L3 while deferring the actual verification

**UI tasks** (`*.razor` or `*.css` files touched):
1. **Downgrade is FORBIDDEN** — verification level stays L3
2. If infrastructure prevents verification, the iteration status is **BLOCKED**, not COMPLETED
3. Do NOT commit code changes. The iteration is incomplete.
4. Document the blocker in the flight recorder and fix the infrastructure issue before proceeding

This prevents "checkbox fraud" where L3 is claimed without evidence, and prevents UI code from shipping without visual verification.

#### L3 Section Format

**Use the `## L3 Verification Evidence` template from the Iteration Log Template** (search for `═══ INCLUDE THIS SECTION IF VERIFICATION LEVEL IS L3 OR L4 ═══`). Copy the subsection structure exactly — do NOT write freeform prose instead of the structured sections.

**Note:** Every screenshot filename must be saved to `.ralph/runs/{RUN_ID}/screenshots/` using `browser_take_screenshot` with `filename` parameter or `browser_run_code` with `page.screenshot({path: ...})`. Claiming L3 without persistent artifacts is verification fraud.

**Why this is strict:** Run 20260227-044734 iter-01 wrote L3 evidence as freeform bullets instead of using the required subsection headers (`### Routes Checked`, `### Console Errors`, `### Viewport Check`). The `ralph.sh` automated gate grep'd for those exact strings and rejected the iteration. The agent did the work but logged it in the wrong format.

---

### 8) Record Evidence (Flight Recorder)

Update `$ITER_LOG` with:
- All sections from the iteration log template
- Commands run with outcomes
- If L3: Include complete L3 Verification Evidence section

---

### 9) Commit

If verification passes:

#### 9a) Pre-Commit Log Compliance Check (30 seconds — MANDATORY)

Before committing, verify `iter-{NN}.md` contains ALL of:
- [ ] `## Status: COMPLETED` (exact spelling — not COMPLETE, Complete, etc.)
- [ ] `## Commits` section (hash placeholder OK — will be filled in 9c)
- [ ] `## PRD Validation` section (even if just "N/A" or "N/A — fix-it task")
- [ ] `## Skills Consulted` including `testing-strategy.md (required: ...)`
- [ ] `## Follow-ups` with explicit `→ Task X.Y` / `→ PARKED` / `→ DISMISSED` on every item

If any section is missing, add it before committing. This takes 30 seconds and prevents the structural gaps found in run 20260401-171023 (10 of 20 iterations missing required sections).

#### 9b) Commit

1. Commit code changes with descriptive message
2. Include IMPLEMENTATION_PLAN.md checkbox updates in same commit
3. Update TOOLING.md if new tools/resources were discovered

#### 9c) Post-Commit Hash Capture (MANDATORY)

Immediately after `git commit` succeeds:
1. Run `git log --oneline -1` to get the short hash
2. Update the `## Commits` section in `iter-{NN}.md` with the actual hash:
   ```markdown
   ## Commits
   - `abc1234` - feat(feature): commit message here
   ```
3. This is NOT optional — commit hashes are required for adversarial review traceability. Run 20260401-171023 had 10 of 20 iterations missing commit hashes because they were never captured post-commit.

---

### 10) Check PRD Completion

After completing a task, check if it completes a PRD:

1. Read the task's `**PRD:**` reference
2. Check `IMPLEMENTATION_PLAN.md` - are ALL tasks for that PRD complete?
3. If complete:
   - Update PRD frontmatter: `status: implemented`, `implemented: {date}`
   - Update `docs/prd/INDEX.md`: move to "Implemented PRDs" section
   - Log in flight recorder: "PRD completed: {name}"

### 11) Write Parked Items to BACKLOG_PARKING_LOT.md

If your iteration log contains any follow-ups marked `→ PARKED`:

1. Open `BACKLOG_PARKING_LOT.md`
2. Append each item under "## Items Awaiting Decision" with: title, source (iteration/task), issue description, decision needed, date
3. Update your iteration log to reference the parking lot entry
4. **This is NOT optional** — all PARKED items must be written in the same iteration they're identified

**Why:** Run 20260207-010903 had 15+ items marked PARKED in reviews that never reached BACKLOG_PARKING_LOT.md.

### 12) Check Archive Need

After completing a phase:

1. Is `IMPLEMENTATION_PLAN.md` > 2000 lines?
2. Are there 3+ completed phases?
3. If yes to either, suggest: `/archive-completed`

### 13) Exit

Stop after ONE task. Do not continue to additional tasks.

---

## PRD Gate (HARD STOP)

**Before implementing any task, verify its PRD reference:**

1. Check task has `**PRD:** docs/prd/{name}/README.md` field
2. Verify the PRD file exists at that path
3. Check PRD frontmatter `status` is `approved` (not `draft`)

**If PRD is missing or draft:**
```
BLOCKED: Task X.Y requires approved PRD

PRD reference: {path}
Status: {missing | draft}

Action required:
- Use /plan to create or complete the PRD
- Get PRD approved before implementing

EXIT ITERATION - Do not proceed without approved PRD.
```

**Why this matters:**
- Prevents implementing features without clear requirements
- Ensures business context and success metrics are defined
- Creates audit trail from requirement → implementation

---

## Approval Gates Reminder

The following require human approval before proceeding:

| Gate | Why |
|------|-----|
| UI visual changes | Human must review appearance |
| Architecture decisions | Hard to reverse |
| Credential/secret setup | Security-sensitive |
| Production deployment | Requires infrastructure access |
| New mockup created | Human must approve design |

At an approval gate: present summary, exit iteration, wait for explicit approval