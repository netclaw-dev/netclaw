## Context

See [proposal.md](proposal.md) for the reason for this change. Current shell analysis keeps only `CommandOccurrence` values. It drops the parser syntax root after it flattens child commands.

The current path projection puts redirect facts on a command candidate. A compound redirect has no `CommandOccurrence`. A compound with no body command also has no command candidate. Netclaw must retain that effect through the call-level screens.

The ShellSyntaxTree proposal adds an opt-in Bash syntax wrapper. The wrapper owns the compound node, ordered redirects, source-time working directory, and redirect source spans. It does not grant path authority.

## Goals / Non-Goals

**Goals:**

- Keep one call-local source tree for each parser invocation, including decoded shell child sources.
- Keep each parser-owned compound wrapper and redirect fact intact.
- Project each outer redirect once into Netclaw's existing path and file-tool policy.
- Keep command candidates separate from file-operation consent.
- Recheck compound redirect paths before process start.

**Non-Goals:**

- Do not create a `CommandOccurrence` for an outer redirect.
- Do not copy outer redirects onto child occurrences.
- Do not add a grant scope, persistent identity, actor message, or configuration field.
- Do not parse an executable's private grammar.
- Do not accept brace groups, PowerShell compounds, unsupported redirects, or targets with command substitutions.

## Decisions

### Retain the parser-owned syntax roots

`ShellCommandAnalysis` will retain a call-local ordered list of `(Source, Syntax)` pairs. `ShellCommandAnalyzer` will add each parsed source string and its existing `ParsedCommand.Syntax` root. The analyzer already parses a decoded Bash child source recursively. It will retain that child's root beside the exact child source too.

Consumers will enumerate `RedirectedCompoundSyntax` nodes from those roots. They will keep the parser nodes in call-local views and candidates. `ShellCommandAnalysis` will not keep a second owner list. A consumer will not recreate an owner from source text.

The parser owner supplies the cwd at compound entry. Netclaw will resolve relative targets against that value. An unknown owner cwd with a relative target stays unknown. Netclaw will not substitute a descendant's cwd or the call cwd.

### Keep path facts at call level

`ShellPolicyPathFacts` will project compound redirect targets with the existing `ShellPolicySourcePathFact` and `ShellPolicyResolvedPathFact` types. Each fact keeps `Origin.Redirect`, the parser's redirect mode, target domain, and completeness. Netclaw resolves a target against the owner's exact cwd.

`ShellPolicyPathFacts.CreateCompoundRedirectViews` will return one existing `ShellPolicyResolvedPathView` per owner. Each view uses the owner's entry cwd as its resolution base. Its ordered facts use `Origin.Redirect`, `RedirectMode`, completeness, and the target domain. The analysis retains the source tree and redirect spans. Netclaw will not copy spans into path facts unless an authority consumer needs them.

These views will remain separate from candidate path facts. This avoids duplicate effects on every descendant command. It also covers a compound that has no command occurrence. Existing command candidates keep their current words and directories.

The policy path will use the same projected facts at each owner boundary:

```text
parse with compound-redirect opt-in
  -> retain source roots and parser-owned redirect wrappers
  -> hard-deny every body command and preserve unparseable-source screening
  -> check protected shell text and each proved redirect path
  -> check owner cwd, path roots, links, protection, and file-tool mode
  -> query existing file-tool grants for each consented file operation
  -> keep one command candidate per body command
  -> add exact Once-only candidates for each uncovered file operation
  -> authorize with existing command grants and file grants independently
  -> parse again and capture/recheck each redirect path before launch
```

`ToolPathPolicy` and `ToolAccessPolicy` will consume the same effect projection. Input redirects use `Read` rules and `file_read` mode. Output redirects use `Write` rules and `file_write` mode. A compound redirect does not change a command candidate's directory scope. The policy checks owner effects independently, so it does not need to associate them with child commands.

### Compose consent with existing identities

`ToolAuthorizer.ShellCall` already owns the call analysis and its command approval. The authorizer will pass the call-level redirect facts through its existing preflight and file-tool grant flow.

