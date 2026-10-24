# Shell launch value assignment proof

## Scope

This note records the proof for a narrow Bash assignment-digest change.
It does not add an executable rule or a `HOME` rule.
The baseline is `aecbe73e1714e9acc2d68612e0cfa0d3abc4f33d`.
Netclaw uses ShellSyntaxTree `0.4.0-beta.24`.

## Baseline

The direct source is `export DOTNET_ROOT=$HOME/.dotnet; dotnet test`.
The launch environment gives the parser the same snapshot as the child shell.
`ShellCommandAnalyzer.Analyze` supplies that launch data to direct source.
The parser reports `AuthoredValue=Unknown` for the assignment.
The parser reports `EffectiveValue=Exact` for the launch-derived value.
The parser reports `ShellState` scope and process-environment reach.
The assignment has a source span in the submitted command.

At baseline, `ShellAssignmentDigestFactory.TryCreate` rejected this fact set.
The factory required exact authored and effective values.
The literal assignment could receive a digest.

## Parser contract evidence

The cached beta.24 package contains no source or XML documentation.
I inspected its NuGet README and decompiled its net8.0 assembly.
I did not validate the package signature.
The assembly hash is `926803b482f369c1698ecc47e20ea70d54885ca69fc04c8d9e29ce220a7ef198`.
The README hash is `9a573e620384f6d56933fc1b9f7fd7a7a3280c3f21f3129499b78b183b9e2f81`.
ILSpy and ICSharpCode.Decompiler report version `10.1.0.8386`.
The separate local ShellSyntaxTree checkout is older. Its path is redacted.
I do not use that checkout as beta.24 proof.

The beta.24 assignment record exposes name, authored value, effective value, scope, export reach, and source span.
The parser derives an assignment's effective value from source bindings or approved launch facts.
The parser accepts only one exact value for an exact assignment fact.
The parser does not execute command substitutions.
It reports unknown for an assignment value with a command substitution.
The parser joins branch states and keeps only equal assignment facts.
The parser marks an assignment unknown when the command may receive an unproved environment value.
The parser reads launch values only under its supported initial-state contract.
The Bash launch lookup accepts `HOME` and `TMPDIR`.
Other exact values must come from source assignments.
The launch environment also declares complete environment names.

Netclaw supplies launch data only to direct shell source.
The wrapper child path passes no launch data to its child source.
The wrapper tests require an assignment inside a fallback shell to stay one-time.
The existing matcher rejects an unqualified grant when a candidate has an assignment digest.
`ShellExecutionEnvironment` selects fresh Bash state only for Bash 5.2 or 5.3.
An unknown Bash host does not get launch-derived value facts.
The PowerShell assignment grammar accepts bounded single-quoted literal assignments.
Its authored and effective assignment values both report the literal value.

## Safety screens

The digest format binds the shell, assignment name, scope, process-environment reach, and effective value.
The digest does not bind an executable name.
The digest does not bind a special variable name.
The command analyzer keeps source and effect facts in the parser-owned occurrence.

The factory now accepts an exact effective value from Bash analysis when the
authored value is unknown. PowerShell still needs exact authored and effective
values. The factory keeps the same digest format and effect fields.

The hard-deny policy checks each parsed Bash occurrence and its effective values.
The file policy checks each path before consent.
The candidate matcher checks assignment identity and path scope.
The actor coordinator checks all candidates before it reuses a grant.
This note does not weaken any of these checks.

## Implementation tests

`ShellAssignmentDigestTests` now contains a baseline-failing positive case.
The test checks authored and effective values, process-environment reach, and the source span.
The test checks the digest and candidate identity after the proof gate accepts the contract.
The test checks that a plain grant does not match the qualified candidate.
The test compares the launch-derived form with its literal twin.
The launch form and its same-value literal twin have the same digest.
The literal `/other/.dotnet` value has a different digest.
An in-command `HOME` change makes beta.24 reject the source as unparseable.
The whole call then stays messy and has no reusable candidates.
The negative cases check command substitution, `unset`, `read`, a branch, and dot-source.
The wrapper case includes launch-derived `HOME` inside a child shell.
The controls check the unknown Bash host and both PowerShell dialects.
The path control checks an exact launch-derived operand against a protected path.
The actor test stores candidates from the literal twin, then reuses them for
the launch-value form. It rejects the changed literal value and denies a
protected `.ssh/authorized_keys` path.

The focused baseline command was:

```text
flock /tmp/netclaw-shell-proof-build.lock dotnet test src/Netclaw.Security.Tests/Netclaw.Security.Tests.csproj --filter FullyQualifiedName~ShellAssignmentDigestTests --logger 'console;verbosity=minimal'
```

