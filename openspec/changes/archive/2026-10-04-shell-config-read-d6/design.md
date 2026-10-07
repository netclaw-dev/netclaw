## Context

See proposal.md for why. Shell paths use `Write` protection, because shell
text cannot show a read from a write. The shell text screen held the whole
config directory. The rules must stay general shell facts plus policy data.

## Goals / Non-Goals

**Goals:**

- A read-only program reads one exact config file, by literal path.
- Secrets and keys stay denied in every form. Writes stay denied.

**Non-Goals:**

- No per-program option parser. No read relaxation for paths outside the
  write list.

## Decisions

- The read-only programs are policy data (`ShellVerbPolicyData.ReadOnlyOperandVerbs`).
  The reviewed-safe catalog is not used: it holds `sort -o` and `uniq in out`,
  which write files.
- `ToolAccessPolicy` gives `Read` protection only when `Write` denies a
  write-protected path, and only for a read-only occurrence. An operand must
  also hold no read-denied path (`grep -r`). A scope (working directory,
  candidate folder) is not read below.
- A read-only occurrence needs bounded argument values, no assignment prefix,
  no file-writing redirect, and no plain word that names an entry of its
  directory. Without a parser proof of the whole source, no occurrence is
  read-only.
- `ToolPathPolicy` derives guarded directories (a write-protected directory
  that holds a read-denied path). Shell text may name one exact file below it;
  the directory, a glob, or a `..` stays denied. Without a parser proof, the
  guarded directory stays shell-denied as a whole.
- Alternative rejected: keep the config directory shell-denied and tell the
  agent to use `file_read`. The owner chose shell reads.

## Risks / Trade-offs

- [A listed program gains a write option in a future release] → Policy data;
  the list holds only programs without file-writing options. Review at each
  minor release.
- [A plain word that SST does not mark as a path] → The plain-word rule keeps
  the occurrence on `Write` protection.
- [Webhook secrets and `daemon.env` become readable] → Owner decision D6.

## Migration Plan

No data migration. Rollback is a revert of the policy lists.
