# Compound input redirect feasibility

## Decision

A focused ShellSyntaxTree change is justified. Bash accepts redirects after compound commands, but both pinned parser packages reject them.

The parser must keep an outer redirect on its compound owner. It must not copy that redirect onto every inner command occurrence.

This parser change adds no variable resolution. Existing caller-supplied launch facts can already prove `$HOME` or another variable in a redirect target. A target remains unknown when its value lacks proof.

## Source and probe basis

The Netclaw incident baseline pins `ShellSyntaxTree` `0.4.0-beta.24` in `Directory.Packages.props:83`. Its nuspec points to source commit `49c54a1eb7ee3121aae804f34b2ea0ea6c380500`.

The stable `0.4.0` nuspec points to `e3aadccfe8a9daef45dffedfb7cb45f255939490`. The parser and API files below have no changes between those commits.

The cached package assemblies have these SHA-256 digests:

| Package | Digest |
|---|---|
| `0.4.0-beta.24` | `926803b482f369c1698ecc47e20ea70d54885ca69fc04c8d9e29ce220a7ef198` |
| `0.4.0` | `028836bb8448d4a1ba7e3d15d850aa2d831306def443e5eacae7a974dbbb8a41` |

The parser probes used both cached packages. GNU Bash `5.2.21` checked the same source strings with `bash -n`; it did not run them.

| Source shape | Pinned parser | Stable parser | Bash `-n` |
|---|---|---|---|
| `cat input.txt < input.txt` | Parsed; one exact input redirect | Same | Accepts |
| `for item in one two; do cat "$item"; done < input.txt` | Unparseable at `<`, offset 42 | Same | Accepts |
| `while true; do cat input.txt; done < input.txt` | Unparseable at `<`, offset 35 | Same | Accepts |
| `if true; then cat input.txt; fi < input.txt` | Unparseable at `<`, offset 32 | Same | Accepts |
| `(cat input.txt) < input.txt` | Unparseable at `<`, offset 16 | Same | Accepts |
| `{ cat input.txt; } < input.txt` | Unparseable: reserved execution syntax lacks structural analysis | Same | Accepts |

The first four rows show a specific gap after supported `for`, `while`, and `if` nodes. The brace group has an earlier, separate structural gap.

Probe code and machine results stay outside the repository. Their local paths are redacted. The retained evidence compares the pinned and stable parser packages with Bash syntax checks.

## Parser gap

`BashStructuralCoordinator.TryParseCommand` dispatches `for`, `while`, `until`, and `if` to structural parsers. The `for`, condition-loop, and `if` parsers stop after `done` or `fi`.

`TryParseSubshell` stops after `)`. Each node span ends at that token. The parser leaves the following `<` token in the input.

`TryParseList` then reports `unexpected token at position ...` because `<` is not a list operator. The parse result has no command occurrences.

The AST confirms the missing owner. `ForEachSyntax`, `ConditionLoopSyntax`, `ConditionalSyntax`, and `GroupSyntax` expose their body and control facts. None exposes redirects.

`CommandOccurrence.Redirects` holds redirects for one authored simple command. It cannot represent one redirect that applies to a whole loop, branch, or group.

Exact source references:

- Beta.24 `BashStructuralCoordinator.cs`: dispatch at lines 554–571; list rejection at lines 422–425; loop construction at lines 1226–1248 and 1309–1322; `if` construction at lines 1429–1437; subshell construction at lines 1915–1930.
- Beta.24 `ShellSyntaxNode.cs`: `GroupSyntax` at lines 202–214 and `ForEachSyntax` at lines 230–264.
- Beta.24 `ShellConditionalSyntax.cs`: `ConditionLoopSyntax` and `ConditionalSyntax` declarations.
- Beta.24 `CommandOccurrence.cs`: redirect property at lines 99–104.
- The stable source commit has the same relevant parser and API lines.

ShellSyntaxTree `SPEC.md` describes redirect facts on command occurrences. It does not define an outer redirect effect on compound nodes. The package API and parser behavior therefore match the documented gap.

## Netclaw owner and security boundary

Netclaw owns path authority. ShellSyntaxTree must publish syntax facts only; it must not decide whether a path is allowed.

