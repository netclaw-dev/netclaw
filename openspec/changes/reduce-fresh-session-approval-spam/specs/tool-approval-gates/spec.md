## ADDED Requirements

### Requirement: Fresh-session approval evidence is sanitized and executable

The system SHALL keep raw runtime logs outside source control. Committed
approval evidence SHALL replace private identities while preserving shell
grammar, path relationships, redirects, argument order, and control flow. The
evidence SHALL record aggregate source counts and representative cases without
exact session IDs, call IDs, user names, private hosts, repository identities,
branches, or source timestamps. A PII and secret scan SHALL cover every added
evidence and fixture file.

#### Scenario: Raw prompt becomes an identity-free case

- **GIVEN** a fresh-session prompt contains private runtime identifiers
- **WHEN** the prompt enters the regression corpus
- **THEN** the committed case uses neutral identity and path values
- **AND** its policy-relevant shell structure remains unchanged
- **AND** no raw log line enters either repository

#### Scenario: Sanitized value retains path authority boundaries

- **GIVEN** a command reads from a declared project and an external local root
- **WHEN** the command is sanitized
- **THEN** the replacement command keeps two distinct canonical roots
- **AND** the external root does not become a project descendant

#### Scenario: PII mutation fails the evidence gate

- **GIVEN** a committed evidence file contains a forbidden identity or secret pattern
- **WHEN** the evidence validation runs
- **THEN** validation fails before delivery

### Requirement: Fresh-session regressions execute real authorization outcomes

Each selected Netclaw case SHALL execute through the real shell policy
coordinator. The fixture SHALL bind its classification, expected final result,
approval options, correction, candidate coverage, and actor contact count. The
sample SHALL contain allow, approval, and terminal-deny outcomes. An evidence
classification SHALL NOT grant coverage or change authority.

#### Scenario: Exact typed scope and grants allow the call

- **GIVEN** a sanitized read-only call has complete parser facts
- **AND** exact typed scope and required grants cover every candidate
- **WHEN** the coordinator evaluates the call
- **THEN** the fixture result is Allow
- **AND** the expected trace identifies each coverage source

#### Scenario: Expected mutation remains approval-gated

- **GIVEN** a sanitized call performs remote mutation or process creation
- **WHEN** no exact authority covers every candidate
- **THEN** the fixture result is RequiresApproval
- **AND** its classification does not reduce the approval options

#### Scenario: Dynamic script remains strict

- **GIVEN** a sanitized script contains unresolved command substitution or control flow
- **WHEN** the coordinator evaluates the script
- **THEN** the fixture result remains RequiresApproval
- **AND** no reusable candidate is invented from incomplete facts

#### Scenario: Protected-path pair terminates without actor contact

- **GIVEN** a sanitized read case has a paired protected-path variant
- **WHEN** the coordinator evaluates the protected variant
- **THEN** the fixture result is Deny
- **AND** the approval actor is not contacted

#### Scenario: Guidance debt remains promptable when authored

- **GIVEN** a case is classified as AgentAlignmentDebt
- **WHEN** the model still authors that shell call
- **THEN** ordinary approval policy evaluates it
- **AND** the classification does not create reviewed-safe or grant coverage

### Requirement: Parser regressions own facts without owning authority

Each selected parser case SHALL have an identity-free ShellSyntaxTree
regression when shell facts affect the Netclaw outcome. The parser regression
SHALL assert general Bash or PowerShell facts only. It SHALL NOT assert a
Netclaw allow, approval, or deny decision.

#### Scenario: Existing strict fact is correct

- **GIVEN** a sampled command has dynamic identity, value, path, or control flow
- **WHEN** ShellSyntaxTree publishes Unknown or incomplete facts
- **THEN** a regression pins that strict result
- **AND** production parser code does not change only to reduce a prompt

#### Scenario: General fact is missing

- **GIVEN** accepted shell grammar proves a fact for a class of commands
- **AND** the current projection omits that fact
- **WHEN** maintainers add the fact
- **THEN** the change uses general Bash or PowerShell syntax
- **AND** it does not identify a private executable operation
- **AND** both target frameworks pass compatibility gates

#### Scenario: Executable behavior is private

- **GIVEN** a prompt can be reduced only by interpreting private executable options
- **WHEN** maintainers classify the case
- **THEN** the call remains approval-gated
- **AND** neither Netclaw nor ShellSyntaxTree receives that parser

### Requirement: Tool guidance uses one structured-tool decision table

