# tool-authorization Specification

## Purpose

Define the testable rules that decide whether a model-authored tool call can
run, from the tool call to process launch, for every tool, audience, and
channel. The [tool authorization architecture](../../../docs/architecture/tool-authorization.md)
explains the contexts, diagrams, guidelines, and future scenarios for people;
this capability states the rules and maps each rule to its tests.

This capability uses these [engineering glossary](../../../docs/spec/GLOSSARY.md) terms:

- [Tool call](../../../docs/spec/GLOSSARY.md#tool-call), [dispatcher](../../../docs/spec/GLOSSARY.md#dispatcher), [authorization](../../../docs/spec/GLOSSARY.md#authorization), [authorization attempt](../../../docs/spec/GLOSSARY.md#authorization-attempt), [authority](../../../docs/spec/GLOSSARY.md#authority), [approval](../../../docs/spec/GLOSSARY.md#approval)
- [Schema exposure](../../../docs/spec/GLOSSARY.md#schema-exposure), [policy-visible tool](../../../docs/spec/GLOSSARY.md#policy-visible-tool), [reviewed-safe policy](../../../docs/spec/GLOSSARY.md#reviewed-safe-policy)
- [Trusted root](../../../docs/spec/GLOSSARY.md#trusted-root), [canonical path](../../../docs/spec/GLOSSARY.md#canonical-path), [file operation](../../../docs/spec/GLOSSARY.md#file-operation), [path access decision](../../../docs/spec/GLOSSARY.md#path-access-decision), [managed temporary directory](../../../docs/spec/GLOSSARY.md#managed-temporary-directory)
- [Audience](../../../docs/spec/GLOSSARY.md#audience), [consent mode](../../../docs/spec/GLOSSARY.md#consent-mode), [candidate](../../../docs/spec/GLOSSARY.md#candidate), [coverage](../../../docs/spec/GLOSSARY.md#coverage), [grant](../../../docs/spec/GLOSSARY.md#grant), [grant scope](../../../docs/spec/GLOSSARY.md#grant-scope), [one-time consent](../../../docs/spec/GLOSSARY.md#one-time-consent), [consent request](../../../docs/spec/GLOSSARY.md#consent-request), [consent answer](../../../docs/spec/GLOSSARY.md#consent-answer), [decision](../../../docs/spec/GLOSSARY.md#decision), [deny rule](../../../docs/spec/GLOSSARY.md#deny-rule), [unresolved syntax](../../../docs/spec/GLOSSARY.md#unresolved-syntax)

Non-goals: network exposure, device pairing, host authentication, tool
argument schema validation, output bounds, and containment of a process after
it starts.

## Authority Flow

```text
inbound adapter -> turn context (audience, requester)          durable in the session journal
  -> schema exposure (no authority)                              call-local
  -> dispatcher: interpret the call
  -> admission: audience profile, MCP allow lists, shell mode    call-local
  -> consent mode (Auto | Approval | Deny)                       call-local
  -> shell only: analyze with ShellSyntaxTree
  -> screen: hard deny, protected paths, path access decision    call-local
  -> cover each candidate: one-time, chat grant, persistent
     grant, reviewed-safe, approval-exempt output command        store is durable
  -> outcome: Allowed | RequiresAgentCorrection
              | RequiresApproval | Denied
  -> RequiresApproval: no operator -> fixed denial text
                       operator    -> journal request, prompt, answer,
                                      record grant, authorize again
  -> shell launch: hard deny, full gate again, hard deny,
     path targets unchanged, start one process
```

The flow is schematic. It omits logging, trace rows, cancellation, argument
validation, and the exact order of correction and consent inside the shell
coordinator. TA-9 states that order.

## Decision Owners

| Decision or data | Owner today | Lifetime |
|---|---|---|
| Audience and requester of a turn | inbound adapter, `TrustContextDeriver`, `TurnContext` | Durable in the session journal |
| Schema exposure | `ToolAccessPolicy.IsToolExposed`, progressive disclosure | Call-local |
| Tool admission for an audience | `ToolAuthorizer` (admission rules), `ToolAccessPolicy.AdmitAudience` / `EvaluateShellCapability`, `ToolAudienceProfileResolver` | Call-local; profiles are configuration |
| Consent mode | `ToolAccessPolicy.GetApprovalMode`, `ToolApprovalConfig` | Call-local; configuration |
| Shell mode and Personal-only shell | `ToolAccessPolicy.EvaluateShellCapability`, `TrustContextPolicy` | Call-local; configuration |
| Hard deny | `ShellCommandPolicy`, `HardDenyRule`, `HardDenyOverridesLoader` | Process-local rules; call-local result |
| Protected paths | `ToolPathPolicy` (lists from `DaemonToolPathPolicyFactory`) | Process-local |
| Glob scope and D5 glob match | `ShellGlobScope`, `ToolPathPolicy.GlobMayReachDeniedPath` | Call-local |
| Path access decision | `PathAccessPolicy` | Call-local |
| Shell syntax facts and candidates | ShellSyntaxTree via `ShellCommandAnalysis`, `ShellApprovalMatcher` | Call-local |
| Shell order of checks, advice, and coverage | `ShellPolicyCoordinator` | Call-local |
| Non-shell grant check | `DispatchingToolExecutor` (inline) | Call-local |
| Chat grants and persistent grant match | `ToolApprovalActor`, `ApprovalPatternMatching` | Chat grants actor-local; store durable |
| Persistent grant store | `ToolApprovalStore` (`tool-approvals.json` v3) | Durable |
| One-time consent | `OneTimeApprovalKeys` in `ToolApprovalAttempt` | Call-local |
| Agent correction | `ShellPolicyCoordinator`, `TemporaryPathCorrectionPolicy`, `NativeToolShellCorrectionDetector` | Call-local; retry key actor-local |
| Prompt options | `ToolAccessPolicy.BuildApprovalOptions` | Call-local |
| Approval note in the tool result | `ConsentAnswerCodec.AppendResultNote` | Call-local |
| Consent request, answer, and requester check | `SessionToolExecutionPipeline`, `LlmSessionActor`, `ApprovalButtonValueCodec`, `PendingApprovalLookup` | Durable journal; actor-local unanswered state |
| Subagent consent | `ParentSessionApprovalBridge`, `SubAgentActor` | Actor-local, live-only |
| Launch re-verification | `ShellProcessLaunch` | Call-local |

A code PR that moves a decision updates this table and the architecture
document in the same diff.

## Verification Map

| Rule | Main proofs |
|---|---|
| TA-1 | `TurnContextTests`, `TrustContextDeriverTests`, `SessionToolExecutionPipelineTests.Source_less_approval_required_turn_fails_closed_without_prompt` |
| TA-2 | `McpToolAudienceGrantsTests.Loading_deferred_tool_does_not_bypass_invocation_approval`, `SearchToolsToolTests` |
| TA-3 | `ToolAuthorizationMutationTests` (tool authorization mutation gate), `McpToolAudienceGrantsTests`, `ToolAudienceProfileDefaultsTests` |
| TA-4 | `ToolApprovalConfigTests`, `SecurityPolicyDefaultsTests`, `ToolApprovalGateTests` |
| TA-5 | Catalog deny rows with 0 approval service calls (`ShellApprovalDispositionMatrixTests`), `ToolAuthorizationMutationTests.Shell_hard_denial_prevents_dispatch_despite_approval`, `DispatchingToolExecutorLaunchTests`, `HardDenyParityCorpusTests`, `GlobPolicyMutationTests` (bound values), the shell analysis mutation gate (daemon kill pattern) |
| TA-6 | `ToolPathPolicyTests`, `UnattendedPathAccessTests`, `PublicAudienceFileAccessPolicyTests`, `PathAccessPolicyMutationTests` (path access mutation gate), `DaemonToolPathPolicyFactoryTests`, `GlobPolicyMutationTests` (D5 glob match and link walk), `ShellConfigReadTests` (D6 gate), `ToolAuthorizerOrderMutationTests` with `scripts/run-shell-config-read-mutations.sh` (D6 mutation gate) |
| TA-7 | `ShellApprovalDispositionMatrixTests` with `ShellApprovalCaseCatalog`, `MessyCommandOneTimeApprovalTests`, `ShellCommandAnalysisTests`, `PerCommandJudgmentMutationTests` (D1), `GlobPolicyMutationTests`, the shell analysis and shell assignment mutation gates |
| TA-8 | `ToolApprovalActorTests`, `RepositoryWorktreeApprovalTests`, `ApprovalDirectoryMutationTests` (approval directory mutation gate), `ReviewedSafeShellPolicyTests`, `PathAccessPolicyMutationTests` (interactive read branch), `ApprovalPatternV3Tests` and `SubcommandEverywhereGrantTests` (legacy grant words) |
| TA-9 | `DispatchingToolExecutorTests` correction cases, `SessionToolExecutionPipelineTests`, `ToolCorrectionDeliveryTests`, `TemporaryPathCorrectionPolicyTests` |
| TA-10 | `ToolApprovalGateTests`, channel prompt builder tests, `SessionBindingContractTests`, `ToolInteractionResponseParserTests`, `McpApprovalMatcherTests`, `ShellApprovalLifecycleIntegrationTests` (approval note) |
| TA-11 | `ApprovalRehydrationTests`, `ToolApprovalStateTests`, `ShellApprovalLifecycleIntegrationTests` |
| TA-12 | `ParentSessionApprovalBridgeTests`, `SubAgentSpawnIntegrationTests`, `SubAgentActorTests`, `ToolApprovalActorTests` |
| TA-13 | `ToolApprovalStoreTests`, `ApprovalEntryWireCodecTests`, `ToolApprovalActorTests`, the `approvals` smoke tape |
| TA-14 | `DispatchingToolExecutorLaunchTests`, `ShellProcessLaunchTests`, `ShellToolTests`, background evals `queued_grant_revoked` and `queued_grant_valid` |
| TA-15 | `DispatchingToolExecutorTests` trace cases, `AuthorizationAttemptIdTests`, `ShellPolicyEvidenceFixtureTests`, evals Category 9 |
| TA-16 | `ApprovalsCommandTests`, `ApprovalsManagerPageTests`, `ToolAudienceProfilesDoctorCheckTests`, `SecurityPolicyDoctorCheckTests` |

The outcome direction check (`scripts/check-approval-outcome-direction.py`)
protects the Result column of the catalog snapshot. The focused mutation gates
are listed in `TOOLING.md` under "Focused Mutation Tests". Evidence fixtures
live in `src/Netclaw.Security.Tests/Evidence/`.

Known gaps without a test today: a shell grant never authorizes `file_read`
(TA-8), a launch-time hard deny case (TA-5), and a direct call to a hidden
first-party tool (TA-2).

## Requirements

### Requirement: TA-1 Trust context is explicit and fails loud

Every session turn SHALL carry an explicit turn context with a parsed audience
(`Personal`, `Team`, or `Public`), a requester, and a principal. Authorization,
consent, and dispatch SHALL use this turn context. The session journal SHALL
persist the turn context with each consent request, and a recovered request
SHALL use the persisted context, not the current session state.

An ingress that receives an invalid audience value SHALL reject the input
loudly (for example an HTTP 400 result or a CLI exit code 1). Where a
component falls back because an audience is missing or cannot be parsed, it
SHALL fall back to the narrowest audience, `Public`. No fallback SHALL select
a broader audience than the source provides. An audience derived from a
deployment default and a source audience SHALL be the narrower of the two.

A tool execution context SHALL hold the audience as a parsed value. Tool
authorization SHALL read that value and SHALL NOT parse a wire string again.

Planned change (owner decision, September 29): a missing or unreadable
audience becomes an error in every component. A follow-up code PR implements
it. Until that PR merges, the `Public` fallback above is the current behavior.

A turn without a message source SHALL NOT synthesize a requester. A consent
request without a recorded requester SHALL fail closed without a prompt,
except for a verified-automation principal.

#### Scenario: Invalid audience is rejected at ingress

- **GIVEN** a reminder create request with audience `admin`
- **WHEN** the daemon endpoint validates the request
- **THEN** it returns HTTP 400
- **AND** it dispatches no command

#### Scenario: Missing source audience does not broaden

- **GIVEN** a deployment default of `Personal` and a source audience of `Team`
- **WHEN** Netclaw derives the effective audience
- **THEN** the effective audience is `Team`

#### Scenario: Turn without a source cannot ask for consent

- **GIVEN** a turn with no message source
- **WHEN** a tool call requires consent
- **THEN** the call fails closed without a prompt
- **AND** Netclaw does not create a requester

#### Scenario: Tool authorization reads the parsed audience

- **GIVEN** a tool execution context with the parsed audience `Team`
- **WHEN** authorization evaluates a tool call
- **THEN** it uses `Team` without a string parse
- **AND** it applies no parse-failure fallback

### Requirement: TA-2 Schema exposure grants no authority

The model SHALL see only policy-visible tool schemas. Schema exposure,
`search_tools` results, and `load_tool` SHALL NOT grant authority. Every call
SHALL pass authorization at dispatch, whether or not its schema was exposed.
A hidden tool and an absent tool SHALL produce the same `load_tool` result.

#### Scenario: Loaded deferred tool still needs consent

- **GIVEN** an MCP tool in `Approval` mode that the model loads with `load_tool`
- **WHEN** the model calls the tool
- **THEN** authorization returns `RequiresApproval`

#### Scenario: Direct call to a tool that the audience cannot use

- **GIVEN** a Team session and an MCP server that the Team profile does not allow
- **WHEN** the model calls a tool of that server by name
- **THEN** authorization returns `Denied` with reason `mcp_server_not_allowed_for_audience_profile`

### Requirement: TA-3 Audience profiles admit tools

Each audience profile SHALL decide which tools the audience can use:

- `ToolsMode` with `AllowedTools` SHALL restrict the profile-managed tools.
  A profile-managed tool absent from the list SHALL be hidden and SHALL be
  denied with `tool_not_allowed_for_audience_profile`. A tool that is not
  profile-managed SHALL NOT be restricted by `AllowedTools`.
- `McpServersMode` with `AllowedMcpServers` SHALL restrict MCP servers
  (`mcp_server_not_allowed_for_audience_profile`). `McpServerToolGrants` SHALL
  restrict the tools of an allowed server
  (`mcp_tool_not_allowed_for_audience_profile`). A server denial SHALL take
  precedence over a tool denial. An allowed server with no tool list for it
  SHALL expose all of its tools.
- An admission denial SHALL be terminal. No consent answer SHALL override it.

The default profiles SHALL be monotonic: every profile-managed tool of
`Public` SHALL also be in `Team`, and every tool of `Team` SHALL also be in
`Personal`. The defaults SHALL be:

- `Public`: `file_read`, `file_list`, `file_search`, `tool_output_read`,
  `attach_file`; no MCP server; file access scoped to its own session.
- `Team`: all file tools, `web_search`, `web_fetch`, `skill_manage`, the four
  reminder tools, and `set_working_directory`; no `shell_execute`, no webhook
  tools, and no MCP server.
- `Personal`: all tools, all MCP servers, and unrestricted file modes.

MCP `GrantCategory` values and tool grant categories SHALL NOT be an
authorization input.

Invalid `Tools.AudienceProfiles.ChannelAttachments` configuration SHALL stop
daemon startup with an error that names the invalid entries.

#### Scenario: Public cannot edit files

- **GIVEN** a Public session with the default profile
- **WHEN** the model calls `file_edit`
- **THEN** authorization returns `Denied` with reason `tool_not_allowed_for_audience_profile`

#### Scenario: MCP server denial wins over a tool grant

- **GIVEN** a Team profile that lists a tool of server `s` in `McpServerToolGrants` but does not allow server `s`
- **WHEN** the model calls that tool
- **THEN** authorization returns `Denied` with reason `mcp_server_not_allowed_for_audience_profile`
- **AND** the tool does not run, even with a one-time consent

#### Scenario: Team can use outbound web tools

- **GIVEN** a Team session with the default profile
- **WHEN** the model calls `web_fetch`
- **THEN** the admission check passes

#### Scenario: Invalid attachment configuration stops startup

- **GIVEN** an audience profile with an invalid `ChannelAttachments` entry
- **WHEN** the daemon starts
- **THEN** startup fails with `Invalid Tools.AudienceProfiles.ChannelAttachments configuration`

### Requirement: TA-4 Consent mode and shell mode resolve per audience and tool

The consent mode of a call SHALL be `Auto`, `Approval`, or `Deny`, resolved in
this order: a matcher-specific key (for example `file_write:control-plane`),
`ToolOverrides[tool]` (an MCP `server__tool` alias also matches),
`McpServerDefaults[server]`, the Personal fail-closed rule, then `DefaultMode`.
Without an `ApprovalPolicy`, the mode SHALL be `Auto` except where the
Personal fail-closed rule applies.

- The Personal fail-closed rule SHALL select `Approval` for `shell_execute`
  unless an exact `shell_execute` override selects a mode. It SHALL apply when
  `ApprovalPolicy` is absent and when `DefaultMode` is `Auto`.
- The rule SHALL also select `Approval` for Personal `file_write` and
  `file_edit` on a Netclaw control-plane path.
- `Deny` SHALL deny with `tool_denied_by_approval_policy` and SHALL NOT prompt.
- A shell call that needs an exact tree approval SHALL raise `Auto` to
  `Approval`.
- An unknown mode value SHALL deny with `internal_policy_failure`.

The shell mode SHALL be `Tools.ShellMode`, else `Security.ShellExecutionMode`,
else the posture default (`HostAllowed` for Personal, `Off` otherwise).
`shell_execute` and `check_background_job` SHALL require the Personal audience
and `HostAllowed`: `Off` denies with `shell_disabled`, `SandboxOnly` denies
with `shell_requires_sandbox_backend`, and a non-Personal audience denies with
`shell_requires_personal_context`.

#### Scenario: Personal shell without an override asks

- **GIVEN** a Personal profile with `DefaultMode` `Auto` and no `shell_execute` override
- **WHEN** the model calls `shell_execute` with an uncovered command
- **THEN** authorization returns `RequiresApproval`

#### Scenario: Explicit Auto override removes the prompt

- **GIVEN** a Personal profile with `ToolOverrides.shell_execute` set to `Auto`
- **WHEN** the model calls `shell_execute` with a command that passes hard deny and path checks
- **THEN** authorization returns `Allowed` with allow reason `PolicyAuto`

#### Scenario: SandboxOnly shell mode denies

- **GIVEN** `Tools.ShellMode` set to `SandboxOnly`
- **WHEN** the model calls `shell_execute`
- **THEN** authorization returns `Denied` with reason `shell_requires_sandbox_backend`

### Requirement: TA-5 Hard deny precedes grant lookup and repeats at launch

Hard deny rules and protected-path rules SHALL run before any grant lookup,
consent prompt, or agent correction. A hard denial SHALL be terminal in every
consent mode, and no grant, one-time consent, or answer SHALL override it.
The terms [deny rule](../../../docs/spec/GLOSSARY.md#deny-rule) and
[policy data](../../../docs/spec/GLOSSARY.md#policy-data) have their
glossary meaning.

- Built-in rules SHALL cover self-destructive commands, system-destructive
  commands (for example `rm -rf /`, `rm -rf ~/`, fork bombs, `mkfs`), and
  privilege escalation (`sudo`, `su`, `doas`, a PowerShell `-Verb RunAs`
  start).
- The self-destructive rules SHALL be `netclaw daemon stop`,
  `systemctl stop netclaw`, and a kill (`kill`, `killall`, `pkill`, or
  `Stop-Process`) whose operand text names the Netclaw daemon, for example
  `pkill netclawd` or `kill $(cat ~/.netclaw/daemon.pid)` (owner decision D2).
  Any other kill SHALL be an ordinary command: it asks for consent, and a
  grant can cover it.
- Operator rules from `Tools.HardDenyPatterns` and structured override rules
  SHALL add to the built-in rules and SHALL NOT remove them.
- A denial reason SHALL be `hard_deny_<category>` with category
  `custom_deny`, `self_destructive`, `system_destructive`, or
  `privilege_escalation`.
- Each command in a compound, a pipeline, or a nested same-language shell
  SHALL be checked. A command inside an assignment substitution
  (`x=$(sudo ls)`) SHALL be checked.
- The hard-deny screen SHALL use a complete parse. When the parse of a call
  stops part way, the screen SHALL check each list element again, so that the
  child of a same-language wrapper meets the hard-deny list.
- The hard-deny list SHALL also check each proved value of a word that reads
  a variable, and each value of a loop variable. The bound form SHALL get the
  decision of its literal twin: `x=/; rm -rf "$x"` is denied as `rm -rf /`
  is. More than 256 value combinations SHALL deny. Netclaw SHALL check bound
  values only for an expansion, so a literal word keeps its own decision.
- A shell call SHALL be denied with `shell_references_protected_path` when its
  text or its analysis names a protected shell path.
- The shell launch SHALL check hard deny and protected paths again before and
  after the final authorization (TA-14).

Owner: `ShellCommandPolicy` owns the rules, the daemon kill pattern, and the
bound-value check. The rules are process-local policy data. Each result is
call-local. ShellSyntaxTree gives the bound values for one call, and Netclaw
keeps them call-local.

#### Scenario: Hard deny wins over a stored grant

- **GIVEN** a persistent global grant for `netclaw daemon stop` (catalog case `hard-deny-beats-stored-grant`)
- **WHEN** the model calls `shell_execute` with `netclaw daemon stop`
- **THEN** authorization returns `Denied` with reason `hard_deny_self_destructive`
- **AND** Netclaw does not query the grant store

#### Scenario: Hard deny in a pipeline tail

- **GIVEN** an interactive Personal session (catalog case `hard-deny-pipeline-tail-blocks`)
- **WHEN** the model calls `shell_execute` with `echo safe | netclaw daemon stop`
- **THEN** authorization returns `Denied` with reason `hard_deny_self_destructive`

#### Scenario: Privilege escalation around a nested shell

- **GIVEN** an interactive Personal session (catalog case `hard-deny-sudo-nested-shell-blocks`)
- **WHEN** the model calls `shell_execute` with `sudo bash -lc "git status"`
- **THEN** authorization returns `Denied` with reason `hard_deny_privilege_escalation`

#### Scenario: An ordinary command is not hard-denied

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `shell_execute` with `git status`
- **THEN** hard deny does not deny the call

#### Scenario: A grant covers a kill of a test server

- **GIVEN** an interactive Personal session with a grant for anywhere for `pkill` (catalog case `kill-test-server-uses-grant`)
- **WHEN** the model calls `shell_execute` with `pkill -f 'http.server 8899'`
- **THEN** authorization returns `Allowed` with allow reason `StoredApproval`

#### Scenario: A kill of a process ID asks for consent

- **GIVEN** an interactive Personal session with no grants (catalog case `kill-process-id-prompts`)
- **WHEN** the model calls `shell_execute` with `kill 12345`
- **THEN** authorization returns `RequiresApproval` with the reusable candidate `kill`

#### Scenario: A kill of the daemon stays hard-denied

- **GIVEN** an interactive Personal session with a grant for anywhere for `pkill` (catalog case `kill-daemon-stays-hard-denied`)
- **WHEN** the model calls `shell_execute` with `pkill -f netclawd`
- **THEN** authorization returns `Denied` with reason `hard_deny_self_destructive`

#### Scenario: A bound value gets the decision of its literal twin

- **GIVEN** an interactive Personal session with a grant for anywhere for `rm`
- **WHEN** the model calls `shell_execute` with `x=/; rm -rf "$x"`
- **THEN** authorization returns `Denied` with reason `hard_deny_system_destructive`
- **AND** `x=/tmp/build; rm -rf "$x"` is not hard-denied

#### Scenario: A command in an assignment substitution is checked

- **GIVEN** an interactive Personal session (catalog case `assignment-substitution-sudo-hard-denies`)
- **WHEN** the model calls `shell_execute` with `x=$(sudo ls)`
- **THEN** authorization returns `Denied` with reason `hard_deny_privilege_escalation`

#### Scenario: A partial parse does not hide a wrapper child

- **GIVEN** an interactive Personal session with grants for anywhere for `cd`, `git fetch`, and `bash` (catalog case `wrapper-child-after-failing-cd-hard-denies`)
- **WHEN** the model calls `shell_execute` with `cd sub && git fetch; bash -lc "echo \"a b\"; netclaw daemon stop"`
- **THEN** authorization returns `Denied` with reason `hard_deny_self_destructive`

### Requirement: TA-6 Path access decisions own file-tool authority

A file tool SHALL get its filesystem authority only from a path access
decision for its exact file operation (`Read`, `Write`, `Attach`, or
`DeclareProjectScope`). A shell path SHALL use the `Write` operation, except
that a Bash program that only reads its operands SHALL get `Read` protection
for a write-protected path (owner decision D6). The
decision SHALL apply, in order: the canonical path, the audience root catalog,
the link check, then protection.

- Roots SHALL come from the audience profile (`ReadFiles`, `WriteFiles`,
  `AttachFiles` with mode `None`, `Roots`, or `All`), the session storage
  envelope of the session, the declared project directory, and the global read
  roots (`{skills_dir}`, `{identity_dir}`, `{workspaces_dir}`) for `Read` only
  and never for `Public`.
- Only `Personal` SHALL get the shared Netclaw sessions root and the legacy
  logs root. `Team` and `Public` SHALL get only their own session envelope
  and session directory. A child run SHALL inherit the audience and workspace
  limits of its parent.
- A legacy run MAY read its own exact raw log. That exact-file authority SHALL
  NOT cover the parent directory, an adjacent file, or a project declaration.
  A storage ancestor that Netclaw reads for a link check SHALL NOT grant
  directory authority.
- `Personal` with mode `All` SHALL skip root checks, attended or unattended
  (decision D2). Only a `DeclareProjectScope` decision SHALL stay inside the
  trusted roots. Consent SHALL NOT widen an explicit `Roots` or `None` profile,
  and a bounded profile SHALL confine attended and unattended runs alike.
- A relative path SHALL resolve against the project directory, else the
  session directory. File tools SHALL NOT expand `~`; `~/x` is a relative path.
- A path through a link that leaves the root SHALL be denied. A path whose
  base has a link ancestor SHALL be denied, and Netclaw SHALL NOT try another
  base.
- Protection SHALL depend on the operation. Each file under the config
  directory is
  [ordinary configuration](../../../docs/spec/GLOSSARY.md#ordinary-configuration)
  and SHALL be readable by a file tool (owner decision D6), except
  `secrets.json` and the webhook route files, which hold the verification
  secret. This includes `netclaw.json`, the grant store `tool-approvals.json`,
  `daemon.env`, device state, bootstrap state, and the hard-deny override
  file. Secrets (`secrets.json`), webhook route files, keys, the database,
  process-control files, and the tooling shadow SHALL be read-denied. The config directory, secrets, keys, the database, process
  control files, system skills, and server feeds SHALL be write-denied.
- Shell text that names secrets, webhook route files, keys, the database, or
  process-control files SHALL be denied. Shell text that names the config
  directory SHALL be denied. Only an exact path argument of a read-only shell
  program that names one file below the config directory SHALL leave this
  text check. A `..` segment, a glob, a brace word, the directory itself, and
  program text (a `jq` module search path, `python3 -c`, `node -e`) SHALL keep
  the denial. The check SHALL also see the directory in each spelling: with
  `//`, `/./`, or `name/../` in the text, and in the unquoted value of a word
  whose quotes split the name.
- A read-only shell program SHALL be one of the policy-data programs `cat`,
  `head`, `tail`, `wc`, `grep`, `jq`, and `diff`, with bounded argument
  values and no assignment prefix. A redirect that writes SHALL keep `Write`
  protection for its target only, and a null-device redirect SHALL be
  ignored. A plain
  argument word that names an entry of the command's directory, or a word
  with a brace, SHALL make the program not read-only. Such a program SHALL get
  `Read` protection only for a path that the write list protects. A
  write-protected directory operand that holds a read-denied path SHALL stay
  denied. Known gap (issue #2341): a recursive or brace read that names only a
  parent of the config directory is not denied by this rule. Each other shell program SHALL keep
  `Write` protection for each path, so each write to a config file stays
  denied.
- Owner decision D5 (option A): a shell glob word that can match a protected
  shell path, the default credential store (`~/.netclaw/keys`,
  `~/.netclaw/config/secrets.json`), or a directory that contains one, SHALL
  get the literal-path denial (`shell_references_protected_path`). The match
  SHALL be lexical and segment by segment. It SHALL cover dot entries,
  bracket expressions, and case. Netclaw SHALL NOT list directories or follow
  links for this check.
- Accepted D5 gap: a link below the covering directory of a glob that leads
  to a protected path is not seen by the lexical check. Only a person can make
  such a link, because an agent call that links to the credential store
  (`ln -s ~/.netclaw/keys x` or `ln -s ~/.netclaw/k* x`) is denied.
- A shell word that reads a proved variable value SHALL get the protected-path
  decision of the literal value.
- Allow checks SHALL compare paths with ordinal case except on Windows. Deny
  checks SHALL ignore case.
- A path access denial SHALL be terminal and SHALL NOT reveal root paths to a
  Public session. No grant SHALL replace a path access denial, attended or
  unattended.
- Tool capability and shell command policy SHALL run before file protection.
  File authority SHALL NOT enable shell. Netclaw SHALL derive the known real
  paths of a shell call from the command analysis, independent of approval
  candidates, and SHALL check known causal-intent and fallback paths before
  stored or reviewed-safe coverage.
- A readable config file SHALL NOT imply write, edit, attach, or other shell
  authority. Secret values SHALL live only in protected stores.

Owner: `PathAccessPolicy` owns the path access decision, and its result is
call-local. `ToolPathPolicy` owns protection and the D5 glob match
(`GlobMayReachDeniedPath`, with the segment match in `ShellGlobScope`). Its
read, write, and shell lists come from `DaemonToolPathPolicyFactory` and are
process-local. No state of these checks is durable.

#### Scenario: Each config file but the secrets is readable

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_read` on `netclaw.json`, `tool-approvals.json`, or `hard-deny-overrides.json`
- **THEN** the path access decision allows the read
- **AND** a `shell_execute` call with `cat <config dir>/hard-deny-overrides.json` is not denied

#### Scenario: A shell write to a config file stays denied

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `shell_execute` with `cp other.json <config dir>/hard-deny-overrides.json`, `echo x > <config dir>/netclaw.json`, or `sort -o <config dir>/netclaw.json <config dir>/netclaw.json`
- **THEN** authorization returns `Denied`

#### Scenario: Ordinary config is readable but not by shell text

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_read` on `netclaw.json` or on `tool-approvals.json`
- **THEN** the path access decision allows the read
- **AND** a `shell_execute` call whose text names the whole config directory (`grep -r token <config dir>` or `cat <config dir>/*.json`) is denied

#### Scenario: Program text that names the config directory stays denied

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `shell_execute` with `jq -n 'import "secrets" as $s {search: "<config dir>"}; $s'`
- **THEN** authorization returns `Denied`

#### Scenario: A harmless redirect does not deny a config read

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `shell_execute` with `grep -n port <config dir>/netclaw.json 2>/dev/null`
- **THEN** the call is not denied

#### Scenario: Webhook route files stay read-denied

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_read` on a file in the webhooks directory, or `shell_execute` with `cat <config dir>/webhooks/<route>.json`
- **THEN** the read is denied

#### Scenario: Secrets stay read-denied

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_read` on `secrets.json` or on a file in the `keys` directory
- **THEN** the path access decision denies the read

#### Scenario: A readable grant store is not writable

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_write` on `tool-approvals.json`
- **THEN** the path access decision denies the write

#### Scenario: A glob that can match the credential store is denied as the literal path

- **GIVEN** an interactive Personal session with a grant for anywhere for `cat` (catalog case `glob-credential-keys-denied-as-literal`)
- **WHEN** the model calls `shell_execute` with `cat ~/.netclaw/k*/*.xml`
- **THEN** authorization returns `Denied` with reason `shell_references_protected_path`
- **AND** `cat ~/.netclaw/*/secrets.json` is denied with the same reason

#### Scenario: A glob that cannot match a protected path is not denied

- **GIVEN** an interactive Personal session with a grant for anywhere for `ls` (catalog case `glob-in-directory-segment-uses-global-grant`)
- **WHEN** the model calls `shell_execute` with `ls -d ~/repositories/*/akka*`
- **THEN** authorization returns `Allowed` with allow reason `StoredApproval`

#### Scenario: An agent cannot link to the credential store

- **GIVEN** an interactive Personal session with a grant for anywhere for `ln` (catalog case `glob-link-to-credential-keys-denied-as-literal`)
- **WHEN** the model calls `shell_execute` with `ln -s ~/.netclaw/k* keys-link`
- **THEN** authorization returns `Denied` with reason `shell_references_protected_path`

#### Scenario: A variable that holds a protected path is denied

- **GIVEN** an interactive Personal session with a grant for anywhere for `cat` (catalog case `assigned-credential-path-denied-as-literal`)
- **WHEN** the model calls `shell_execute` with `x=~/.netclaw/config/secrets.json; cat "$x"`
- **THEN** authorization returns `Denied` with reason `shell_references_protected_path`

#### Scenario: A stored grant decides outside the trusted roots in an unattended run

- **GIVEN** an unattended Personal run in Approval mode and the default Personal profile
- **AND** a folder grant for `make` in an external directory
- **WHEN** the model calls `shell_execute` with `make` in that directory
- **THEN** the call is allowed by the stored grant, as in a chat

#### Scenario: An unattended run without a grant stays denied

- **GIVEN** an unattended Personal run in Approval mode and no grant
- **WHEN** the model calls `shell_execute` with `make` in an external directory
- **THEN** the call is denied with `approval_required_unattended`
- **AND** a chat of the same audience would prompt for the same call

#### Scenario: An unattended run has the file reach of a chat

- **GIVEN** an unattended Personal run and the default Personal profile
- **WHEN** the model calls `file_read` on a file outside the session and project
- **THEN** the path access decision is the same as for an interactive Personal session

#### Scenario: A bounded profile confines an unattended run

- **GIVEN** a Personal profile with `WriteFiles` mode `Roots` and an unattended run
- **WHEN** the model calls `shell_execute` with a working directory outside those roots
- **THEN** the call is denied with `shell_working_directory_outside_trust_zone`
- **AND** a covering stored grant does not change the denial

#### Scenario: A grant never opens a protected path

- **GIVEN** an unattended Personal run in Approval mode and a grant for `cat`
- **WHEN** the model calls `shell_execute` with `cat <config dir>/secrets.json`
- **THEN** the call is denied

#### Scenario: A brace word keeps the denial

- **GIVEN** a Personal run, attended or unattended
- **WHEN** the model calls `shell_execute` with `cat <config dir>/{netclaw,secrets}.json`
- **THEN** the call is denied, because Bash expands the word to `secrets.json`
- **AND** `cat <config dir>/netclaw.json` is not denied

#### Scenario: Program text keeps the denial in each spelling

- **GIVEN** a Personal run, attended or unattended
- **WHEN** the model calls `shell_execute` with `jq -n 'import "secrets" as $s {search: "<home>//config"}; $s'`, or with `<home>/./config`, `<home>/x/../config`, or `<home>/con'fig'` in the program text
- **THEN** the call is denied
- **AND** program text that names `<home>/x/../other` is not denied by this check

#### Scenario: Team does not get the shared sessions root

- **GIVEN** a Team session
- **WHEN** the model calls `file_read` on a file in another session's directory
- **THEN** the path access decision denies the read

#### Scenario: Link escape from a project base

- **GIVEN** a project directory whose ancestor is a link to `/outside`
- **WHEN** the model calls `file_read` with a relative path
- **THEN** the path access decision denies the read
- **AND** Netclaw does not retry against the session directory

#### Scenario: Restricted session cannot read a sibling session

- **GIVEN** a Team session
- **WHEN** the model calls `file_read` on the raw log of another session
- **THEN** the path access decision denies the read

#### Scenario: Personal session keeps cross-session read access

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_read` on a file in another session directory
- **THEN** the path access decision allows the read

### Requirement: TA-7 Shell analysis uses general syntax facts

Netclaw SHALL analyze a shell call with ShellSyntaxTree for the native shell
of the daemon: Bash on Linux and macOS, and the probed PowerShell dialect
(7.x or Windows PowerShell 5.1) on Windows. All stages of one call SHALL share
one analysis. The terms [candidate](../../../docs/spec/GLOSSARY.md#candidate),
[phrase](../../../docs/spec/GLOSSARY.md#phrase), and
[unresolved syntax](../../../docs/spec/GLOSSARY.md#unresolved-syntax)
have their glossary meaning.

- Netclaw SHALL derive one candidate from each complete command occurrence.
  Pipelines, lists, loops, and same-language nested shells SHALL NOT hide an
  occurrence. A cross-language payload (for example `pwsh -Command` under
  Bash) SHALL stay an argument of the host command.
- In Bash, each command inside `if`, `case`, `while`, `until`, or a
  background list (`server &`) SHALL get its own candidate and its own
  decision.
- Candidate identity SHALL use the static verb tokens that the parser gives.
  Netclaw SHALL NOT parse the private subcommands, options, or operands of an
  executable. Safe-verb lists and deny lists are policy data.
- `Exact` and `FiniteSet` effective path values and authored filesystem
  values SHALL enter path policy. `AuthoredPathShape` alone SHALL NOT create
  filesystem authority.
- Non-path operands: an absolute word below a top-level directory that does
  not exist on the host names no file. It SHALL have no path scope, and its
  candidate SHALL use the working directory. An example is the API route in
  `gh api /repos/o/r/actions/jobs/1/logs`. Netclaw SHALL probe the top-level
  directory only when the working directory exists on the host. A top-level
  link or a probe failure SHALL keep the path scope. A URL SHALL have no path
  scope. A grant keeps its folder and repository scope (owner decision D3).
- A path word with a control character, for example multi-line `python3 -c`
  code, SHALL NOT make the call unresolved. Its scope SHALL be the deepest
  ancestor directory of its text before the first control character. The
  scope SHALL hold no control character, so the prompt can show it.
- Glob path facts (Bash 5.2 host): the scope of a glob word SHALL be its
  covering directory. Netclaw SHALL walk the directories that each segment
  can match, to a bound of 4096 directories, and every match SHALL stay inside
  the covering directory. A link that leaves the covering directory, or a walk
  that fails, SHALL keep the candidate exact with `Once` and `Deny` only. A
  glob with a wildcard in a directory segment (`*/notes.md`) can expand to an
  option word, so decision D1 SHALL apply to it. A leaf glob (`*.cs`) SHALL
  keep its path rule.
- Variable values: a word that reads a proved variable value SHALL get the
  decision of the literal value, in path policy and in hard deny (TA-5). A
  name with a run-time value (`PID=$!`, `x=$(cmd)`, `read x`) SHALL be
  unknown. A command that reads it as a word SHALL be one exact candidate with
  `Once` and `Deny` only, and SHALL get no rewrite advice. The data-position
  rule below is the exception: an `echo` or `printf` operand that reads it
  SHALL be data.
- An unresolved command (a dynamic command name, an unknown value, an
  unresolved path or redirect, a command after an unproved directory change
  such as `cd "$x"`, `pushd`, `popd`, or a failed `cd`) SHALL produce one
  exact candidate: its source text, with no reusable grant. In a Bash session,
  attended or not, each other command of the call SHALL keep its own
  candidates and coverage.
- Bracket-word rule: a program word that is a literal bracket pattern (for
  example `["ci","build"]`), with no command words and no other word except a
  redirect, SHALL be unresolved, because Bash expands the pattern. Brace text
  and regex text in the program word (`{"b":2}`, `^\d{4}$`) SHALL NOT be
  unresolved by this rule; they keep the rewrite advice of a command with
  unknown command words. This rule SHALL NOT apply to the `[` test builtin
  (`[ -d /work ]`); the data-position rule below applies to it.
- A source that does not split into commands (incomplete control flow, a
  command-resolution mutation such as `alias` or `hash`, `&&` under Windows
  PowerShell 5.1, unresolved PowerShell syntax) SHALL allow only a one-time
  consent for the whole call.
- Data-position rule: in Bash, a dynamic operand of an output command
  (`echo`, `printf`, `:`, `true`, `false`) SHALL be data, not unresolved
  syntax. A `printf` operand SHALL be data only after a literal format, and
  `printf -v` SHALL stay unresolved. A command substitution inside the operand
  SHALL be its own command with its own candidate, and a redirect target SHALL
  keep its own check. PowerShell SHALL keep only the bare `$?` rule.
- Test builtins: in Bash, `test` and `[` SHALL be data commands. Each operand
  SHALL be data only when the parser proves an exact value or a finite set
  and no value has a `[`. Bash evaluates an array subscript in a `-v` operand
  as arithmetic, and the arithmetic runs a command substitution. Thus an operand with a `[`, an
  unknown value, or a glob or file-name value SHALL make the command one exact
  candidate. Netclaw SHALL NOT parse the test operators. A path operand of a
  test builtin SHALL NOT be a scope. The protected-path screen SHALL still
  deny a literal or proved protected path.
- A Bash data command with no redirect SHALL get no assignment digest only
  when each operand is proved data. An output operand SHALL be proved data
  with an exact value, a finite set, or one double-quoted raw word (Bash does
  no pathname expansion inside double quotes). A test operand SHALL need an
  exact value or a finite set with no `[`. Any other data command, and a data
  command with a redirect, SHALL keep its digest. Thus
  `n=$(cmd); echo "$n"` is data, and `d=key; echo ../netclaw/"${d}s"/*` needs
  consent, because its literal twin is denied.
- `continue` and `break` inside a loop SHALL stay unresolved until
  ShellSyntaxTree accepts them. ShellSyntaxTree 0.4.0-beta.17 rejects them.
- Owner decision D1: a command whose command words are known and whose only
  unknown part is an operand value SHALL be covered by a reviewed safe phrase
  or by a grant for anywhere, attended or unattended (D2). A folder,
  repository, or chat grant SHALL NOT cover it. An unknown program word, an
  unknown redirect target, and a link SHALL keep the prompt; an unattended
  run SHALL deny it with `approval_required_unattended`.
- Accepted D1 gap: an unknown operand value can name a path that the
  protected-path text screen cannot see. A grant for anywhere already lets a
  literal operand name any readable path, so the gap adds only this case.
- A bounded assignment fact SHALL qualify a reusable grant with a SHA-256
  digest of the canonical assignment facts. A changed assignment SHALL need
  separate authority. An assignment inside an opaque fallback wrapper SHALL
  stay one-time only.
- A working directory with a `..` segment SHALL be denied with
  `shell_invalid_working_directory`.
- An internal exception, an impossible state, or an inconsistent actor result
  SHALL deny with `internal_policy_failure`.

Owner: `ShellCommandAnalysis` and `ShellApprovalMatcher` own the syntax facts,
the scopes, and the candidates. `ShellPolicyCoordinator` and
`ReviewedSafeShellPolicy` own the D1 coverage rule. All of this state is
call-local. The analysis keeps no state between calls.

#### Scenario: Compound command keeps every occurrence

- **GIVEN** an interactive Personal session with no grants
- **WHEN** the model calls `shell_execute` with `git status && npm test`
- **THEN** authorization returns `RequiresApproval`
- **AND** the candidates contain `git status` and `npm test` as separate phrases

#### Scenario: Each command in a control-flow construct gets its own decision

- **GIVEN** an interactive Personal session with no grants (catalog cases `if-statement-prompts-for-each-command` and `case-statement-uses-reviewed-phrases`)
- **WHEN** the model calls `shell_execute` with `if test -f marker; then git push; else git fetch; fi`
- **THEN** authorization returns `RequiresApproval` with the candidates `git push` and `git fetch`
- **AND** `test -f marker` is a data command, so it needs no grant
- **AND** `case x in a) cat a.txt ;; *) cat b.txt ;; esac` returns `Allowed` with allow reason `ReviewedSafePolicy`

#### Scenario: A command substitution is its own command

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `shell_execute` with `echo $(git push)` (catalog case `command-substitution-fails-closed`)
- **THEN** authorization returns `RequiresApproval` with the candidate `git push`
- **AND** the `echo` operand is data, so `echo` needs no grant

#### Scenario: A test builtin with bounded operands needs no approval

- **GIVEN** an interactive Personal session on the Bash 5.2 host with no grants (catalog cases `test-builtin-literal-operands-allows`, `test-builtin-bounded-variable-allows`, and `test-builtin-loop-value-allows`)
- **WHEN** the model calls `shell_execute` with `x=3; [ "$x" -gt 2 ] && echo yes`
- **THEN** authorization returns `Allowed` with allow reason `ApprovalExemptShellCandidates`
- **AND** `for d in a b; do [ "$d" = a ] && echo yes; done` returns the same result with no rewrite advice

#### Scenario: A test operand with a subscript is not data

- **GIVEN** an interactive Personal session on the Bash 5.2 host with no grants (catalog case `test-builtin-subscript-operand-prompts`)
- **WHEN** the model calls `shell_execute` with `[ -v 'a[$(printf marker >&2)]' ]`
- **THEN** authorization returns `RequiresApproval` with that exact candidate
- **AND** a run-time value such as `n=$(cmd); [ -v "$n" ]` gets the same result (catalog case `test-builtin-unknown-value-prompts`)

#### Scenario: A test builtin does not hide another command

- **GIVEN** an interactive Personal session on the Bash 5.2 host with no grants (catalog cases `test-builtin-guard-keeps-action-prompt`, `test-builtin-guard-keeps-hard-deny`, and `test-builtin-credential-path-denies`)
- **WHEN** the model calls `shell_execute` with `[ 3 -gt 2 ] && git push`
- **THEN** authorization returns `RequiresApproval` with the candidate `git push`
- **AND** `x=3; [ "$x" -gt 2 ] && rm -rf /` is denied with `hard_deny_system_destructive`
- **AND** `[ -f ~/.netclaw/keys/x ] && echo yes` is denied with `shell_references_protected_path`

#### Scenario: A run-time value in an output operand is data

- **GIVEN** an interactive Personal session on the Bash 5.2 host with no grants (catalog case `echo-substitution-value-is-data`)
- **WHEN** the model calls `shell_execute` with `n=$(git push); echo "$n"; printf '%s\n' "$n"`
- **THEN** authorization returns `RequiresApproval` with the candidate `git push` only

#### Scenario: An unquoted glob built from a variable needs consent

- **GIVEN** a Personal session on the Bash 5.2 host with no grants (catalog cases `output-glob-from-binding-prompts` and `output-glob-from-binding-unattended-denies`)
- **WHEN** the model calls `shell_execute` with `d=key; echo ../netclaw/"${d}s"/*`
- **THEN** an interactive call returns `RequiresApproval` with the candidate `echo`
- **AND** an unattended call is denied with `approval_required_unattended`

#### Scenario: A dynamic redirect target is not data

- **GIVEN** an interactive Personal session with grants for anywhere for `git status` and `printf` (catalog case `bash-substitution-redirect-target-fails-closed`)
- **WHEN** the model calls `shell_execute` with `git status > "$(printf result.log)"`
- **THEN** authorization returns `RequiresApproval` with the exact candidate `git status > "$(printf result.log)"`
- **AND** the prompt offers only `Once` and `Deny`

#### Scenario: An API route uses the folder grant of the working directory

- **GIVEN** an interactive Personal session with a folder grant for `gh api` in the project directory (catalog case `api-route-word-uses-project-folder-grant`)
- **WHEN** the model calls `shell_execute` with `gh api /repos/o/r/actions/jobs/1/logs` in the project directory
- **THEN** authorization returns `Allowed` with allow reason `StoredApproval`

#### Scenario: A word below an existing top-level directory keeps its path scope

- **GIVEN** an interactive Personal session with a folder grant for `gh api` in the project directory
- **WHEN** the model calls `shell_execute` with `gh api /usr/share/doc` in the project directory
- **THEN** the word keeps its path scope below `/usr`, because `/usr` exists on the host
- **AND** the folder grant does not cover the candidate

#### Scenario: A multi-line operand uses the scope of its clean text

- **GIVEN** an interactive Personal session with a folder grant for `python3` in the project directory (catalog case `multi-line-inline-code-uses-folder-grant`)
- **WHEN** the model calls `shell_execute` with multi-line `python3 -c` code in the project directory
- **THEN** authorization returns `Allowed` with allow reason `StoredApproval`
- **AND** without the grant, the prompt offers the reusable candidate `python3` (catalog case `multi-line-inline-code-offers-reusable-grant`)

#### Scenario: A glob that leaves through a link keeps one-time options

- **GIVEN** an interactive Personal session with a grant for anywhere for `cat` (catalog case `glob-intermediate-symlink-scope-fails-closed`)
- **AND** a directory under `artifacts` that is a link to a directory outside the covering directory
- **WHEN** the model calls `shell_execute` with `cat artifacts/*/secret.txt`
- **THEN** authorization returns `RequiresApproval` with one exact candidate and only `Once` and `Deny`

#### Scenario: D1 applies to a glob with a wildcard directory segment

- **GIVEN** an interactive Personal session (catalog cases `glob-that-may-add-option-uses-global-grant`, `glob-that-may-add-option-prompts-with-folder-grant`, and `glob-that-may-add-option-unattended-uses-global-grant`)
- **WHEN** the model calls `shell_execute` with `rm */stale.tmp`
- **THEN** a grant for anywhere for `rm` allows the call
- **AND** a folder grant for `rm` gives a prompt with the exact candidate `rm */stale.tmp`
- **AND** an unattended call with the grant for anywhere is allowed, as in a chat (D2)

#### Scenario: A run-time value gets a one-time prompt

- **GIVEN** an interactive Personal session with a grant for anywhere for `kill` (catalog case `background-process-id-kill-prompts`)
- **WHEN** the model calls `shell_execute` with `server & PID=$!; kill "$PID"`
- **THEN** authorization returns `RequiresApproval` with the candidates `server` and the exact `kill "$PID"`

#### Scenario: A bound value is not covered by a grant for another value

- **GIVEN** an interactive Personal session with a grant for anywhere for `git push origin feature-x` (catalog case `assigned-branch-is-not-covered-by-another-branch-grant`)
- **WHEN** the model calls `shell_execute` with `b=main; git push origin "$b"`
- **THEN** authorization returns `RequiresApproval`

#### Scenario: An unknown operand under a grant for anywhere

- **GIVEN** an interactive Personal session with a grant for anywhere for `kubectl get pods`
- **WHEN** the model calls `shell_execute` with `kubectl get pods -l "app=$(whoami)"` (catalog case `unknown-operand-global-grant-allows`)
- **THEN** authorization returns `Allowed`
- **AND** the same call with a folder grant prompts with one exact candidate and only `Once` and `Deny`

#### Scenario: A bracket program word stays unresolved

- **GIVEN** an unattended Personal session with no grants (catalog case `unattended-bracket-program-word-denies`)
- **WHEN** the model calls `shell_execute` with `["ci","build"]`
- **THEN** authorization returns `Denied` with reason `approval_required_unattended`
- **AND** in an interactive session, `["batch one"]` gives a prompt with only `Once` and `Deny` (catalog case `bracket-program-word-with-space-stays-unresolved`)

#### Scenario: A brace program word keeps its rewrite advice

- **GIVEN** an unattended Personal session in Approval mode with no grants (catalog case `unattended-brace-program-word-gets-rewrite-advice`)
- **WHEN** the model calls `shell_execute` with `{"b":2,"nested":{"c":3}}`
- **THEN** authorization returns `RequiresAgentCorrection`
- **AND** the bracket-word rule does not deny the call

#### Scenario: Unresolved syntax in a headless run

- **GIVEN** a headless Personal session with `shell_execute` in `Approval` mode
- **WHEN** the model calls `shell_execute` with `cat "$FILE"`
- **THEN** authorization returns `Denied` with reason `approval_required_unattended`
- **AND** no prompt is shown

### Requirement: TA-8 Every candidate needs coverage

A call SHALL run without a prompt only when every candidate has
[coverage](../../../docs/spec/GLOSSARY.md#coverage). Coverage sources
SHALL be:

- a one-time consent for the exact blocked call (the retry passes every check
  again);
- a chat grant of the same session, or of a parent session for a subagent;
- a persistent grant: global (any directory), folder (the candidate's real
  scope is inside the folder, with no link below the grant root), or
  repository;
- [reviewed-safe policy](../../../docs/spec/GLOSSARY.md#reviewed-safe-policy),
  only for a catalog phrase, and only when the path rule below permits every
  known path. Under decision D1 it also covers an unknown operand value;
- under decision D1, a grant for anywhere for an exact candidate whose only
  unknown part is an operand;
- an approval-exempt data command with no directory scope and no assignment
  digest, while the store is available: an output command (`echo`,
  `printf`, `:`, `true`, `false`), or in Bash a test builtin (`test`, `[`).

The path rule for reviewed-safe policy SHALL NOT depend on the run (D2):

- A known path SHALL qualify when the audience profile lets a file tool read
  it (`ReadFiles`). The path SHALL be a host path of the shell's own style.
  Netclaw SHALL apply protection to the lexical path and to the link-resolved
  path, and a protected path SHALL never qualify. Such a path SHALL NOT need
  a project declaration for a reviewed phrase.
- Otherwise a known path SHALL qualify only inside the session and project
  roots.

A grant SHALL apply only to the audience and the tool that it names. A grant
for `shell_execute` SHALL NOT authorize another tool. A chat grant of one
session SHALL NOT cover another session. A global grant SHALL cover a phrase
in any directory. A folder grant SHALL NOT cover a candidate outside its
folder, and a new global grant SHALL NOT remove a folder grant.

A legacy exact-phrase grant (`LegacyExact`) SHALL cover a candidate when its
phrase equals all command words of the candidate. The display text of the
prompt SHALL NOT count. A candidate with more words or other words SHALL need
separate coverage.

A repository grant SHALL cover a candidate only when the candidate resolves to
an ordinary checkout or a Git-registered linked worktree of the same Git
common directory. Netclaw SHALL resolve that identity from disk at each check.
A repository grant for repository A SHALL NOT cover repository B. Netclaw SHALL
reject copied `.git` pointers, moved worktrees, external links, and a main
checkout that uses `--separate-git-dir`.

A non-shell tool SHALL have one candidate: its tool name, or a path-scoped
name for a control-plane write.

Owner: `ReviewedSafeShellPolicy` and `PathAccessPolicy.IsReadableByAudience`
own the reviewed-safe path rule; their result is call-local.
`ApprovalPatternMatching` owns the grant match, including the legacy rule; its
result is call-local. `ToolApprovalActor` holds chat grants (actor-local).
`ToolApprovalStore` holds persistent grants (durable).

#### Scenario: Folder grant stays inside its folder

- **GIVEN** a persistent folder grant for `git status` in `/work/a`
- **WHEN** the model calls `shell_execute` with `git status` in `/work/b`
- **THEN** authorization returns `RequiresApproval`

#### Scenario: A safe phrase reads a path outside the project in an interactive run

- **GIVEN** an interactive Personal session with the default profile and no grants (catalog case `safe-verb-external-allows`)
- **WHEN** the model calls `shell_execute` with `git status` in a directory outside the project
- **THEN** authorization returns `Allowed` with allow reason `ReviewedSafePolicy`

#### Scenario: A safe phrase does not leave the trusted roots in an unattended run

- **GIVEN** an unattended Personal run in Approval mode and no grant (catalog case `unattended-external-reviewed-safe-allows`)
- **WHEN** the model calls `shell_execute` with `git ls-tree feature` in an external directory that the profile may read
- **THEN** authorization returns `Allowed` with allow reason `ReviewedSafePolicy`, as in a chat (D2)
- **AND** under a bounded profile that cannot read the directory, the call is denied

#### Scenario: A legacy grant covers its own command words

- **GIVEN** a Personal `LegacyExact` grant for `dotnet list package`
- **WHEN** the model calls `shell_execute` with `dotnet list package --vulnerable --include-transitive`
- **THEN** the grant covers the candidate, although the prompt text is `dotnet list`

#### Scenario: A legacy grant does not cover other words

- **GIVEN** a Personal `LegacyExact` grant for `git merge-base`
- **WHEN** the model calls `shell_execute` with `git merge-base dev`
- **THEN** authorization returns `RequiresApproval`

#### Scenario: Repository grant covers a registered sibling worktree

- **GIVEN** a repository grant for `./scripts/bump.sh` created in worktree `w1` of repository `r`
- **WHEN** the model calls the same command in registered worktree `w2` of `r`
- **THEN** authorization returns `Allowed` with allow reason `StoredApproval`

#### Scenario: Repository grant does not cover another repository

- **GIVEN** a repository grant for `./scripts/bump.sh` in repository `r`
- **WHEN** the model calls the same command in unrelated repository `q`
- **THEN** authorization returns `RequiresApproval`

#### Scenario: Grant of another audience does not cover

- **GIVEN** a persistent Team grant for `git status`
- **WHEN** a Personal session calls `shell_execute` with `git status`
- **THEN** the Team grant does not cover the candidate

### Requirement: TA-9 Agent correction precedes a prompt and grants no authority

Agent correction SHALL run only after admission, hard deny, protected-path, and
shell analysis checks pass. It SHALL return `RequiresAgentCorrection` with one
or more typed corrections and SHALL NOT run the tool, show a prompt, or create
a grant. The model's replacement call SHALL start a new authorization attempt
and pass every check again.

- A shell call that runs one exact native-tool executable SHALL receive a
  native-tool correction before stored grants.
- A Personal call, attended or unattended, that authors a write under the
  platform temporary root SHALL receive a managed temporary directory
  correction when the ordinary result would ask for consent. Team and Public SHALL NOT receive
  the managed path.
- An exact leading Bash directory change for project work SHALL receive a
  one-call working-directory correction that does not rewrite the command.
- Corrections SHALL precede an `Auto` allow. Temporary advice SHALL keep
  stored-grant and one-time precedence.
- A repeated equivalent call after a managed temporary correction SHALL
  suppress the correction once and SHALL offer only `Once` and `Deny`.
- The parent session and a subagent SHALL use the same corrections.

#### Scenario: Temporary write gets a correction, not a prompt

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_write` on a path under the platform temporary root
- **THEN** authorization returns `RequiresAgentCorrection` with a managed temporary directory correction
- **AND** no prompt is shown

#### Scenario: Hard deny wins over a correction

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `shell_execute` with a hard-denied command that also writes under the temporary root
- **THEN** authorization returns `Denied`

#### Scenario: Retry after a correction asks with one-time options only

- **GIVEN** an armed managed temporary correction for an exact call
- **WHEN** the model repeats the same call
- **THEN** authorization returns `RequiresApproval`
- **AND** the prompt offers only `Once` and `Deny`

#### Scenario: A reviewed phrase in a readable folder gets no project correction

- **GIVEN** a Personal session, attended or unattended, with no grants
- **AND** the shell folder is readable by the audience but is not the declared project directory
- **WHEN** the model calls `shell_execute` with a reviewed phrase, for example `git status`, in that folder
- **THEN** authorization returns `Allowed` through reviewed-safe coverage
- **AND** the result has no correction that asks for `set_working_directory`

### Requirement: TA-10 Consent prompts offer only safe options

A consent request SHALL carry the tool name, a display text with secrets
removed, the requester, the candidates, the working directory, the offered
options, and the authorization attempt identifier. It SHALL NOT carry a
`DirectoryRoots` field.

The consent request SHALL also carry adopted-context provenance.
`HasAdoptedContext` SHALL be true for any non-empty adopted window. The
adopted speakers SHALL list every adopted sender, including the requester.
`HasThirdPartyAdoptedContext` SHALL be a separate flag and SHALL NOT trim that
list. Adopted context SHALL stay quoted background and SHALL NOT originate a
consent request.

The options SHALL come from this set, in this order, with these stable keys
and labels:

| Key | Label |
|---|---|
| `approve_once` | Once |
| `approve_session` | This chat |
| `approve_always` | Always here |
| `approve_repository` | This repository |
| `approve_everywhere` | Always anywhere (Always allow this tool for an MCP tool) |
| `deny` | Deny |

- The prompt SHALL offer only `Once` and `Deny` when any uncovered
  candidate has unresolved syntax or no reusable phrase, or when the call is
  a managed temporary retry.
- `Always here` SHALL be offered only for a shell call with a directory scope
  that is not shallow and not session-owned.
- `This repository` SHALL be offered only for a clean reusable shell phrase
  whose candidates all resolve to one Git common directory.
- An assignment-qualified prompt SHALL use the versioned keys
  `approve_assignment_{session,always,repository,everywhere}_v1` for reusable
  options. `Once` and `Deny` keep their keys.
- Labels SHALL fit in 76 characters. `Always anywhere` and `Deny` SHALL have
  danger styling where the channel supports it.
- Only the requester SHALL answer, unless the principal is verified
  automation. Netclaw SHALL reject an option that the request did not offer.
- A channel type that supports interactive approval (Slack, Discord,
  Mattermost, TUI, SignalR) SHALL render the options and a text fallback. A
  channel that cannot post a prompt SHALL answer `Deny` for that call.
- Decision D2: when nobody can answer (headless chat, a reminder, a webhook,
  or a sub-agent with no approval bridge), the authorizer SHALL turn a consent
  request into the denial `approval_required_unattended` and SHALL NOT
  prompt. The tool result SHALL say that nobody can answer a prompt in an
  unattended run and SHALL tell the agent to save an "Always" grant in a chat
  with the same audience. A stored grant that covers the call SHALL still
  allow it. No reason code `channel_does_not_support_approval` exists.
- After a person approves a prompt, the tool result that the model reads
  SHALL end with one line that names the
  [consent answer](../../../docs/spec/GLOSSARY.md#consent-answer):
  `[approval: once]`, `[approval: this chat only]`,
  `[approval: always in this folder]`, `[approval: always in this repo]`, or
  `[approval: always anywhere]`. The parent session and a subagent retry
  SHALL add the same line. A denial SHALL add no line. A call that a stored
  grant or a policy allows SHALL get no line.

Owner: `ToolAccessPolicy.BuildApprovalOptions` owns the options, and the
result is call-local. `ConsentAnswerCodec.AppendResultNote` owns the approval
note line. `SessionToolExecutionPipeline` and `SubAgentActor` add the line to
the call-local tool result. The journal keeps the consent request and the
answer (durable).

#### Scenario: Unresolved syntax offers one-time options only

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `shell_execute` with a command that has unresolved syntax
- **THEN** the prompt offers `Once` and `Deny` only

#### Scenario: Wrong requester cannot answer

- **GIVEN** a consent request from requester `U1`
- **WHEN** user `U2` selects `approve_once`
- **THEN** Netclaw rejects the answer and posts a warning
- **AND** the call does not run

#### Scenario: Headless run cannot ask

- **GIVEN** a headless Personal session with `shell_execute` in `Approval` mode
- **WHEN** the model calls `shell_execute` with an uncovered command
- **THEN** authorization returns `Denied` with reason `approval_required_unattended`
- **AND** the same command with a covering stored grant is allowed

#### Scenario: MCP tool prompt has no folder option

- **GIVEN** an MCP tool in `Approval` mode
- **WHEN** the prompt is built
- **THEN** it does not offer `Always here`
- **AND** its global option has the label `Always allow this tool`

#### Scenario: Self-only adopted window keeps its provenance

- **GIVEN** a turn whose adopted window holds only earlier messages of the requester
- **WHEN** a tool call in that turn asks for consent
- **THEN** the consent request has `HasAdoptedContext` true and lists the requester as an adopted speaker
- **AND** `HasThirdPartyAdoptedContext` is false

#### Scenario: An approved result names the choice

- **GIVEN** an interactive Personal session and a `shell_execute` prompt
- **WHEN** the requester selects `This chat`
- **THEN** the tool result that the model reads ends with `[approval: this chat only]`

#### Scenario: A denied or pre-approved call has no approval note

- **GIVEN** an interactive Personal session
- **WHEN** the requester selects `Deny`, or a stored grant allows the call with no prompt
- **THEN** the tool result has no `[approval: ...]` line

### Requirement: TA-11 An unanswered consent request survives restart

A parent-session consent request SHALL pause only its own tool call. Other
calls in the batch SHALL continue.

- The session SHALL journal `ToolApprovalRequested` with the turn context
  before it shows the prompt, and `ToolApprovalResolved` when the answer is
  accepted.
- No timer SHALL deny an unanswered request. The wait SHALL end only with an
  answer, a cancellation, or a new user message that abandons the parked
  batch.
- An unanswered request SHALL NOT keep the session in memory. After a daemon
  restart or passivation, an answer SHALL recover the session from its
  journal, apply the answer, and re-drive the parked batch once every sibling
  request has an answer.
- A re-drive SHALL use the persisted turn context and audience. It SHALL NOT
  execute a completed sibling call again.
- An answer for an unknown or already resolved call SHALL fail loud with an
  expired-prompt notice and SHALL NOT run the tool.

#### Scenario: Answer after restart resumes the call

- **GIVEN** a journaled consent request for `shell_execute` and a daemon restart
- **WHEN** the requester selects `approve_once`
- **THEN** the session recovers, re-drives the parked batch, and runs the call once

#### Scenario: Duplicate answer does not run the tool again

- **GIVEN** a consent request that already has an accepted answer
- **WHEN** a second answer arrives for the same call
- **THEN** Netclaw posts an expired-prompt notice
- **AND** the tool does not run again

### Requirement: TA-12 Subagent consent goes through the parent session

A subagent SHALL ask for consent through its parent session with the same
consent request contract, a parent-scoped correlation identifier, the parent
requester, and the inherited parent working directory. A subagent SHALL use
chat grants of its parent session, and its own chat grants SHALL NOT reach the
parent.

- A subagent consent request SHALL be live-only. After a daemon or parent
  restart, Netclaw SHALL reject the old prompt as expired and SHALL close the
  interrupted `spawn_agent` call.
- An answer that arrives after the subagent wait ends SHALL NOT run the tool.
- A subagent without a parent bridge SHALL NOT ask. Its run SHALL fail with
  `ToolExecutionFailed`.
- A one-time consent in a subagent SHALL cover only that exact call.

#### Scenario: Subagent prompt reaches the parent requester

- **GIVEN** a subagent spawned in an interactive Personal session
- **WHEN** the subagent calls `shell_execute` with an uncovered command
- **THEN** the parent channel shows a consent request for the parent requester
- **AND** the subagent resumes after the answer

#### Scenario: Subagent prompt expires after restart

- **GIVEN** an unanswered subagent consent request and a daemon restart
- **WHEN** the requester answers the old prompt
- **THEN** Netclaw rejects the answer as expired
- **AND** the tool does not run

### Requirement: TA-13 The persistent grant store fails closed

Persistent grants SHALL live in `~/.netclaw/config/tool-approvals.json` with
`version` 3 and an `audiences` map keyed by audience, then by tool. Each entry
SHALL have one closed form: token-prefix shell, legacy-exact shell, or
non-shell exact. A shell entry MAY carry `repository`, `repositoryWorktree`,
and a `sha256:` `assignmentDigest`. A null `directory` SHALL mean global.

- The first load of a version 2 file SHALL write a byte-identical
  `.v2.bak`, then convert valid shell entries to `LegacyExact` without
  token-prefix authority. A version 1 file SHALL be quarantined and replaced by
  an empty version 3 file. A future version SHALL stay byte-identical.
- A malformed, partial, or future-version file SHALL make the store
  unavailable. A call whose candidate needs a persistent grant SHALL then be
  denied with `approval_store_unavailable`. A chat grant or a one-time consent
  SHALL still cover its candidate.
- The store SHALL reject duplicate members, unknown audiences, noncanonical
  tool keys, and control or bidi characters.
- An equivalent new entry SHALL keep the existing entry and its `createdAt`.
- The daemon SHALL read the file at each check and SHALL NOT restart when it
  changes.

#### Scenario: Version 2 upgrade adds no authority

- **GIVEN** a version 2 store with shell entry `dotnet build`
- **WHEN** the daemon loads the store
- **THEN** it writes `tool-approvals.json.v2.bak`
- **AND** the entry becomes a `LegacyExact` grant that does not cover `dotnet build --no-restore`

#### Scenario: Malformed store denies an uncovered call

- **GIVEN** a malformed `tool-approvals.json`
- **WHEN** the model calls `shell_execute` with a command that no chat grant covers
- **THEN** authorization returns `Denied` with reason `approval_store_unavailable`

#### Scenario: Chat grant still covers with a malformed store

- **GIVEN** a malformed `tool-approvals.json` and a chat grant for `git status`
- **WHEN** the model calls `shell_execute` with `git status` in the same session
- **THEN** the chat grant covers the candidate

### Requirement: TA-14 Launch re-verifies the authorized call

A shell process SHALL start only for the exact authorized command, working
directory, and shell environment. The command policy, the path policy, and the
launch SHALL use one shell environment instance; a mismatch SHALL fail at
construction.

Before the process starts, the launch SHALL:

1. check hard deny and protected paths;
2. prepare the managed temporary directory and the child environment;
3. capture every known path target, including each proved Bash scope;
4. run the full authorization again for the exact call;
5. check hard deny and protected paths again;
6. deny with `shell_launch_paths_changed` when a captured path target changed;
7. start one process without another await.

The Bash child environment SHALL exclude startup hooks (`BASH_ENV`), imported
functions, and loader overrides (`LD_*`, `DYLD_*`). A queued call whose grant
was revoked SHALL fail without a process. These checks narrow filesystem races;
they SHALL NOT be described as an OS sandbox.

#### Scenario: Revoked grant stops a queued launch

- **GIVEN** a background `shell_execute` call that a persistent grant allowed
- **WHEN** the operator revokes the grant before the launch starts
- **THEN** the launch fails and no process starts

#### Scenario: Link change during authorization

- **GIVEN** an authorized shell call whose path target is a directory
- **WHEN** that directory becomes a link before the final start
- **THEN** the launch fails with `shell_launch_paths_changed`

#### Scenario: Unchanged call starts once

- **GIVEN** an authorized shell call with no path changes
- **WHEN** the launch runs
- **THEN** exactly one process starts with the authorized arguments

### Requirement: TA-15 Authorization decisions are observable without an audit store

Each tool call SHALL have one `AuthorizationAttemptId` (`auth-<guid>`) that
joins the decision, the consent request, the answer, and the retry. A retry
after a consent answer SHALL keep the identifier. A replacement call after a
correction SHALL get a new one. The identifier SHALL contain no user data and
SHALL grant no authority.

- The daemon log SHALL record `Tool authorization evaluated: {ToolName}
  outcome={AuthorizationOutcome} ...` for each decision (Allowed at debug level
  with a reason; RequiresApproval and RequiresAgentCorrection at information
  level; Denied at warning level with `reason=`), and `Tool executed: ...` for
  each execution.
- Each shell decision SHALL log ordered `Shell policy trace:` rows with enum
  stage, outcome, reason, call-local candidate ID, a redacted executable name
  of at most 128 UTF-16 code units, coverage, scope relation, and grant time.
  The trace SHALL hold at most 256 rows and SHALL add one `TraceTruncated` row
  on overflow without a change to the decision. It SHALL NOT hold commands,
  argument values, raw paths, secrets, or model content, and SHALL NOT enter a
  prompt or the journal.
- Netclaw SHALL NOT keep a separate tool audit store. The durable records are
  the journal events and the grant store.

#### Scenario: Denied decision is logged with its reason

- **GIVEN** a Personal session and a hard-denied command
- **WHEN** authorization denies the call
- **THEN** the daemon log has a warning line `Tool authorization evaluated: shell_execute outcome=Denied reason=hard_deny_self_destructive`

#### Scenario: Trace never holds a secret

- **GIVEN** a shell command whose executable text contains a private key
- **WHEN** Netclaw writes the shell policy trace
- **THEN** the trace row does not contain the key text

### Requirement: TA-16 Operators manage grants through the CLI and doctor

The `netclaw approvals` command SHALL list, add, and revoke grants per audience
and tool. `list` SHALL show token-prefix, legacy-exact, and repository grants
with distinct labels. `trust-verb` SHALL accept one complete static shell
phrase and SHALL reject a flag, a redirect, an assignment, a dynamic command
name, and a compound command. `revoke` SHALL reject an ambiguous
cross-audience form. The command SHALL fail closed on an invalid store.

`netclaw doctor` SHALL warn when `shell_execute` is in `Approval` mode but the
shell mode is `Off`, when shell grants exist but shell is disabled, and when
Personal sets `shell_execute` to `Auto` while the host shell is enabled. It
SHALL report an error for an unrestricted Public or Team profile.

#### Scenario: trust-verb rejects a compound command

- **WHEN** the operator runs `netclaw approvals trust-verb "git status && rm x" --shell bash`
- **THEN** the command fails and stores nothing

#### Scenario: Doctor warns about an explicit Personal shell Auto

- **GIVEN** `Tools.ShellMode` `HostAllowed` and a Personal `shell_execute` override of `Auto`
- **WHEN** the operator runs `netclaw doctor`
- **THEN** the audience profile check reports a warning