For a known redirect whose file-tool mode is `Approval`, a stored grant for that exact file tool and path covers only that file operation. Without that grant, the existing `WithFileToolConsent` composition keeps the full-source candidate and adds an exact candidate for each uncovered file operation. Each candidate keeps `VerbTokens` null and uses internal, call-local parser references. The full-source candidate has the exact shell source in `Verb` and one real compound owner reference. It preserves source visibility when `DisplayText` uses a summary and keeps the command-length check. A separate call-local shell source key binds the exact source to `Once`. Each file-operation candidate has the exact operation text in `Verb` and references its actual owner and redirect. `WithFileToolConsent` derives operation text from the existing canonical `FileToolCall`; it does not parse display text. These references do not enter durable approval records.

`ShellPolicyProjection` validates every owner against compound nodes reachable from this call's retained syntax roots. It validates each redirect against that owner's redirect list. It validates the full-source candidate against the exact retained shell source. It validates each operation candidate against the canonical file operation derived from its referenced redirect and projected path. A candidate cannot combine these references with `SourceOccurrence`. The full-source candidate keeps the source visible when display text uses a summary and preserves the existing command-length check. A separate call-local shell source key binds the exact full source to `Once`, including after recovery. File-operation candidates bind each action. These exact candidates cannot match a stored exact grant or a generic verb grant.

For a compound with no body command, the file operation remains visible even when no command candidate exists. `FileToolGrantsAsync` and redirect screening will read call-level effects without requiring `SourceOccurrence`. If a file-tool grant does not cover an operation with mode `Approval`, the composed file candidate must remain uncovered. The `NoCommand` and automatic approval rules cannot skip that candidate.

A shell grant for a body command will not cover its redirect. A file-tool grant will not cover any body command. Mode `Deny`, an out-of-root target, a protected path, or an unsafe link denies before consent.

An unknown target, incomplete redirect, or unknown cwd for a relative target on a supported owner is denied with `shell_redirect_unproved`. No one-time answer or stored grant overrides this denial.

A command substitution in an outer target stays on the parser's unparseable-source path. Netclaw will not add text recognition to identify that form. It will keep the current unresolved-source policy and will not create reusable candidates from partial facts. Tests will record whether the existing parser facts expose nested hard-denied commands. A gap stays a reported parser limitation until a general parser fact closes it.

### Recheck paths at launch

`ShellProcessLaunch.CapturePathState` already records canonical shell paths and their resolved link targets before consent. It compares the same state after a second hard-policy parse and before process start.

The shared path-fact projection will include compound redirect targets in both captures. The launch parser will use the compound option. If a target, its link target, or directory state changes, the existing `shell_launch_paths_changed` denial stops the process.

### Keep compatibility fail closed

The ShellSyntaxTree parser option will remain false by default. Netclaw will enable it in the Bash analysis path that retains and screens the wrapper. The PowerShell path will not set the option.

The Netclaw package pin will target the reviewed public API. An unsupported form or a parser result that lacks a required owner or redirect fact will fail closed. Netclaw will not select a runtime fallback based on package capabilities.

## Risks / Trade-offs

- [Risk] A consumer may check child commands but miss an outer redirect. → Keep the call-level effect list and use it in hard-deny, path, file-tool, and launch screens.
- [Risk] A compound with no body command may take the no-command exemption. → Keep its file effects visible before automatic or no-command approval.
- [Risk] An unknown compound redirect may receive an ordinary child grant. → Deny a supported owner with an unknown or incomplete effect before grant lookup.
- [Risk] A rejected command substitution may hide a hard-denied command. → Test the existing hard-deny path and report a missing parser fact; do not add source-text parsing.
- [Risk] A child changes cwd inside the compound body. → Resolve the redirect only with the parser's owner-entry cwd.
- [Trade-off] Each redirect remains a separate file operation, even if two redirects name the same path. → Preserve source order and let existing file-operation checks decide each effect.

## Migration Plan

1. Keep the parser option off until the consumer implementation passes parser, security, actor, and launch tests.
2. Pin the separately reviewed ShellSyntaxTree API release. Do not publish or deploy it as part of this change.
3. Enable the option only in Netclaw's Bash analyzer and keep exact-consent fallbacks for unsupported input.
4. Roll back by disabling the option and restoring the previous package pin. Existing saved grants need no migration.

## Open Questions

None. The consumer will use the approved owner cwd and redirect source facts from ShellSyntaxTree.