Always-loaded guidance, tool schemas, and the bundled operations skill SHALL
use one tool-selection boundary. Known file reads, listings, and edits SHALL
prefer their first-party file tools. External discovery SHALL prefer
`web_search`, and page retrieval SHALL prefer `web_fetch`. Local repository
search, VCS, builds, tests, and process semantics SHALL remain shell work.
Guidance SHALL NOT claim that a preferred tool bypasses its own authority.
Guidance SHALL start with the smallest necessary shell operation. It SHALL use
one operation per call unless the requested result requires a pipeline. After
independent searches or diagnostics, it SHALL keep later operations in separate
calls instead of joining them with separators or presentation labels. If a
tool requires approval but no interactive requester is available, guidance
SHALL NOT retry or substitute that call during the current turn. After a
`Tool access denied:` result, guidance SHALL NOT change scope, retry, or
substitute another tool during the same user turn. A later explicit user
request MAY start a new call under normal approval policy. Guidance MAY apply one
`Tool execution deferred:` correction unchanged. Otherwise it SHALL use an
available structured tool or report the blocked operation once.
A successful structured file mutation SHALL serve as confirmation of that
operation. Guidance SHALL NOT add shell solely to verify it unless the user
requests shell behavior. Guidance SHALL select tools from the required effect.
Guidance SHALL NOT delegate a known file operation that one available file
tool can complete.

#### Scenario: Known file read selects file_read

- **GIVEN** the agent knows an exact file path
- **AND** `file_read` is available
- **WHEN** it needs file content without shell behavior
- **THEN** guidance selects `file_read`
- **AND** the shell schema does not recommend a shell read chain

#### Scenario: Known directory listing selects file_list

- **GIVEN** the agent knows an exact directory
- **AND** `file_list` is available
- **WHEN** it needs an ordinary listing
- **THEN** guidance selects `file_list`
- **AND** local recursive repository search remains a shell use case

#### Scenario: Known file change selects a file mutation tool

- **GIVEN** the agent knows the target file and intended edit
- **AND** `file_write` or `file_edit` is available
- **WHEN** it changes the file without requested shell behavior
- **THEN** guidance selects the matching first-party file tool
- **AND** the selected tool keeps its normal approval policy

#### Scenario: Successful file mutation is not verified with shell

- **GIVEN** `file_write` or `file_edit` reports success
- **AND** the user did not request shell behavior
- **WHEN** the agent continues the task
- **THEN** guidance treats the structured result as confirmation
- **AND** it does not add a shell-only verification call

#### Scenario: Disposable text starts with structured file tools

- **GIVEN** disposable text belongs in session scratch
- **AND** `file_write` and `file_read` are available
- **WHEN** the agent creates and reads that text
- **THEN** guidance selects those structured tools directly
- **AND** it does not first attempt a shell redirect

#### Scenario: Simple file operation is not delegated

- **GIVEN** a task requires one known local file operation
- **AND** one available file tool can complete that operation
- **WHEN** the agent selects a tool
- **THEN** guidance selects that file tool directly
- **AND** it does not delegate the operation to a subagent

#### Scenario: External retrieval avoids shell HTTP

- **GIVEN** the task needs external discovery or page retrieval
- **AND** the matching built-in web tool is available
- **WHEN** the agent selects a tool
- **THEN** discovery uses `web_search`
- **AND** retrieval uses `web_fetch`
- **AND** guidance does not recommend a shell HTTP client

#### Scenario: Required shell semantics stay in shell

- **GIVEN** the task needs local search, VCS, build, test, or process semantics
- **WHEN** the agent selects a tool
- **THEN** guidance retains `shell_execute`
- **AND** normal shell approval policy remains active

#### Scenario: Shell work starts with one necessary operation

- **GIVEN** a task requires shell semantics
- **WHEN** the agent composes its first shell call
- **THEN** guidance selects the smallest operation that answers the task
- **AND** optional diagnostics remain absent until the task requires them

#### Scenario: Unrequested diagnostics do not create a command chain

- **GIVEN** one shell operation answers the requested result
- **WHEN** the agent authors the first shell call
- **THEN** the call contains that operation only
- **AND** it does not add branch, history, layout, or environment diagnostics

#### Scenario: Independent shell reads remain separate

- **GIVEN** a task requires multiple independent searches or diagnostics
- **WHEN** the agent authors shell calls for those operations
- **THEN** each independent operation uses a separate call
- **AND** separators or presentation labels do not join their outputs

