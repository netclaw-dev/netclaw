## Why

PRD-002 SEC-009 requires project or scratch scope for shell work. PRD-006
requires tool guidance to preserve approval gates. A post-swap sample from three
fresh sessions produced 30 shell prompts. At least eight prompts came from
read-only project review, despite the current scope and file-tool guidance.

## What Changes

- Add a sanitized, parse-preserving evidence sample from the fresh-session
  window. Replace every identity, repository, host, URL, session, and path.
- Execute representative allow, prompt, and deny cases through the real shell
  coordinator. Lock each expected approval shape and actor contact count.
- Require project commands to retain the declared project scope. Session scratch
  remains the default only for disposable artifacts and unscoped work.
- Strengthen the shell tool schema and always-loaded guidance. A successful
  project declaration must not be replaced by session scratch or an inline
  directory change for later project commands.
- Strengthen first-party tool selection for known file operations and external
  web retrieval. Local search, builds, tests, VCS, and process work remain shell
  use cases.
- Add sanitized ShellSyntaxTree regressions for every sampled parser-fact gap.
  Add only general Bash or PowerShell facts that the shell grammar proves.
- Change Netclaw policy only when existing typed facts produce an incorrect
  decision. Do not add executable-private parsing or classification-derived
  authority.
- Add fresh-session model evals and measure approval results after a new binary
  swap. Preserve prompts for mutation, network, external authority, incomplete
  syntax, and unknown values.

In scope for MVP: evidence, guidance, tool schemas, focused parser facts,
policy corrections, regressions, evals, and live measurement. Out of scope:
the large shell evaluator refactor, executable-specific policy, approval-store
migration, broader grants, and automatic session cleanup.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-approval-gates`: Add sanitized fresh-session regressions and require
  durable project-scope and structured-tool guidance without new authority.
- `session-cwd`: Define project and session-scratch selection after a successful
  project declaration, including parent and subagent behavior.

## Impact

- **Code:** Shell tool metadata, prompt resources, child context, and policy
  projection only when evidence proves a defect.
- **Tests and evidence:** Netclaw coordinator fixtures, PII checks, model evals,
  and ShellSyntaxTree unit or corpus regressions.
- **Security:** The change adds no grant, safe root, parser guess, or approval
  bypass. Unknown, incomplete, external, and prompt-worthy work stays strict.
- **Operations:** Fresh sessions should produce fewer avoidable prompts. Live
  measurement will report prompt reduction and retained legitimate prompts.
- **APIs and persistence:** No planned public API, actor protocol, approval
  store, session history, or configuration change.
- **Dependencies:** A ShellSyntaxTree package update occurs only if a general
  parser fact is required and its independent release gates pass.
