## Context

Before this change, shell analysis flattened child commands and dropped parser roots. An outer redirect belonged to its compound owner. A compound with no body command had no command occurrence. Netclaw had to check that effect separately.

ShellSyntaxTree supplies an opt-in wrapper with the compound node, ordered redirects, source spans, and entry working directory. The parser reports syntax facts. Netclaw owns path and grant authority.

## Decisions

### Retain parser-owned roots

Retain each `(Source, Syntax)` root for the call. Retain roots for decoded Bash child sources too. Walk these roots to find compound owners. Keep the parser nodes in call-local views. Do not rebuild owners from source text or keep a second owner list.

The owner supplies the working directory at compound entry. Resolve relative targets against that value. Keep a relative target unknown when the owner directory is unknown. Do not use a descendant or call-level directory as a fallback.

### Project each redirect as a call-level file effect

Use the current `RedirectAnalysis`, `ShellPolicySourcePathFact`, and `ShellPolicyResolvedPathFact` types. Keep the target domain, mode, completeness, and source order. Keep source spans with the retained parser tree. Do not copy an outer redirect onto child command occurrences.

The path fact uses `Origin.Redirect`.

This separate projection covers compounds without body commands. It also avoids duplicate effects on each child. Input redirects use read rules and `file_read` mode. Output redirects use write rules and `file_write` mode. A redirect does not change a child command's directory scope.

Descriptor duplicate, move, and close redirects do not name file operations. Keep their parser facts. Do not resolve them as file paths.

For nested compounds, check each redirect once against its own owner's entry directory. Keep inner redirects separate from outer redirects.

### Keep the authority sequence ordered

Schematic flow; normal admission and persistence steps are omitted:

```text
parse with compound support and retain owners
  -> screen commands and redirects for hard deny, paths, links, roots, and file mode
  -> check command grants and file-operation grants separately
  -> request exact consent for uncovered effects
  -> reparse and compare redirect paths before launch
```

The authorizer keeps the full source visible in the candidate's `Verb`. It adds an exact candidate for each uncovered file operation.
The source candidate keeps the 900-character check. A call-local source key binds the full shell source to `Once`.

Each operation candidate has null `VerbTokens`. It keeps call-local references to its owner and redirect. These references do not enter durable records. The projection checks each reference against this call's roots and the canonical file operation.

Use stored file-tool grants only when that tool has mode `Approval`. An invalid owner or redirect reference fails closed.

A shell grant for a body command does not grant a redirect. A legacy exact shell grant for the full source does not grant a file operation.
A file-tool grant does not grant a body command. A stored file-tool grant covers only its exact tool and path.
Deny mode, protected paths, unsafe links, and out-of-root targets stop before consent.
Auto mode and the no-command rule cannot skip a file operation that needs approval.

Positive case: an exact input target uses its owner's entry directory and the read policy. Negative case: an unknown target on a supported owner is denied with `shell_redirect_unproved`. A one-time answer or stored grant does not override that denial.

An outer target with command substitution stays on the parser's unresolved-source path. Do not add text parsing or reusable candidates from partial facts. The paired authorization probe showed that this unsupported form can hide a nested hard-denied command. The probe checked authorization only. It ran no command. Keep this gap explicit until a general parser fact closes it.

### Recheck paths at launch

Use the path-state capture before consent and after the second policy parse. A changed redirect path, link target, or directory state denies launch with `shell_launch_paths_changed`.

### Preserve compatibility

Keep the parser option off by default. Enable it only in the Bash path that retains and screens owners. Do not enable it for PowerShell. Pin the reviewed public API. Missing required facts fail closed. Do not select a runtime fallback by package capability.

Outer heredocs, here strings, brace groups, and decoded child wrappers stay unsupported.

Separate redirects remain separate file operations, even when they name the same path. Existing file-operation policy decides each effect.
