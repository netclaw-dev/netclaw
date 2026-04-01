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

**For the FIRST mid-loop review — pre-run commit check:**
If the branch has commits before the run start commit (i.e., `git log {default-branch}..{run_start_commit}` returns commits), include those pre-run commits in your first review scope. Use `{default-branch}..{current_HEAD}` as the range instead. This prevents foundational code from going unreviewed.

**Why:** Run 20260224-233039 had 4 pre-run commits (5,773 lines) that built the entire OAuth foundation but were never adversarially reviewed because each mid-loop review only started from the run start commit.

**For final postmortem:**
1. Same as above - find the last mid-loop review's end commit
2. Your review range: `{last_review_end_commit}..{current_HEAD}`
3. Also: summarize all prior reviews (don't re-review, just aggregate)

**If reviewing after a BAIL:**
- The run was terminated early due to ambiguity
- Review all commits from run start to the bail point
- Pay extra attention to the last 1-2 iterations (work done while the agent was uncertain)
- Write fix-its to `IMPLEMENTATION_PLAN.md` as usual
- Note the bail reason in the review header

### Step 2: Read Prior Reviews for Context

For each prior `review-after-iter-*.md`, extract:
- Commit range reviewed
- Verdict (PASS/PARTIAL/FAIL)
- Issues flagged (especially PARTIAL/FAIL items)
- Whether issues were marked as "needs follow-up"

Build a **review ledger**:

```markdown
## Review Ledger

| Review | Iterations | Commit Range | Verdict | Open Issues |
|--------|------------|--------------|---------|-------------|
| review-after-iter-03 | 1-3 | d551b45..c026a96 | PASS | None |
| review-after-iter-07 | 4-7 | c026a96..489d11b | PARTIAL | Missing cluster role config |
| THIS REVIEW | 8-10 | 489d11b..ed2c8d9 | ? | TBD |
```

### Step 3: Handle Open Issues from Prior Reviews

If prior reviews flagged issues:

**PARTIAL issues:** Check if subsequent commits addressed them
- If YES: Note "Resolved in commit {hash}" - no need to re-flag
- If NO: Carry forward as "Previously flagged, still unresolved"

**FAIL issues:** The loop should have paused. If you're reviewing anyway:
- These MUST be verified as fixed before continuing
- If not fixed: Re-flag as FAIL with "Blocking issue not resolved"

### Step 4: Write Your Review Header

Every review file MUST start with:

```markdown
# Adversarial Review - {type}

## Review Metadata
- Run ID: {run_id}
- Review Type: mid-loop | postmortem
- Iteration(s) Covered: {N} to {M}
- Commit Range: {start}..{end}
- Prior Reviews: {count} ({list verdicts})

## Prior Review Summary
{If mid-loop: list any open issues being tracked}
{If postmortem: aggregate all prior reviews}
```

### Step 5: Record Your End Commit

At the end of your review, record:

```markdown
## Review Boundary
- End Commit: {commit_hash}
- Next review should start from: {commit_hash}
```

This allows the next review (or postmortem) to know where to start.

---

## Required Skills to Load

Before reviewing, **load these skills** to have the correct quality bar:

| Skill | Load Command | Review Area |
|-------|--------------|-------------|
| `testing-strategy` | Local skill | Test coverage, integration vs unit |
| `extend-only-design` | Local skill | Schema/persistence compatibility |
| `akka-hosting-actor-patterns` | `/dotnet-skills:akka-hosting-actor-patterns` | Actor registration, sharding config, extractors |
| `akka-net-best-practices` | `/dotnet-skills:akka-net-best-practices` | Supervision, error handling, pub/sub |
| `slopwatch` | `/dotnet-skills:slopwatch` | Reward hacking detection |

**Conditional skills** (load if relevant changes exist):
| Skill | When to Load |
|-------|--------------|
| `akka-net-testing-patterns` | If actor tests added/modified |
| `akka-cluster-roles` | If cluster sharding/singletons configured |
| `blazor-server-dbcontext` | If Blazor services use DbContext |
| `ui-smoke-validation` | If UI changes made |

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

### B) Testing Strategy Compliance
Using `testing-strategy`:
- If code coordinates DB/HTTP/actors/external services:
  - Integration tests must exist and validate real behavior
- If endpoints added/changed:
  - Integration tests must cover request/response behavior
- If UI changed or UI dependencies changed:
  - Screenshots in `.ralph/runs/<run-id>/screenshots/` must exist
- Auth in tests:
  - Must use `/dev-login` (never real OAuth)

### C) Architecture / Layering Compliance
Check for:
- endpoints calling actors directly when service layer is required
- business logic in UI layer
- missing DI registration or misconfigured services
- duplication or obvious smell introduced

### D) Akka.NET Compliance (if actors touched)
Using `akka-hosting-actor-patterns` and `akka-net-best-practices`:

**Cluster Sharding:**
- `RememberEntities` should be `false` unless explicitly justified (causes recovery overhead)
- Shard regions should be registered ONCE via `WithShardRegion`, not duplicated with `IRequiredActor`
- Message extractors must be deterministic and consistent
- Cluster roles configured for all shards/singletons (see `akka-cluster-roles` skill)

**Actor Patterns:**
- Props created via `DependencyResolver` or `IRequiredActor`, never `Props.Create<T>()`

**Persistence (if applicable):**
- Events/snapshots follow extend-only design (load `extend-only-design` skill)
- Serializer bindings registered for all persisted types
- Recovery tested with existing data

### E) UI Sanity (if applicable)
If UI was impacted:
- Are there likely runtime errors? (null states, missing guards)
- Are empty/error/loading states handled?
- Would a user journey plausibly break?
- **Are there screenshots in `.ralph/runs/<run-id>/screenshots/`?** (L3 evidence)
- Is there documented manual click testing in the iteration log?

**Note:** We do NOT check for Playwright test code. Instead, verify screenshots were taken during manual click testing.

### E2) UI Screenshot Gate Enforcement

For each iteration that touched `*.razor` or `*.css` files, the reviewer MUST:

1. **Detect which iterations touched UI files** — scan `git diff` for `*.razor`/`*.css` changes
2. **Verify verification level is L3+** — L2 for UI work is an automatic **FAIL**
3. **Verify screenshot files exist on disk** in `.ralph/runs/{RUN_ID}/screenshots/`
4. **Actually read the screenshot images** and check for obvious visual problems (broken layouts, empty pages, error screens)
5. **Check mockup comparison** if the task's done-when criteria specify it

**Hard fail criterion:** `*.razor`/`*.css` modified + (verification < L3 OR no screenshots on disk) = **FAIL**

### F) Regression Risk
- missing negative tests / edge cases
- N+1 query patterns
- unsafe assumptions
- telemetry gaps (if required for launch)

### G) Slopwatch (Mandatory)
**Run `/dotnet-skills:slopwatch` on files changed in YOUR commit range.**

Check for reward hacking patterns:
- Disabled or skipped tests (`[Fact(Skip=...)]`, `#if false`)
- Suppressed warnings (`#pragma warning disable`, `[SuppressMessage]`)
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

**Why:** Claiming verification without doing it is checkbox fraud. It makes the review
ledger unreliable and allows broken code to ship.

### False Positive Prevention (MANDATORY)

Before flagging ANY finding as FAIL or PARTIAL, you MUST verify the issue exists with evidence.

**Before flagging a file as missing:**
- Use `Glob` or `ls` to verify the file doesn't exist on disk
- File naming may differ from what you expect — check for alternate names

**Before flagging imports/usings as dead code:**
- Search for type names in the codebase (not just direct references)
- Types resolved at runtime (DI, reflection, Akka actor instantiation) won't show as direct references

**Every FAIL/PARTIAL finding must include:**
- The specific command/search you ran to verify the issue
- The output of that command
- No finding without evidence

**Why:** Run 20260207-010903 had 2 false positives: PurchaseReceipt.mjml flagged as missing (existed under different name) and Services.Gmail imports flagged as dead code (used by DI-registered types).

### Missing Required Tests

**PARTIAL minimum if:**
- New UI components added without screenshots
- New endpoints added without integration tests
- New services with I/O added without integration tests

**Why:** Testing strategy requires integration tests for I/O coordination and E2E for UI.

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

**Examples that trigger this:**
- 3+ files with structurally identical modal/form/list patterns
- Copy-pasted method bodies that would need synchronized updates
- Repeated inline styling that should be CSS classes
- Same validation logic duplicated across multiple components
- Identical error handling patterns in multiple places

**Acceptable copy-paste (don't flag):**
- Boilerplate (using statements, standard DI registration patterns)
- Test setup code that's intentionally isolated
- Configuration that varies per-environment
- Scaffolded code that will diverge by design

**How to detect:**
1. For each file modified, search for similar patterns in other files
2. If 70%+ structural similarity exists, flag for extraction
3. Check: "If I found a bug in this pattern, how many files would I need to fix?"

### I) Useless Tests

Flag as **PARTIAL** when tests provide no actual coverage of OUR code.

**Examples that trigger this:**
- Testing external SDK behavior (Stripe, HttpClient, EF Core primitives)
- Tests that just verify mocks return what they were configured to return
- Tests with no meaningful assertions (just "doesn't throw")
- Tests that verify framework guarantees (DI resolution, serialization of BCL types)
- Tests that duplicate what the compiler already guarantees
- High test count but tests don't exercise our business logic

**What we SHOULD test:**
- Our business logic given various inputs
- Our error handling when external services fail (mock the failure, test our response)
- Our state transitions and side effects
- Integration between our components

**Rule:** "Test YOUR code's behavior, not the libraries you depend on."

**Example of useless test:**
```csharp
// BAD: Tests Stripe SDK, not our code
[Fact]
public async Task StripeClient_CanGetSubscription()
{
    var subscription = await _stripeClient.GetSubscriptionAsync("sub_123");
    Assert.NotNull(subscription); // Just tests that Stripe SDK works
}
```

**Example of useful test:**
```csharp
// GOOD: Tests OUR code's behavior when Stripe returns data
[Fact]
public async Task HandleWebhook_CreatesSubscriptionRecord_WhenStripeReportsCheckoutComplete()
{
    // Arrange: Stripe sends checkout.session.completed
    var webhookEvent = CreateStripeEvent("checkout.session.completed", ...);

    // Act: OUR code handles it
    await _webhookService.HandleEventAsync(webhookEvent);

    // Assert: OUR code created the right record in OUR database
    var subscription = await _dbContext.Subscriptions.FirstOrDefaultAsync(s => s.StripeSubscriptionId == "sub_123");
    Assert.NotNull(subscription);
    Assert.Equal(SubscriptionStatus.Active, subscription.Status);
}
```

### J) L3 Evidence Audit

For any task with L3/L4 verification level, explicitly verify:

1. **Did the agent run the application?**
   - Look for `aspire run` or equivalent command with outcome
   - If missing: FAIL

2. **Did the agent navigate to the routes?**
   - Look for "Routes Checked" or equivalent section with specific URLs
   - If missing: FAIL for L3 tasks

3. **Did the agent check console errors?**
   - Look for "Console errors: none" or documented errors
   - If missing: PARTIAL minimum

4. **Is there runtime evidence, not just compile-time?**
   - Unit/integration tests passing is L2
   - L3 requires evidence of UI actually rendering

### K) L3+ Artifact Verification (MANDATORY)

For any task claiming L3 or L4 verification, verify that screenshot artifacts exist on disk.

1. **Check the screenshots directory:**
   ```bash
   ls -la .ralph/runs/{RUN_ID}/screenshots/iter-{NN}-*.png
   ```

2. **FAIL if:**
   - L3/L4 claimed but screenshots directory is empty or no files match the iteration
   - Screenshot filenames listed in iter log but files don't exist on disk
   - Iteration log says "took screenshots" but provides no filenames

3. **PARTIAL if:**
   - Screenshots exist but aren't referenced in iteration log
   - Fewer screenshots than expected (e.g., only 1 viewport instead of 3)

**Why FAIL not PARTIAL:** Claiming evidence that doesn't exist is verification fraud.

---

## Must-Check Section

Before issuing a PASS verdict, explicitly confirm:

- [ ] For any L3+ task: Screenshot files exist in `.ralph/runs/{RUN_ID}/screenshots/` (verified with ls/Glob)
- [ ] For any L3+ task: Iteration log lists specific screenshot filenames (not just "screenshot taken")
- [ ] For UI tasks: Agent actually navigated to new routes (not just compiled)
- [ ] For any L3+ task: Runtime verification evidence exists in iteration log
- [ ] For `*.razor`/`*.css` iterations: verification level is L3+ (L2 = automatic FAIL)
- [ ] For `*.razor`/`*.css` iterations: screenshots reviewed for obvious visual problems
- [ ] For new tests: Tests call at least one method from OUR codebase (verified with grep)
- [ ] For any FAIL/PARTIAL finding: Included command output as evidence
- [ ] No patterns requiring synchronized multi-file changes
- [ ] All findings written to actionable locations (IMPLEMENTATION_PLAN.md or BACKLOG_PARKING_LOT.md)
- [ ] Iteration logs in review range have `## Status: COMPLETED` (exact spelling), commit hashes, and `testing-strategy.md` citation
- [ ] All follow-up items in review range have explicit `→` dispositions (Task X.Y / PARKED / DISMISSED)

**If ANY checkbox is unchecked and applies to the commits reviewed, the verdict cannot be PASS.**

---

## Triage: NOW vs PARK vs FIX INLINE

Every finding MUST have a disposition. There is no "flagged for awareness" category.

**NOW** (insert fix-it at top of `IMPLEMENTATION_PLAN.md`) — the agent can fix this:
- Bugs, dead code, missing tests, nitpicks, pattern violations
- Architecture violations the agent can correct
- Anything the review flagged that has a clear fix

**PARK** (append to `BACKLOG_PARKING_LOT.md`) — only for items needing a human decision on something non-blocking:
- "Should we extract hardcoded brand colors to config?" (design call)
- "The PRD doesn't mention rate limiting. Worth adding?" (scope decision)
- "Support a 3rd webhook format not in PRD?" (product direction)

**FIX INLINE** — trivial items (< 5 min) the reviewer fixes during the review itself. Commit the fix.

**If an item doesn't fit any of these, don't flag it.**

---

## Review Output: Write Directly to Plan and Parking Lot

**Every review finding MUST be written to an actionable location before the review is complete.**

**For NOW items:** Insert a fix-it block **at the very top of `IMPLEMENTATION_PLAN.md`** (before any phase or task), so it is the FIRST incomplete task the loop finds:

```markdown
## Fix-it (Review after iter-{N}) — NOW

### Task C.{N}: {title}
**Source:** Review after iteration {N}, finding #{X}
**Issue:** {description}
**Done when:**
- [ ] {objective acceptance criterion}
**Verification:** L{level}
```

**Positioning matters.** The ralph-loop picks "the FIRST incomplete task" from the plan. By inserting fix-its at the top, they become the next thing the agent works on — guaranteed.

**For PARK items:** Append to `BACKLOG_PARKING_LOT.md` with title, source, issue, and what decision is needed.

**For FIX INLINE:** Fix during the review, commit, note in the review file.

**There is no "flagged for awareness" category.** Every finding has exactly one disposition:
- **NOW** → written to `IMPLEMENTATION_PLAN.md`, fixed next iteration
- **PARK** → written to `BACKLOG_PARKING_LOT.md`, needs human decision
- **FIX INLINE** → fixed during review
- **BAIL** → current task ambiguous → write `BAIL.md`, trigger review, exit run

**Why:** Run 20260207-010903 had 15+ flagged items that sat in review files without reaching `IMPLEMENTATION_PLAN.md` or `BACKLOG_PARKING_LOT.md`. The issues were "noted" but never actioned.

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

**New skill creation** is also allowed:
- Create new `.claude/skills/{name}.md` files for patterns observed 2+ times
- Register the skill in `ralph-loop.md`'s skill loading section
- Log the creation in the review file under "## Process Improvements Applied"

### Requires Human Approval

| File | Why |
|------|-----|
| `CLAUDE.md` | Constitution — changes affect all agents, not just RALPH |
| CLAUDE.md mandatory trigger table | Makes skill binding for all work, not just reviews |

### NEVER Allowed (Even With Human Approval Via Review)

- **Removing** existing rules, gates, or checks from any file
- **Lowering** verification levels or quality bars
- **Weakening** hard-fail criteria
- **Reducing** required evidence

The review can tighten the screws but never loosen them. The worst case is the process becomes slightly over-cautious. The alternative — a review that erodes its own guardrails — is unacceptable.

### Logging Requirement

Every autonomous process edit MUST be logged in the review file:

```markdown
## Process Improvements Applied

### 1. Added call-chain tracing to Architecture Compliance (ralph-output-adversarial-review.md)
**Trigger:** Review found DbContext issue that was missed because reviewer only checked the entry point (Settings.razor) without tracing through AccountService → AccountLifecycleService.
**Change:** Added requirement to trace full service layer chain, not just entry points.
**File:** .claude/skills/ralph-output-adversarial-review.md, section C

### 2. Created skill: efcore-migration-conventions.md
**Trigger:** 5 iterations documented "deviation" for timestamp-based migration names vs plan dates.
**Change:** New skill formalizing that EF Core migration timestamps are auto-generated.
**File:** .claude/skills/efcore-migration-conventions.md (new)
**Registered in:** ralph-loop.md, section 4
```

This log allows the human to review what was changed during the after-action and revert if needed.

---

## Deliverable Format

### For Mid-Loop Reviews

```markdown
# Adversarial Review - After Iteration {N}

## Review Metadata
- Run ID: {run_id}
- Review Type: mid-loop
- Iteration(s) Covered: {start_iter} to {end_iter}
- Commit Range: {start_commit}..{end_commit}
- Prior Reviews: {count}

## Commits Reviewed
- {hash} - {message}
- {hash} - {message}

## Findings
{numbered list with file:line references}

## Verdict: PASS | PARTIAL | FAIL

## Open Issues (for next review)
{list any PARTIAL items that need tracking}

## Review Boundary
- End Commit: {end_commit}
- Next review should start from: {end_commit}
```

### For Final Postmortem

```markdown
# Adversarial Review - Postmortem

## Review Metadata
- Run ID: {run_id}
- Review Type: postmortem
- Total Iterations: {N}
- Full Commit Range: {run_start}..{run_end}

## Review Ledger (All Reviews)
| Review | Iterations | Commits | Verdict | Issues |
|--------|------------|---------|---------|--------|
{table of all reviews including this one}

## This Review Covers
- Iterations {X} to {Y} (commits not covered by prior reviews)

## Aggregated Findings
{combine findings from all reviews}

## Final Verdict: PASS | PARTIAL | FAIL

## Fix-it Tasks (NOW)
{if any}

## Parked Items
{if any}

## Skill/Tooling Evolution Proposals
{if patterns emerged that should become skills}
```

### Actions Taken (REQUIRED at end of every review)

At the end of every review, output a structured section listing what was written where. This is the audit trail proving every finding was actioned:

```markdown
## Actions Taken
- **NOW:** Task C.1 "Fix dead code in UserCampaignActor" → inserted at top of IMPLEMENTATION_PLAN.md
- **NOW:** Task C.2 "Delete 10 useless AbandonedSignupDetectionTests" → inserted at top of IMPLEMENTATION_PLAN.md
- **PARK:** "Extract hardcoded brand colors to config" → appended to BACKLOG_PARKING_LOT.md
- **FIX INLINE:** Removed stale import in Services.Gmail (commit abc123)
```

**Rules:**
- Every finding in the review MUST appear in this section with its disposition
- If a finding has no action, it shouldn't be in the review
- The Actions Taken section is the contract between the review and the loop — if it's not listed here, it didn't happen
