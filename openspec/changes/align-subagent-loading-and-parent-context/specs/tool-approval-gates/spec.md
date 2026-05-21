## ADDED Requirements

### Requirement: Subagent approval evaluation uses the inherited parent cwd

The approval gate SHALL treat a subagent's `shell_execute` invocation as
having the cwd inherited from the parent session at spawn time, captured per
the `session-cwd` capability's "Resolved shell cwd flows to spawned subagents
as read-only snapshot" requirement. Persisted folder-scoped grants whose
directory contains the inherited cwd SHALL therefore auto-approve the
subagent invocation under the same rules as the parent session. Persisted
global grants (`directory: null`) SHALL continue to auto-approve regardless
of cwd, including when the inherited cwd is `null`. The matcher SHALL NOT
introduce a new short-circuit that bypasses persisted grants when the
inherited cwd is `null`; the existing
`ApprovalPatternMatching.MatchesShellApproval` semantics apply.

#### Scenario: Folder-scoped parent grant covers subagent invocation

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"dotnet build","directory":"/home/user/repos/foo/"}`
- **AND** the parent session's resolved cwd at subagent spawn is
  `/home/user/repos/foo/`
- **WHEN** the spawned subagent invokes `dotnet build` with no explicit
  `WorkingDirectory` argument
- **THEN** the matcher returns approved
- **AND** no approval prompt is rendered to the user

#### Scenario: Global grant covers subagent invocation with null cwd

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"netclaw stats","directory":null}`
- **AND** the spawned subagent has no inherited cwd (the parent had none
  either)
- **WHEN** the subagent invokes `netclaw stats`
- **THEN** the matcher returns approved regardless of the null cwd
- **AND** no approval prompt is rendered

#### Scenario: Folder-scoped parent grant does not match subagent with null cwd

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"dotnet build","directory":"/home/user/repos/foo/"}`
- **AND** the spawned subagent has no inherited cwd
- **WHEN** the subagent invokes `dotnet build` with no explicit
  `WorkingDirectory` argument
- **THEN** the folder-scoped grant SHALL NOT match (no effective directory)
- **AND** the approval gate prompts the user with the header form
  `Approve dotnet build in (no working directory)?` as documented in this
  capability's "Five-button approval prompt with verb-and-directory framing"
  requirement
- **AND** the daemon log SHALL emit an `approval_near_miss` diagnostic with
  reason `NoCandidateDirectory` so the operator can see why the grant did
  not match

### Requirement: Subagent inherits parent session-scoped approvals

The approval gate SHALL walk from a subagent's scope id toward its parent
session and SHALL treat any session-scoped approval (a `This chat` click)
recorded against the parent session id as also authorizing the subagent's
verbs. The subagent scope id has the form
`{parentSessionId}/subagent/{name}/{runId}`; the walk SHALL terminate at the
first non-`/subagent/` segment so unrelated sessions never share
session-scoped approvals. This requirement codifies the existing
`ToolApprovalActor.IsSessionApproved` scope-walk behavior so future
refactors SHALL NOT regress it; it does not introduce a new code path.

#### Scenario: This-chat grant in parent authorizes subagent invocation

- **GIVEN** the parent session granted `This chat` for verb `gh pr view` in
  the current chat
- **WHEN** a spawned subagent in that chat invokes `gh pr view 123`
- **THEN** the matcher returns approved via the session-scoped grant
- **AND** no approval prompt is rendered
