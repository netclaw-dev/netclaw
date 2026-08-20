## Context

Netclaw uses ShellSyntaxTree facts before it reuses a shell approval. The first
v0.3 alpha lacks the later Bash redirect and command-resolution behavior.

The current consumer marks all heredocs and here strings unresolved. This rule
is safe, but it causes prompts for complete data sent to a non-interpreter.

The shell gate runs before actor dispatch. This change does not alter actor
messages, actor ownership, persisted grants, or recovery behavior.

## Goals / Non-Goals

**Goals:**

- Adopt ShellSyntaxTree `0.3.0-alpha.1`.
- Allow complete bounded stdin data for one constrained receiver grammar.
- Keep unknown and unsupported shell forms strict.
- Preserve complete command text in approval displays.
- Pin each decision in the review matrix.

**Non-Goals:**

- Add a general stdin receiver catalog.
- Change grant storage or approval button behavior.
- Migrate PowerShell shell analysis.
- Add a safe `sed` grammar.

## Decisions

### Use the typed redirect operation

The consumer will use `RedirectOperation.HereDocument` and `HereString`. It will
not infer these forms from a compatibility redirect target.

The display formatter will encode raw line breaks with a visible `⏎` marker
for both operations. This choice preserves `<<` or `<<<`, the data, and each
authored boundary in a single-line prompt.

Alternative: reconstruct these redirects from `Clause.Redirects`. That model
cannot preserve the v0.3 redirect operation and can misstate `<<<` as `<`.

### Start with one data-only receiver grammar

The safe grammar will require all these facts:

- The occurrence is complete.
- The verb chain contains only `cat`.
- The command has no authored arguments.
- The redirect uses the default stdin source or explicit descriptor zero.
- The redirect is complete and is not path-relevant.
- A heredoc is complete, literal, and has complete body provenance.
- A here-string target domain is `Exact` or `FiniteSet`.

A heredoc uses its expansion mode and authored body facts. A here string uses
its target domain. Every expanding heredoc, other receiver, or unproved value
stays unresolved and prompts.

Netclaw will keep its established transparent analysis for a direct shell
dispatch such as `bash -c`. A receiver wrapper such as `command cat` does not
match the one-token receiver grammar and stays unresolved. The parser clears
outer source spans after a safe command-string decode. Netclaw accepts paired
unavailable spans only on those decoded occurrences and still requires the raw
delimiter, raw body, literal mode, and complete facts.

Alternative: allow all safe verbs. That choice is unsafe because some programs
interpret stdin as code, options, or a policy-sensitive language.

### Keep every other redirect decision unchanged

The consumer will still evaluate file redirects, descriptor redirects, hard
deny rules, protected paths, and every command occurrence. An output redirect
on the same `cat` command still receives its independent path decision.

### Use the review matrix as the downstream contract

The matrix will cover exact data, unknown data, interpreter receivers, stored
grants, command-resolution mutation, and reserved execution syntax. Focused
tests will pin display text and parser integration.

## Risks / Trade-offs

- [Risk] The `cat` grammar excludes useful receivers. -> The narrow boundary
  prevents an unsafe receiver classification. Later slices can add proved
  receivers.
- [Risk] A future package changes an enum or value-domain shape. -> Unknown
  enum values and incomplete facts stay strict.
- [Risk] A parser failure removes approval candidates. -> Netclaw offers only
  one-shot approval and deny. It does not persist a grant.
- [Risk] A data-only stdin redirect also has a file output. -> Netclaw evaluates
  the file redirect separately.

## Migration Plan

1. Update the central package version.
2. Add focused analysis and display tests.
3. Add the review matrix cases.
4. Run the security and actor test suites.

Rollback restores the previous package and consumer rule. No persisted state
or configuration needs conversion.

## Open Questions

None.