`ShellPolicyOccurrencePathFacts.Create(CommandOccurrence)` reads each occurrence redirect and makes a path fact. `ToolPathPolicy.CommandReferencesDeniedPath` applies protected-path checks to the full analysis. `ToolAccessPolicy.ScreenProtectedShellText` invokes that check.

Redirect authority also has a distinct gate. `ToolAccessPolicy.ScreenNoProgramRedirects` checks redirect path resolution, read access, and the audience policy.

An integration change must pass compound redirect facts to these existing checks. It must preserve hard-deny checks, path scope, source proof, and the existing consent behavior.

An outer redirect must not masquerade as an inner command redirect. For example, the loop redirect in `for f in a b; do cat "$f"; done < input.txt` applies to the loop’s input stream. The `cat` operand and the loop redirect remain distinct facts.

## Smallest useful boundary

Reuse the existing `RedirectAnalysis` family for the redirect operator and its target. Add a generic AST owner for a redirect that belongs to a compound construct.

The contract must retain the exact source span, redirect order, target domain, mode, and completeness. It must leave unknown targets unknown. It must not assign outer redirects to child `CommandOccurrence` records.

Netclaw can then collect redirect effects from both simple command occurrences and compound owners. It must check every file redirect with the existing path and file-operation policies.

The parser can support the already modeled `for`, `while`, `until`, `if`, and subshell forms first. Brace groups need separate structural support before the parser can safely represent their outer redirects.

## Examples and limits

Positive syntax example:

```bash
for item in one two; do cat "$item"; done < input.txt
```

Bash accepts this form. The parser should expose one exact input redirect on the loop node, while it keeps the body command occurrence separate.

Negative authority example:

```bash
for item in one two; do cat "$item"; done < "$INPUT"
```

The redirect structure may be known, but the target value is not. Netclaw must keep the path unresolved and must not grant file access from this syntax fact.

An inner redirect remains a separate positive control:

```bash
for item in one two; do cat "$item" < per-item.txt; done
```

That redirect belongs to the `cat` occurrence. It must use the existing occurrence redirect path.

This report does not show that parser support alone fixes every unknown-read-variable prompt. Existing launch facts can prove supplied values such as `$HOME`; unsupported or unsupplied values remain unknown. Netclaw path policy still controls file access.

## Recommendation

At the time of this feasibility review, the recommendation was to add generic parser facts first, then consume them through Netclaw's existing path authority.

The later parser release and Netclaw consumer work are recorded below. The consumer must prove that compound effects reach protected-path screening and file-read authorization before any approval-relief claim.

## Release update on 24 October 2026

[ShellSyntaxTree PR 253](https://github.com/Aaronontheweb/ShellSyntaxTree/pull/253) merged as `8ce9d24da81adbbeca75e643e0a20a868e901f2f`.
Its source tree matches the reviewed parser tree at `0a28ecb4ebfc7bbe4d3640d6e44e66aeac621f4f`.
Merged Linux and Windows CI passed. Publish workflow `40058785172` succeeded.
Public prerelease `0.5.0-beta.1` supports both `netstandard2.0` and `net8.0`.
The NuGet package records source commit `8ce9d24da81adbbeca75e643e0a20a868e901f2f`.
Its package SHA-256 is `94c901112edeb3eb97a93f2c8f2194cd60e0866e0756d9b47ea9d4706db4f2af`.
Both assembly payloads match the GitHub release asset. Package validation does not change the live daemon or saved grants.

The default-off option preserves existing consumer behavior.
The opted-in syntax reports file and descriptor redirects, source spans, and the directory at compound entry.
Outer heredocs, here strings, target substitutions, brace groups, and decoded child wrappers remain unsupported.

A separate baseline probe found a denial gap for rejected source.
A synthetic probe used `while false; do :; done < "$(sudo cat secret)"`. The word `secret` is a fixture name.
The ordinary `cat` substitution control also requests consent.
The probes evaluate authorization only; they execute no command.
An unparseable result therefore does not prove a nested hard denial.
This existing gap remains unresolved and does not qualify as a passed safety check.
