---
name: ralph-loop
description: RALPH loop methodology for iterative, verifiable development. Activate when user says "ralph", "run ralph", or when working through IMPLEMENTATION_PLAN.md tasks.
---

# RALPH Loop Development

RALPH (Recursive Autonomous Loop for Programming Humans) is an iterative development methodology where progress lives in files + git, not LLM context. The loop is only "done" when the correct verification has been performed and recorded.

## When to Activate

Activate this skill when:
- User says "ralph", "run ralph", "ralph loop"
- Working through tasks in `IMPLEMENTATION_PLAN.md`
- User wants autonomous iterative development

## Core Principles

1. **One task per iteration** — Pick ONE `### Task:` block, complete it fully, then stop
2. **Progress in files** — All meaningful work persists in code, docs, and git
3. **Verification is mandatory** — Done means verified, not "seems right"
4. **Bottom-up order** — Schema/Data → API → Services → UI (unless plan explicitly overrides)
5. **Flight Recorder** — Every iteration logs decisions + verification ("log or it didn't happen")
6. **Approval gates are hard stops** — If human approval is needed, exit the loop immediately
7. **Traceability gates are hard stops** — Tasks must reference PRD + OpenSpec artifacts
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

## Status: IN_PROGRESS

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

## Traceability Validation
- PRD reviewed: {yes/no}
- OpenSpec capability reviewed: {yes/no}
- OpenSpec change reviewed: {yes/no/n/a}
- If NO: {describe discrepancy and resolution}

## Investigation Log
- [{HH:MM}] {finding or action taken}
- [{HH:MM}] {finding or action taken}

## Implementation Summary
{2-3 sentences on what was done}

## Commands Run
| Command | Outcome |
|---------|---------|
| `{build command}` | {0 errors, 0 warnings} |
| `{test command}` | {X passed, Y failed, Z skipped} |
| {other commands} | {outcome} |

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
- {item} → {disposition: "Task C.X" | "PARKED" | "DISMISSED (reason)"}
- (or "None")

