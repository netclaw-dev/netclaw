## Context

See `proposal.md`. The parser is lexical: `keylink` and `keys2` are plain
words, not paths. The protected-path screen in `ToolPathPolicy` runs before
grant lookup and denies a call that names a protected path. Its link resolution
(`FileSystemAuthority.IsProtected`) already resolves the final link and each
intermediate link.

## Goals / Non-Goals

**Goals:**

- Deny a plain word whose link target is protected, for every plain word.
- Keep grant coverage unchanged for any link word.

**Non-Goals:**

- Folder link rules for a path scope. They do not change.
- Command-specific knowledge.

## Decisions

- **Screen the link target, not the candidate scope.** `ToolPathPolicy` checks
  each clause element after the program word. When the element value names one
  link directly in the occurrence directory (`ShellGrantFileWords.NamesLink`),
  the screen asks whether the link path is protected for the shell operation.
  Alternative: a path scope for the link (the first version). Rejected: a folder
  grant refuses a link below its root, so a link to a file in the same folder
  lost its coverage.
- **The occurrence directory comes from the parser proof.** An unproved
  directory names no entry; such a command is exact consent only.
- **The program word does not count.** The shell finds a bare program word
  through `PATH`, not in the directory.

The screen is call-local and stateless. No actor message or persisted record
changes.

```text
schematic:
for each command, for each element after the program word:
  if element names a link L in the proved command directory
     and IsProtected(L, Shell)          // resolves the link target
    deny shell_references_protected_path
```

## Risks / Trade-offs

- [A link in an unproved directory is not screened] → The command is exact
  consent only, so no grant covers it.
- [Each plain word costs one file-attribute read] → Only words that are one
  plain name are read, once per word.
