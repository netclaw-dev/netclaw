## Context

See `proposal.md` and [the engineering glossary](../../../docs/spec/GLOSSARY.md).
The observed session declared its project root. The later shell calls still used inline `cd` and a pipeline.
ShellSyntaxTree marked the submitted syntax complete, but a later directory join lost its exact path.
Netclaw then marked the call complex and omitted reusable candidates.
Some other observed prompts had no grant for `rm`, `python3`, or `dotnet new install`.

## Goals / Non-Goals

**Goals:** Reduce prompts when all executable occurrences and all reachable path scopes have proved coverage.
Keep a command that lacks any required grant subject to the existing approval gate.

**Non-Goals:** Guess runtime command output, expand a glob, or infer an executable's private grammar.
Do not change the meaning of a parent folder grant or enlarge session authority.

## Decisions

### Keep syntax and authority in their current owners

ShellSyntaxTree owns executable occurrences, control flow, path shapes, and value domains.
Netclaw owns grants, reviewed-safe policy, protected paths, audience checks, and symlink checks.
These facts are call-local. The approval actor owns durable and session grant evidence.

Schematic flow:

```text
parse source -> enumerate reachable occurrence scopes -> validate each path
             -> match each scoped candidate -> apply deny and reviewed-safe rules
             -> permit only if every candidate has coverage
```

For `cd /work/sub && cat result.txt | sed -n '1p'; ls .`, `ls` can run in `/work` or `/work/sub`.
The policy must check both paths before it can reuse a folder grant.
An unknown directory or an unproved descendant glob leaves the call exact.

### Treat directory advice as a correction

The shell call can suggest a one-call `WorkingDirectory` when the leading directory target is exact and allowed.
The correction does not execute the original call. The agent must submit a replacement call.
The replacement uses the same policy path as any other shell call.
The smaller alternative is guidance alone. The observed agent already received that guidance and still used inline `cd`.

### Bound the parser addition

ShellSyntaxTree will add a finite, conservative directory domain only where its abstract flow proves every reachable directory.
It will keep an unknown value after an unbounded transition, a limit overflow, or an unresolved target.
Netclaw will consume only exact or finite path sets with a small fixed bound.
The smaller alternative is a Netclaw parser for `cd` and list operators. That would duplicate shell grammar across the authority boundary.

## Risks / Trade-offs

- A missed failure path could reuse a folder grant outside its root. Tests must force the `cd` failure branch.
- A symbolic link could leave a lexical parent root. Netclaw must check each concrete path at the authorization and launch boundaries.
- A finite set can grow during loops. ShellSyntaxTree must use a strict bound and return unknown on overflow.
- A parser fact may expose a complete command without enough path facts. Netclaw must retain exact approval in that case.

## Migration Plan

Netclaw will first ship the correction and corpus without a grant change.
ShellSyntaxTree will ship the parser facts as `0.4.0-beta.3` after its Linux and Windows gates pass.
Netclaw will then pin the public beta and enable candidate coverage for proved finite scopes.
Each release can roll back to its prior package or binary. Old grants retain their folder scope after schema migration.

## Delivery

1. Netclaw PR: Add a typed one-call directory correction and sampled corpus rows. Prove that the original call starts no process.
2. Netclaw PR: Add bounded candidate coverage for currently complete static compounds. Keep unresolved scopes exact.
3. ShellSyntaxTree PR: Publish finite directory and path facts with parser, corpus, API, and adversarial tests. Merge, tag, and publish `0.4.0-beta.3`.
4. Netclaw PR: Pin the public beta and consume its proved facts. Run the full security and integration gates.

Each later Netclaw branch started from the preceding implementation branch.
Merge the PRs in order and preserve that ancestry.
The ShellSyntaxTree release must exist on NuGet before the fourth PR can pass public restore.
The maintainer authorized automatic merge of these PRs. CI and the independent security review still gate each merge.

### Explicit repository scope

The operator chose a new repository grant type. Existing folder grants keep their path meaning.
Netclaw will bind a repository grant to a canonical Git common directory and its registered worktree roots.
This scope supports an ordinary `.git` directory and its registered linked worktrees.
A main checkout with `--separate-git-dir` remains outside this scope.
The policy will confirm both facts for the current directory before it uses that grant.
It will reject an unregistered `.git` pointer and a moved or removed worktree.
It will apply ordinary verb, path, audience, and hard-deny checks after the repository match.

The approval record must use a new tagged scope shape. It must not infer this scope from an old directory field.
The operator will see the repository scope as a separate approval choice and a distinct list label.
The repository identity is durable grant data; each worktree check is call-local.
The approval actor owns the stored record. The shell coordinator owns the current worktree proof.

The fifth PR will add this scope after the compound-command work.
It will test a registered sibling, an unrelated repository, a forged `.git` pointer, a moved worktree, a symlink escape, and an ungranted second verb.