At that baseline run, the class had 54 tests.
The run passed 52 tests and failed 2 new positive tests.
Both failures stop at `ShellAssignmentDigestFactory.TryCreate`.
The first failure shows the authored-exact guard rejects the launch-derived value.
The second failure shows the same guard rejects the launch-derived identity comparison.
All parser fact and source span assertions before that guard passed.
The branch, substitution, `read`, `unset`, dot-source, wrapper, unsupported-host, PowerShell, and path controls passed.
The candidate-matching assertions did not run after the expected factory failure.

After the narrow factory change, the focused security class passed 63 tests.
The actor launch class passed 24 tests.
The focused mutation class passed 9 tests.
The new cross-shell mutation control uses a synthetic PowerShell fact pair:
unknown authored value and exact effective value. Beta.24 does not emit that
pair for PowerShell source, so the test protects the factory boundary itself.

## Context probes

I used the cached Netclaw test assemblies with `dotnet script`.
Reflection called the existing internal launch-environment and parse methods.
The probe did not execute any submitted command.

| Source shape | Parser result | Assignment facts | Matcher result |
| --- | --- | --- | --- |
| Unknown Bash: `mode=fast; export other=$mode; inspect item` | Unresolved, zero commands | No assignment facts | Messy, zero candidates |
| Unknown Bash: `export mode=fast; export other=$mode; inspect item` | Unresolved, zero commands | No assignment facts | Messy, zero candidates |
| Bash 5.2: unexported `mode` then exported `other` | Resolved, two commands | `other` is Unknown/Exact(`fast`), and reaches the process | Exact fallback; export is `Operand`, inspect is `Command` |
| Bash 5.2: exported `mode` then exported `other` | Resolved, three commands | Both assignments reach the process; `other` is Unknown/Exact(`fast`) | Exact fallback; export-other is `Operand`, inspect is `Command` |
| PowerShell 7: `$first='fast'; $mode=$first; inspect item` | Unresolved, zero commands | No assignment facts | Messy, zero candidates |
| PowerShell 7: `$env:MODE=$HOME; inspect item` | Unresolved, zero commands | No assignment facts | Messy, zero candidates |
| PowerShell 7: `$mode='fast'; inspect item` | Resolved, one command | `mode` is Exact(`fast`)/Exact(`fast`) | One normal candidate with a digest |
| Bash 5.2: `export PATH=$HOME/.launch-value; dotnet test` | Unparseable: `bounded Bash assignment names must use the ordinary scalar boundary` | Zero commands | Messy, zero candidates |
| Bash 5.2: `export LD_PRELOAD=$HOME/.launch-value; dotnet test` | Unparseable: same assignment-name boundary | Zero commands | Messy, zero candidates |
| Bash 5.2: `HOME=/other; export DOTNET_ROOT=$HOME/.dotnet; dotnet test` | Unparseable: same assignment-name boundary | Zero commands | Messy, zero candidates |
| Bash 5.2: `export GIT_DIR=$HOME/.launch-value; git status` | Resolved, two commands | `GIT_DIR` is Unknown/Exact(`[REDACTED: resolved home path]`) and reaches the process | Two normal candidates; `git status` has a digest |
| Bash 5.2: `export DOTNET_ROOT='/other/.dotnet'; dotnet test` | Resolved, two commands | Exact/Exact(`/other/.dotnet`) | Two normal candidates; `dotnet test` has a different digest |

The two Bash source-binding shapes publish an exact value with unknown authored text.
Their command candidates still lack complete reusable command identity.
The unknown Bash host and dynamic PowerShell forms publish no candidate.
These probes do not show that every exact effective value can gain a grant.
The matcher still requires complete command and effect facts.

## Netclaw source pointers

- `ShellExecutionEnvironment.cs:258-315` passes launch facts for direct source.
- `ShellExecutionEnvironment.cs:514-537` selects supported host state.
- `ShellCommandAnalysis.cs:262-275` passes no launch facts to wrapper source.
- `ShellAssignmentDigestFactory.cs:47-71` requires exact values and hashes effect fields.
- `ShellCommandPolicy.cs:224-235` checks each Bash command and effective argument.
- `ToolPathPolicy.cs:366-395` checks protected paths before consent.
- `ShellAssignmentDigestTests.cs` contains the baseline and safety tests.
- `ShellLaunchEnvironmentApprovalTests.cs` contains actor-level plain-grant and qualified-grant controls.

## Decision

The factory accepts exact effective Bash facts and keeps the PowerShell authored-value gate.
The matcher still requires complete command identity, assignment digest, and path scope.
The launch-to-literal actor test proves the stored digest has the same meaning in both forms.
Changed or unresolved source facts keep the call one-time or require approval.

## Separate loop study

ShellSyntaxTree beta.24 and stable `0.4.0` both reject a `while` command with a trailing input redirect.
The loop without that redirect parses, but `$n` remains unknown.
The trailing redirect would need a generic compound filesystem-effect fact.
Netclaw would then need to pass the input path through file authority.
This study does not propose a ShellSyntaxTree edit or a package update.