#### Scenario: Approval without an interactive requester does not fan out

- **GIVEN** a shell result reports that approval is required
- **AND** no interactive approval requester is available
- **WHEN** the agent continues the current turn
- **THEN** guidance does not retry or substitute that call
- **AND** it reports the block once

#### Scenario: Access-denied shell work does not fan out during the same turn

- **GIVEN** a shell result reports `Tool access denied:`
- **WHEN** the agent continues the task
- **THEN** guidance does not retry or substitute shell variants
- **AND** it does not call `set_working_directory`
- **AND** it reports the block once

#### Scenario: A later user request starts a new approval decision

- **GIVEN** the user denied an earlier shell call
- **WHEN** a later explicit user request requires a new shell call
- **THEN** guidance permits the new call
- **AND** normal approval policy evaluates it

#### Scenario: Deferred shell work applies one correction

- **GIVEN** a shell result reports `Tool execution deferred:` with one explicit correction
- **WHEN** the agent continues the task
- **THEN** it may apply that correction once and retry the original shell call unchanged
- **AND** any later approval-required or denied result terminates that turn's attempt

#### Scenario: Preferred tool is unavailable

- **GIVEN** an audience does not expose the preferred structured tool
- **WHEN** the agent selects from its actual tool list
- **THEN** guidance does not invent or call the absent tool
- **AND** any shell fallback follows ordinary approval policy

### Requirement: Guidance changes do not create shell authority

A guidance or tool-schema change SHALL NOT rewrite an authored shell call,
change its resolved cwd, cover a candidate, or suppress a required prompt. A
Netclaw authorization change SHALL require a coordinator counterexample with
complete typed facts and paired strict-boundary tests.

#### Scenario: Model ignores structured-tool guidance

- **GIVEN** the model authors shell for a known file operation
- **WHEN** the shell call reaches authorization
- **THEN** the coordinator evaluates the authored call unchanged
- **AND** no guidance classification grants authority

#### Scenario: Complete facts prove a policy defect

- **GIVEN** a fixture has complete typed parser and path facts
- **AND** the coordinator returns an outcome that conflicts with the specification
- **WHEN** maintainers change policy
- **THEN** paired external, dynamic, protected, and mutating cases remain strict

#### Scenario: A fixed glob root contains an in-root file alias

- **GIVEN** a complete shell occurrence contains a leaf glob under a fixed covering directory
- **AND** a link in that directory has an existing final target inside the same root
- **WHEN** the matcher extracts reusable candidates
- **THEN** that link alone does not make the analysis messy
- **AND** ordinary approval, mutation, audience, and path rules remain active

#### Scenario: An unsafe glob alias remains strict

- **GIVEN** a leaf-glob directory contains a broken or externally targeted link
- **OR** final-target or symlink inspection fails
- **WHEN** the matcher extracts reusable candidates
- **THEN** the analysis remains messy and publishes no reusable candidates

### Requirement: Fresh-session evals measure prompt reduction without prescribing answers

Behavioral evals SHALL use fresh sessions and natural task prompts. A prompt
SHALL NOT name the expected tool, `WorkingDirectory`, project declaration,
scratch path, or inline directory form. Assertions SHALL inspect exact tool
calls, their order, completion, and approval events. Baseline and changed runs
SHALL use the same task and model configuration.

#### Scenario: Project review eval measures scope retention

- **GIVEN** a fresh session receives a multi-command project review task
- **WHEN** the agent performs the task
- **THEN** assertions record project declaration and later shell directories
- **AND** the task text does not provide the desired call sequence

#### Scenario: Tool-selection eval measures structured choices

- **GIVEN** a fresh session receives known-file and external-retrieval tasks
- **WHEN** the agent selects tools
- **THEN** assertions record file, web, and shell tool choices
- **AND** the task text does not name those tools

#### Scenario: Before-and-after report keeps legitimate prompts

- **GIVEN** five baseline and five changed runs for each eval case
- **WHEN** maintainers report the results
- **THEN** the report includes shell calls, prompts, tool choices, and completion rate
- **AND** it identifies retained prompts for legitimate risk
- **AND** a variable result is reported without a weaker assertion

#### Scenario: Headless directory-transition guard keeps the boundary

- **GIVEN** an explicit directory transition reaches its expected trust-zone denial
- **WHEN** the headless result is assessed
- **THEN** the behavior guard requires one authored transition and no scope substitution
- **AND** the requested operation remains incomplete
