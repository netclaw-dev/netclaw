## Context

The current runtime contains PR #1952 file-tool guidance and PR #1977 typed
directory guidance. Fresh sessions still produced 30 prompts after the latest
binary swap. One read-only review session produced eight prompts.

The strongest failure crossed two existing instructions. A child declared a
project, then passed `session_dir` as `WorkingDirectory` for project commands.
The child also added an inline directory change. Its session block says to
always use session scratch for disposable shell work. That statement can
override the later project context.

The sample also contains required prompts. Remote mutation, process creation,
container control, external paths, dynamic scripts, and network access must
remain approval-gated. The design must reduce bad tool choices without turning
guidance into authority.

Raw runtime logs contain private identifiers and remain local. Source control
receives only identity-free commands and aggregate counts.

## Goals / Non-Goals

**Goals:**

- Freeze a sanitized sample from the post-swap fresh-session window.
- Preserve shell grammar, path relationships, redirects, and argument order.
- Separate expected approvals, guidance debt, parser gaps, and policy defects.
- Make project scope take precedence for project work after declaration.
- Reserve session scratch for disposable work outside a project.
- Prefer first-party file and web tools when they satisfy the exact task.
- Execute Netclaw cases through the real coordinator.
- Pin parser facts in ShellSyntaxTree with identity-free commands.
- Measure the same natural tasks before and after the change.

**Non-Goals:**

- Auto-approve a call because guidance labels it read-only.
- Parse private executable options in Netclaw or ShellSyntaxTree.
- Rewrite an authored command, cwd, or tool choice.
- Expand stored or reviewed-safe authority.
- Treat every complex command as a parser defect.
- Add a recursive file-search tool in this change.
- Start the large shell evaluator refactor.
- Change session cleanup or retention.

## Decisions

### Keep raw evidence outside both repositories

The local harvest process will read runtime logs and produce aggregate counts.
An agent will curate each selected case before any repository write.

The committed evidence will use neutral values:

- `/work/project` for a project root;
- `/work/project-child` for a child worktree;
- `/home/user/.netclaw/sessions/example` for session scratch;
- `/external/cache` for an external local root;
- `example/project` for a remote repository; and
- `service.example.invalid` for a remote host.

The committed artifact will omit session IDs, call IDs, user names, exact
timestamps, branches, private hosts, and original repository names. A PII scan
will reject forbidden values and common secret forms.

The alternative was a redacted copy of each raw log line. That approach keeps
too much linkable metadata and makes later PII review fragile.

### Reuse the executable evidence contract

The new sample will follow the existing live-regression evidence structure.
Each selected case will declare these facts:

- a stable evidence ID;
- a parse-preserving command;
- one classification and owner;
- the expected authorization result;
- the expected approval options;
- the expected actor contact count; and
- the reason for the classification.

The coordinator fixture will execute each Netclaw case. A locked digest will
cover the sanitized command and every expected result. Mutation tests will
prove that a changed command or expectation fails the contract.

The sample will include these result classes:

- an allow with exact typed scope and required grants;
- a prompt for mutation, network, or external authority;
- a prompt for dynamic or incomplete syntax;
- a prompt that guidance should prevent; and
- a terminal deny from a protected-path adversarial pair.

The classification never grants coverage. An `AgentAlignmentDebt` case remains
promptable if the model still authors it.

### Define one project-versus-scratch decision order

Parent and child guidance will use this order:

1. Use `project_dir` for work that belongs to the declared project.
2. Use typed `WorkingDirectory` for one call in a named child directory.
3. Use `session_dir` only for disposable work outside a project.
4. Keep an inline directory change only when that change is the task.

A successful `set_working_directory` call already updates child project scope
and reloads project instructions. The change will use that existing state. It
will not add a second project or scratch field.

The shell tool schema, parent session context, child session context, bundled
operations skill, and always-loaded rules will express the same order. Tests
will compare the load-bearing statements across parent and child contexts.

The alternative was policy-side command repair. That would change authored
intent and could create authority from prompt text.

### Keep tool selection in model guidance and tool schemas

The model will receive a short decision table:

- use `file_read` for a known file;
- use `file_list` for a known directory;
- use `file_write` or `file_edit` for known file changes;
- use `web_search` for external discovery;
- use `web_fetch` for page retrieval; and
- use the shell for local search, VCS, builds, tests, and processes.

The file and web tool descriptions will state their preferred use when the
tool is available. The shell description will retain its negative boundary.

The same surfaces will state one shell-composition order:

1. Start with the smallest shell operation that answers the request.
2. Do not use shell only to verify a successful structured tool result.
3. Do not retry or substitute shell variants after an approval-required result.
4. Treat a `Tool access denied:` result as terminal. Do not change scope, retry,
   or substitute another tool.
5. Apply one `Tool execution deferred:` correction unchanged. Otherwise use an
   available structured tool or report the blocked operation once.

A successful `file_write` or `file_edit` result is the confirmation for that
operation. Shell verification remains appropriate only when the user requests
shell behavior or the task independently requires shell semantics. Disposable
text starts with `file_write` and `file_read`; it does not first attempt a shell
redirect.

The agent does not delegate a known file operation that one available file
tool can complete.

