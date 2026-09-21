## Context

See `proposal.md` for the motivation.
The current repository check starts from the request working directory.
The shell analyzer already supplies one structured directory for each grant-bearing candidate.
Repository grants already store a canonical Git common directory.

## Goals / Non-Goals

**Goals:**

- Use each grant-bearing candidate's effective directory as the repository fact source.
- Require one canonical repository across all grant-bearing candidates.
- Recheck reciprocal Git registration at each authority boundary.
- Keep all non-repository policy gates unchanged.

**Non-Goals:**

- Parse command text or executable options.
- Change the meaning of folder or global grants.
- Support unregistered worktrees or separate Git directories.
- Infer repository identity from a model declaration.

## Decisions

### Reuse the existing Git scope type

`GitRepositoryApprovalScope` will resolve each effective grant-bearing candidate directory.
A null candidate directory will use the request working directory.
The resolver will return one existing scope value for each grant-bearing candidate.
The existing pure side-effect rule will exclude candidates that never create grants.

This choice avoids a parallel repository identity model.
The raw command alternative would cross the shell approval abstraction boundary.

### Require a conjunctive repository proof

The resolver will require these facts in order:

```text
structured grant-bearing candidates
  -> resolve each effective directory
  -> verify reciprocal Git worktree metadata
  -> reject every link or parent traversal
  -> compare every canonical common directory
  -> return one scope per grant-bearing candidate
```

This flow is schematic.
Ordinary hard-deny, audience, protected-path, and launch checks still apply.

The shell coordinator owns the call-local prompt decision.
The approval response uses the same call-local candidates and working directory.
The approval actor owns the durable repository grant.

### Recompute instead of trusting prompt data

The prompt stores only the offered canonical common directory.
Grant creation will recompute every grant-bearing candidate scope and compare the result with that identity.
Each grant will carry its candidate-derived worktree root to the approval actor.
Its candidate will carry the exact resolved directory for revalidation.
The actor will recheck reciprocal metadata before it writes the durable entry.
The durable repository entry will retain no folder scope.

Grant reuse will resolve the current candidate effective directory.
It will compare the current common directory with the stored repository identity.
The launch boundary will retain its existing path and link snapshot checks.

### Preserve candidate order

The repository resolver will return scopes in candidate order.
Grant creation will pair each grant-bearing candidate with its scope by index.
An empty set or a count mismatch will fail closed.

## Risks / Trade-offs

- **A path changes after a prompt.** Recompute candidate scopes before grant creation and retain launch checks.
- **Git metadata changes after approval.** Recheck reciprocal metadata before persistence and each reuse.
- **Grant-bearing candidates span repositories.** Require equal canonical common directories for the full grant-bearing candidate set.
- **A candidate crosses a link.** Reject every symbolic-link or reparse segment during scope resolution.
- **A null directory hides missing facts.** Use only the request working directory as its explicit fallback.

## Migration Plan

No approval record migration is required.
Existing repository grants retain their stored canonical identity and phrase.
The new resolver changes only prompt eligibility, grant validation, and reuse checks.
A rollback restores the earlier working-directory rule without changing stored data.