Note: Each follow-up MUST have an explicit disposition:
- **→ Task X.Y**: Create a new task in IMPLEMENTATION_PLAN.md
- **→ PARKED**: Add to BACKLOG_PARKING_LOT.md (needs human decision)
- **→ DISMISSED (reason)**: Not worth pursuing, document why
```

### Logging Rules

1. **"Log or it didn't happen"** — If you claim a command was run, it must appear in Commands Run with outcome
2. **"Write early, write often"** — Create iter file IMMEDIATELY on iteration start; append findings as discovered
3. **Skills must be listed** — Even if just "testing-strategy.md (required)"
4. **Issues require root cause** — Don't just note issues, explain why they occurred
5. **Commits must be listed** — Include hash and message for traceability
6. **Traceability validation** — Note if PRD + OpenSpec references were checked
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
5. **`openspec/specs/README.md`** — Capability inventory and naming

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

### Task Interpretation Rules

**Done-when criteria describe the end state, not the steps.**
- If a criterion says "Service X exists with methods A, B, C" — check if the service already exists with those methods
- Don't mechanically re-implement what already exists just because a checkbox says to create it

**Phase pre-check:** Before starting a new phase, scan ALL tasks in that phase against current code. If >50% of tasks are already satisfied by prior work, report this and ask whether to skip the phase or cherry-pick remaining items.

**Task granularity:** If a single task has >8 done-when checkboxes, it may be over-specified. The agent should group related checkboxes and verify them together, not one-by-one across multiple iterations.

---

### 1.5) Verify Traceability Gate (HARD STOP)

Before proceeding, verify the task has references to planning artifacts:

1. `**PRD:**` field with one or more `docs/prd/*.md` files
2. `**OpenSpec Capabilities:**` field with one or more
   `openspec/specs/*/spec.md` files
3. Optional but preferred: `**OpenSpec Changes:**` field with one or more
   `openspec/changes/*/` directories
4. Verify all referenced paths exist

**If required references are missing or invalid:**
- EXIT the iteration immediately
- Report: "BLOCKED: Task missing PRD/OpenSpec traceability references."
- Do NOT proceed to implementation

This prevents implementation drift from the approved planning baseline.

---

### 2) Determine Mode

From `AGENTS.md` / `CLAUDE.md` Task Routing, pick the correct MODE for this iteration.

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

---

### 4) Gather Context (Skills & Policies)

#### 4a) Mandatory Skill Citation Checklist (REQUIRED)

**Before writing any code**, check the Mandatory Skill Consultation Triggers table in `CLAUDE.md` (if present).

For EACH trigger that matches your task:
1. Load the skill
2. Read the relevant section
3. **Cite it in your iteration log** under "## Skills Consulted" with the format:
   ```
   - {skill-name} (mandatory trigger: {which trigger matched})
   ```

**Even if the pattern is familiar from prior work, you MUST cite the skill.** The citation is for traceability, not learning. The adversarial review checks for these citations.

**Why:** Mandatory skill citation rate can drop below 50% without this rule. The agent applies patterns correctly but doesn't cite skills, making it impossible to audit whether skills were actually consulted.

#### 4b) Load Standard Skills

Load relevant skills based on surface area:

**Always for code:**
- `testing-strategy.md` — if present: unit vs integration test decisions

**If UI or UI dependencies change:**
- `ui-smoke-validation.md` — if present: UI verification requirements

**If schema/events:**
- `extend-only-design.md` — wire compatibility rules (if present)

---

### 5) Decide Verification Level (Do Not Skip)

Before implementing, classify the task surface area and choose a Verification Level:

- **L0**: docs-only
- **L1**: pure logic changes (build + unit tests)
- **L2**: I/O coordination (DB/HTTP/actors/external) → integration tests required
- **L3**: UI changes OR UI dependency changes → L2 + manual click testing via Playwright MCP (screenshots required; no downgrade to L2 for UI files)
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
  - Example: `iter-08-1280px-settings-page.png`

#### Playwright MCP Subagent Rules

When delegating Playwright work to a subagent via the Task tool:
- **Use subagent type: `playwright-gopher`** — this is the ONLY valid type for browser automation
- **Specify `model: "haiku"`** — playwright-gopher agents default to parent model otherwise, wasting cost
- **`browser-automation` does NOT exist** — never use it as a subagent type
- **Use HTTP, not HTTPS** for local dev URLs if Playwright does not trust the dev certificate

#### Screenshot Persistence Rules

- Screenshots MUST be saved to `.ralph/runs/{RUN_ID}/screenshots/` — NOT to project root
- Every screenshot filename MUST be listed in the iteration log under "## L3 Verification Evidence > ### Screenshots Captured"
- If no screenshot files exist in the screenshots directory for this iteration, the L3 verification is INVALID
- Do NOT delete screenshots after viewing them — the adversarial reviewer needs persistent evidence
- After capturing, use `Read` tool to visually confirm the screenshot content before proceeding

---

### 5.75) UI File Gate (MANDATORY)

**If ANY file touched in this iteration is a UI file** (e.g., `*.razor`, `*.css`, `*.tsx`, `*.vue`, `*.svelte`, `*.html`), the following rules apply:

1. **Screenshots are MANDATORY** — verification level MUST be L3 or higher
2. **No L3→L2 downgrade for UI work** — this is a hard policy, not a suggestion
3. **If the app can't start:** the iteration is **BLOCKED**, not COMPLETED. Do not commit code without visual verification.

#### L3 Deferral for Unintegrated Components

If a new component is created but **cannot be visually tested** because it isn't integrated into any page yet, L3 may be deferred to the integration iteration — provided:

1. The **same RALPH run** has a subsequent task that integrates the component into a page
2. That integration task performs full L3 verification covering both the component and its styling
3. The creation iteration is marked **BLOCKED on L3** (not COMPLETED) until the integration iteration's L3 passes

---

### 6) Implement

Follow architecture rules from the constitution (AGENTS.md / CLAUDE.md):
- service layer patterns
- endpoints thin
- UI thin
- value objects preferred

---

### 7) Verify (Must Match Chosen Level)

Minimum quality bar:

```bash
# Use project-appropriate build + test commands
# Examples: dotnet build && dotnet test, npm run build && npm test, cargo build && cargo test
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

#### Required Evidence

Your iteration log MUST include explicit evidence of each item:

1. **Application Running**
   - Start command ran (per project: `aspire run`, `npm start`, `cargo run`, etc.)
   - Wait for health check or startup confirmation
   - Log the command and outcome

2. **Routes Navigated**
   - List specific routes checked
   - For authenticated routes: used dev auth method
   - Confirm each route rendered without errors

3. **Console Errors Checked**
   - Open browser dev tools
   - Navigate to each impacted route
   - Report: "Console errors: none" OR list actual errors
   - **ANY console error is a failure** - fix before proceeding

4. **Viewport Sanity (spot check)**
   - Check layout at 1024px, 1280px, 1920px
   - No broken layouts, hidden CTAs, or content overflow

5. **Click Interactions (for new/changed UI)**
   - Test primary interactions: buttons, links, modals, form submissions
   - Record what was clicked and outcome

---

### 8) Record Evidence (Flight Recorder)

Update `$ITER_LOG` with all sections from the iteration log template.

---

### 9) Commit

If verification passes:
1. Commit code changes with descriptive message
2. Include IMPLEMENTATION_PLAN.md checkbox updates in same commit
3. Update referenced `openspec/changes/*/tasks.md` checkboxes in same commit
4. Run `openspec validate --all --no-interactive` and record result
5. Update TOOLING.md if new tools/resources were discovered

---

### 10) Check OpenSpec Change Progress

After completing a task, check whether linked OpenSpec changes are ready to
advance:

1. Read task's `**OpenSpec Changes:**` references
2. Update task checkboxes inside each referenced
   `openspec/changes/<name>/tasks.md`
3. Run `openspec status --change <name>` for each linked change
4. If all artifacts are complete and implementation for that change is done,
   note that the change is ready for `openspec archive <name>`

### 11) Write Parked Items to BACKLOG_PARKING_LOT.md

If your iteration log contains any follow-ups marked `→ PARKED`:

1. Open `BACKLOG_PARKING_LOT.md`
2. Append each item under "## Items Awaiting Decision" with: title, source (iteration/task), issue description, decision needed, date
3. Update your iteration log to reference the parking lot entry
4. **This is NOT optional** — all PARKED items must be written in the same iteration they're identified

### 12) Check Archive Need

After completing a phase:

1. Is `IMPLEMENTATION_PLAN.md` > 2000 lines?
2. Are there 3+ completed phases?
3. If yes to either, suggest: `/archive-completed`

### 13) Exit

Stop after ONE task. Do not continue to additional tasks.

---

## OpenSpec Traceability Gate (HARD STOP)

**Before implementing any task, verify traceability references:**

1. Task has `**PRD:** docs/prd/*.md`
2. Task has `**OpenSpec Capabilities:** openspec/specs/*/spec.md`
3. Referenced files exist

**If required references are missing:**
```
BLOCKED: Task X.Y requires PRD + OpenSpec references

PRD reference: {path or missing}
OpenSpec capability: {path or missing}

Action required:
- Update IMPLEMENTATION_PLAN.md task metadata with valid references
- Create missing OpenSpec artifacts with /opsx:new or /opsx:continue

EXIT ITERATION - Do not proceed without traceability.
```

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