This order reduces repeated approval attempts. It does not classify the shell
operation, alter its arguments, or provide authority.

Netclaw will not detect `cat`, `curl`, or another private command and replace
it. A model that ignores guidance will still reach normal approval policy.

The alternative was an executable table in the approval layer. That conflicts
with the shell abstraction rule and cannot cover future commands safely.

### Give each repository one type of proof

Netclaw tests will own authorization outcomes, approval options, actor contact,
prompt context, and model tool choice. ShellSyntaxTree tests will own only
general shell facts.

Each sampled parser command will receive a sanitized parser regression. The
test will assert facts such as:

- command occurrence order;
- the effective working directory;
- authored path domains;
- control-flow completeness;
- dynamic identity or value state; and
- arguments that precede the complete verb phrase.

A parser regression does not label a command safe. If current facts are
correctly unknown, the test will pin that strict result without production
changes.

Only a missing general Bash or PowerShell fact can justify ShellSyntaxTree
production work. Any public fact requires the normal package and compatibility
release gates before Netclaw consumption.

### Change policy only after a typed-fact counterexample

The first implementation pass will change guidance, schemas, tests, and evals.
It will not change authority.

A Netclaw policy change requires an executable fixture where all parser facts
are complete and the coordinator still returns the wrong result. The paired
test must prove the correction does not affect external, dynamic, protected,
or mutating cases.

This rule prevents a model-alignment failure from becoming an approval bypass.

The replay found one qualifying defect. A fixed leaf-glob root containing an
in-root file alias made a complete read pipeline messy. The matcher now accepts
only existing links whose final target remains within that fixed root.
Broken links, external targets, and failed inspection remain strict. Normal
executable, path, audience, mutation, and approval rules still decide authority.

### Measure behavior with natural fresh-session evals

The eval prompts will name the task and project, but not the desired tool or
directory argument. Assertions will inspect exact tool calls and their order.

The eval set will cover:

- repeated read-only project review;
- one named child-worktree inspection;
- a known-file read and edit;
- external discovery and page retrieval;
- disposable output outside a project; and
- an explicit directory-change task.

Each case will run five fresh sessions before and after the change. Evidence
will report prompt count, shell-call count, tool choices, and completion rate.
The report will keep legitimate prompts in the denominator and explain them.

The explicit directory-transition case is a boundary guard. A headless pass
requires one authored transition, the expected denial, and no substitution or
retry. The requested operation remains incomplete.

Deterministic tests own security. Model evals measure alignment and do not
replace actor or coordinator tests.

## Actor Boundaries and Persistence

The parent session actor owns project scope and rebuilds its prompt after a
successful declaration. `SubAgentActor` owns the child scope and child prompt.
Neither actor will infer scope from command text.

Tool metadata remains static and contains no private path. Volatile parent and
child context can contain the bound project and session paths for eligible
audiences. Public context remains redacted.

The change adds no actor message, event, snapshot, approval record, or store
version. Recovery rebuilds guidance from the existing run scope and working
context. A failed declaration leaves the prior project unchanged.

## Failure Modes and Recovery

- **The model still selects scratch for project work.** → Normal policy remains
  strict, and the eval records the failure.
- **The model still selects shell for a known file.** → Normal approval remains
  active, and the case stays in the guidance corpus.
- **The model verifies a successful file tool with shell.** → Normal approval
  remains active, and the follow-up eval records the redundant attempt.
- **The model retries after a policy denial.** → Normal approval remains active,
  and the no-retry assertion records the additional call.
- **A tool is unavailable.** → Guidance does not invent it. The model uses an
  available tool under normal policy.
- **A parser fact is incomplete.** → The command remains promptable. No fallback
  marks it safe.
- **A sanitized case loses grammar.** → Parser and coordinator digest tests fail.
- **A PII scan finds a value.** → Delivery stops until the fixture is replaced.
- **A declaration fails.** → The agent corrects the path or keeps the old scope.
- **A session recovers.** → Existing project and session state rebuild context.
- **An eval result varies.** → Report the observed rate. Do not weaken an
  assertion to claim success.

## Risks / Trade-offs

- **More guidance can compete for model attention.** → Replace conflicting text
  with one short order instead of appending another rule.
- **A small sample can overfit the implementation.** → Keep classifications
  general and include adversarial pairs.
- **Structured tools cannot replace recursive local search.** → Keep local
  repository search as a shell use case in this change.
- **A parser release can change facts.** → Review each fixture delta before a
  package update.
- **Prompt reduction can hide retained risk.** → Report legitimate prompts and
  denied cases beside the reduction number.

## Migration Plan

1. Commit sanitized evidence and executable regressions.
2. Record the fresh-session baseline from the current binary.
3. Update guidance and tool schemas without policy changes.
4. Run deterministic tests and five-run model evals.
5. Add parser or policy work only for a proved fact gap.
6. Rebase, run full gates, and deploy a binary swap.
7. Run the same fresh-session workload and report the delta.

Rollback restores the prior guidance and tool descriptions. It does not need a
data migration. The strict approval behavior remains valid during rollback.

## Open Questions

None. Evidence can change the implementation lane, but not the authority rules.
