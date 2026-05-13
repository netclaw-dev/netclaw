## Context

`approval-policy-v2` ships the `(verb, directory)` `ApprovalEntry` model
and a five-button prompt row, but the verb extractor at
`src/Netclaw.Security/ApprovalPatternMatching.cs` collapses both halves
of the pair into a single string — `find /home/petabridge` rather than
`("find", "/home/petabridge")`. The directory half of `ApprovalEntry`
exists in the data model but the extraction path never populates it
from arguments, so it falls back to the cwd of the spawned process
(also null in most sessions because the model rarely calls
`set_working_directory` preemptively).

The downstream effect is the dogfood evidence in
`D0AC6CKBK5K/1778303523.861279`: the operator clicks "Always here" on
`find /home/petabridge`, the entry persists as `("find /home/petabridge",
null)`, and the next call (`find /home/petabridge/.netclaw -name X`)
produces a candidate `"find /home/petabridge/.netclaw -name X"` that
doesn't equal the stored verb → no match → re-prompt. Folder-scoped
trust never compounds.

This change separates the two halves at extraction time. The verb is
the command head plus subcommand chain (`find`, `git status`, `npm
install`); the directory half comes from the first path-looking
argument when present, falling back to cwd otherwise. Persistence
shape is unchanged — only the extractor and matcher logic move.

## Goals / Non-Goals

**Goals:**

- Verb extraction emits a clean command head (no path arguments).
- Path arguments declare scope implicitly — `find /repo` produces a
  candidate whose effective directory is `/repo`, not the daemon's
  cwd.
- Folder-scoped trust compounds: `(find, /home/petabridge)` covers
  `find /home/petabridge/.netclaw/...` automatically.
- Pure side-effect commands (`echo X`, `printf X`, `true`, `false`)
  authorize once but don't pollute persistence.
- Existing `ApprovalEntry` storage shape is unchanged; existing v2
  test coverage continues to pass.

**Non-Goals:**

- No new buttons, no new persistence fields, no new prompt sections.
- No migration logic for the dogfood entries — they age out as the
  operator re-approves under the new rules.
- No changes to `set_working_directory`, the safe-verb short-circuit,
  the failure-path hint, or any layer-1 deny-list behavior.
- No attempt to extract paths from flag-hidden positions like `git -C
  <path>` or `make -C <path>`. Operators with that workflow can still
  call `set_working_directory` explicitly.

## Decisions

### 1. Path classification at the tokenizer layer

`ShellTokenizer.SplitCompoundCommand` already produces verb-chain
tokens for each clause. We extend the per-clause tokenizer pass to
classify each token as either a verb-chain token or a path token. A
token is path-like when it starts with `/`, `~/`, `./`, `../`, or is
exactly `~` / `.` / `..`. Other heuristics (token contains `/`
internally; token resolves to an existing directory) are rejected as
too clever for security-relevant code — false positives would silently
expand or contract trust scope.

The verb output is the chain of non-path tokens up to the first path or
end of clause: `find`, `git status`, `npm install -g` (flags-as-verb is
preserved because flags often subset the verb's behavior; `git push`
vs `git fetch` differ semantically). The path output is the **first**
path token encountered.

**Alternative considered**: extracting all path tokens, treating the
deepest-common-ancestor as the effective directory. Rejected — DCA on
`cp /src/a /dst/b` is `/`, which is exactly the shallow path we already
guard against. First-path-wins gives the source on `cp`/`mv`, the
directory on `find`/`ls`/`grep -r`, and the file on `cat`/`less` —
parent extraction handles the file-vs-directory case (see Decision 4).

### 2. Effective directory at match time

`ApprovalPatternMatching.MatchesShellApproval` takes
`(candidateVerb, candidateDirectory, cwd, approvedEntries)`. The match
predicate becomes:

```
candidateVerb == entry.verb
  AND (entry.directory is null
       OR effectiveDirectory is under entry.directory)
  AND no symlink segment along effectiveDirectory
```

Where `effectiveDirectory = candidateDirectory ?? cwd`. Relative
extracted paths (`./build`, `../shared`) resolve against cwd at match
time. Absolute paths bypass cwd entirely.

**Alternative considered**: storing the cwd separately on the candidate
and matching independently of extracted path. Rejected — the goal is
that path arguments declare scope, so the matcher must use the path
when present. Otherwise we ship verb-only extraction without the
elegance gain.

### 3. Persistence on `approve_always`

`LlmSessionActor`'s response handler currently writes one entry per
candidate verb chain with `directory = pending.Cwd` (after the cwd
fix landed in `01a142e3`). Under this change:

- For each candidate, persist `(verb, candidateDirectory ?? cwd)`.
- If `candidateDirectory` is non-null AND fails the depth guard
  (`IsCwdTooShallow`), skip the entry — same rule that today omits the
  "Always here" button when cwd is shallow. The shallow-path skip
  emits a single warning to the resolution line so the operator knows
  why some candidates were dropped.
- For candidates whose verb is in the side-effect skip list, do not
  persist at all. They were already authorized for this call by the
  decision; the resolution line lists them as authorized-once.

`approve_everywhere` continues to write `(verb, null)` for every
candidate with no path filter — global trust is global.

### 4. File-targeting commands and parent inference

`cat ~/.profile` extracts `~/.profile` as the path. A file is not a
directory; the matcher must compare against `Path.GetDirectoryName(...)`
in that case. Two implementations:

**(a) Resolve at extract time** — call `File.Exists` / `Directory.Exists`
on the extracted path and use the parent if it's a file. Rejected:
TOCTOU across the approval prompt (file may not exist at extract time
but exists by execution time, or vice versa); also adds a syscall to
the prompt-generation hot path.

**(b) Match-time normalization** — if the extracted path is treated as
a directory but `Path.HasExtension` returns true (heuristic that file
paths usually have extensions), normalize to the parent directory for
matching purposes only. Rejected: heuristic, not portable (`.bashrc`
has an extension; many Unix executables don't).

**(c) Persist as-is, match using `Path.GetDirectoryName` of the
extracted path when persisting "Always here"**. Chosen. The persisted
directory for `cat ~/.profile` is `~/` (the parent), which gives the
operator a useful folder-scoped grant. The matcher applies the same
parent-of-extracted-path rule for candidate directories at match time.
This is a deterministic string operation, no syscalls.

### 5. Side-effect skip list

The skip list is small and explicit:

- `echo`, `printf`, `:`, `true`, `false`

Bash builtins that produce no filesystem effect when used without
redirects. We do **not** include redirect-producing variants — `echo X
> /tmp/file` has a path in the redirect target and should persist as
`(echo, /tmp/)`. Detection: a clause is "pure side effect" when its
verb head is in the skip list AND it has no path token AND no shell
redirect operator (`>`, `>>`, `|`, etc.). Otherwise it persists
normally.

**Alternative considered**: persist every verb but tag side-effect
ones as low-confidence. Rejected — adds complexity to the matcher
without operator-visible benefit. Operators don't want a future
prompt to silently auto-allow on `(echo, *)`; they want it to never
have been persisted in the first place.

### 6. Backwards compatibility with existing v2 entries

Stored `ApprovalEntry` records from the dogfood window have path-
embedded verbs (`find /home/petabridge`). Under the new extractor they
become inert — no candidate will ever produce a verb string equal to
`find /home/petabridge` because the new extractor emits `find`. Inert
entries are harmless and self-cleanup as the operator re-approves
under the new rules.

We do **not** add quarantine logic. v2 hasn't deployed beyond the
single dogfood operator; the dogfood entries were already wiped
manually. Future operators encountering pre-fix entries get the same
prompts they would have gotten anyway.

## Risks / Trade-offs

**[Risk] Path classification false positives.** A user-supplied
argument that happens to start with `/` but isn't really a path (`grep
-r '/foo' .`) would be classified as a path and could narrow scope
unexpectedly. → **Mitigation**: the symlink-segment guard already runs
on extracted paths; combined with the directory-existence check on
`set_working_directory`, the worst case is the matcher refuses a
match and falls back to a prompt. No security regression — only
slightly more prompts than necessary.

**[Risk] Multi-path commands lose the second path.** `cp /src /dst`
extracts `/src` and ignores `/dst`. If the operator wants to grant
trust on the destination tree, they have to approve `cp` again with a
different first-path. → **Mitigation**: this is the pragmatic
trade-off called out in the proposal. The first-path-wins rule covers
the common shape (find, ls, grep, cat). Operators who routinely work
in dst-first patterns can call `set_working_directory` to declare
scope explicitly.

**[Risk] File-path parent inference surprises operators.** Clicking
"Always here" on `cat ~/.bashrc` persists `(cat, ~/)` rather than
`(cat, ~/.bashrc)`. Some operators may expect file-level scoping. →
**Mitigation**: the resolution line emitted on persistence already
shows the saved scope (`Saved: cat in ~/`). Operators see what was
persisted and can revoke via `netclaw approvals revoke` if it's wider
than they wanted. Spec scenario covers this rendering explicitly.

**[Risk] Side-effect skip list drift.** Bash has more no-op builtins
than the five we list (`pwd`, `command`, `eval`, …). → **Mitigation**:
the skip list is conservative — only commands that are unambiguously
pure stdout. `pwd` we also include as truly pure. `eval` is excluded
because it executes its arguments. The list lives next to the
safe-verb list so future additions follow the same review gate.

**[Risk] Old v2 dogfood entries silently persist.** They're inert under
the new extractor, but they still appear in `netclaw approvals list`
output. → **Mitigation**: operator-side. The list output already
formats them readably (`find /home/petabridge anywhere`) and they can
be revoked with `netclaw approvals revoke`. No code in the daemon
needs to know about them.

## Migration Plan

No migration. The change is in-place:

1. Land the extractor and matcher updates.
2. Tests cover the new extractor cases plus the existing v2 cases
   (which all continue to pass because verb-only extraction is a
   strict refinement of the v2 chain extraction — non-path tokens are
   preserved).
3. Operators rebuild any project-scoped grants they relied on
   pre-fix by clicking "Always here" once per project tree under the
   new rules. The old entries become inert; deleting them is optional
   cosmetic cleanup.
4. Agent guidance (SKILL.md, AGENTS.md) updates in the same change so
   the operator-facing language matches the runtime behavior.

Rollback is trivial: revert the change. The `ApprovalEntry` storage
shape is unchanged across this change, so no data is at risk.

## Open Questions

None at design time. The first-path-wins rule, side-effect skip list,
and parent-of-file-path persistence are settled (Decisions 1, 4, 5).
If any of these surface unexpected friction during implementation,
they'll be revisited in tasks rather than re-opening the design.
