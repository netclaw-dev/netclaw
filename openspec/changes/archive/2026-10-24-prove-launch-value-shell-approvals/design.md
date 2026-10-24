## Context

At the study baseline, launch-derived assignments caused repeat prompts. The parser reported authored value, effective value, scope, export reach, and source span.
The approval store owned durable grants. Analysis and candidate decisions stayed call-local.

`ShellExecutionEnvironment` owned the launch snapshot and parser mode. `ShellCommandAnalyzer` owned source and effect accounting.
`ShellAssignmentDigestFactory` owned assignment identity. `ApprovalPatternMatching` owned phrase and scope matches.
`ShellPolicyCoordinator` owned final candidate coverage.

See [the proposal](proposal.md) for scope. Reuse the current parser, launch snapshot, and authority checks.

## Decisions

### Keep diagnostics separate from grant matching

Valid legacy entries do not store `VerbTokens`. Use the entry's canonical phrase for bounded near-miss diagnostics.
Keep `PhraseMatches`, `MatchesChain`, scope checks, and reviewed single-token program exceptions unchanged.
Preserve result bounds, redaction, and reason order. Test the explanation and the actual match result.

### Require source and effect proof before digest reuse

The baseline was `aecbe73e1714e9acc2d68612e0cfa0d3abc4f33d`. It used ShellSyntaxTree `0.4.0-beta.24`.
Direct source received the launch snapshot that the child process used. Wrapper child source received no launch values.
The proof applies only to supported fresh Bash 5.2 or 5.3 launch facts.

Compare these forms under one launch snapshot:

```bash
export DOTNET_ROOT=$HOME/.dotnet; dotnet test ./sample.csproj | head
export DOTNET_ROOT=/home/test/.dotnet; dotnet test ./sample.csproj | head
```

For the first form, the parser reports an exact effective value and an unknown authored value.
A literal assignment reports exact authored and effective values.
The factory accepts the first form only when source, value, scope, export reach, syntax, and nested effects have complete proof.
The digest keeps the shell, name, scope, export state, and effective value.

The proof review accepted only the direct, supported Bash case. PowerShell keeps its authored-value check.

Schematic flow; normal admission and authority checks are omitted:

```text
launch snapshot + source
  -> parser facts and complete effect analysis
  -> existing assignment digest fields
  -> qualified candidate and existing path and grant checks
```

Positive case: the launch-derived and same-value literal forms share an assignment identity.
A grant with that identity may cover the candidate.
Negative cases: a plain grant, changed value, unknown host, wrapper source, substitution, or incomplete effects gain no reusable authority.
An unqualified grant does not cover a qualified assignment.
If a substitution invokes a hard-denied command, hard deny still wins.

Branch joins, `read`, `unset`, dot-source, and mutations keep values or effects unknown.
Those cases retain exact consent or the existing denial.

Protected paths, links, scopes, audience, and launch checks stay active.
Assignments that affect `PATH`, `LD_PRELOAD`, or `GIT_DIR` keep their environment qualifiers.
Unknown or unsupported PowerShell values keep their current consent.

### Keep corpus evidence bounded

Use the current runner. The repository corpus has 55,004 inputs. The historical archive has 55,144 inputs.
Keep their hashes and identities separate. Keep incident source private. Commit only reviewed, sanitized controls.
Map each control to its roots, grants, audience, host, and initial directory.

Review each changed candidate, consent option, outcome, and execution fact.
A diagnostic change may alter trace text, but it must preserve coverage and outcomes.
Synthetic grants do not reproduce a live approval store. The probe checks authorization only.
Do not execute corpus commands.

The assignment study kept compound redirects out of scope. The later compound change records that parser and consumer work.

## Migration

The digest and grant wire format stay unchanged. No saved grant needs migration or automatic revocation.
The current authorization architecture owns the implemented flow.
The operator waived runtime LLM evaluation for this change. The cancelled partial run is not a pass.

See [the current authorization architecture](../../../../docs/architecture/tool-authorization.md) and [the compound redirect design](../2026-10-24-consume-bash-compound-redirect-effects/design.md).
See [PR 2665](https://github.com/netclaw-dev/netclaw/pull/2665) for implementation and validation evidence.
