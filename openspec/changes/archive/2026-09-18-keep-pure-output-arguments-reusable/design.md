## Context

See [the proposal](proposal.md) and the [engineering glossary](../../../docs/spec/GLOSSARY.md).

ShellSyntaxTree reports bare `$?` as unknown data because Bash can split an unquoted value. Netclaw then marks a complete static list complex before it extracts any candidates.

The existing policy exempts a small set of output commands when they have no path or redirect effect. The parser still reports nested executable occurrences.

## Goals / Non-Goals

**Goals:**

- Preserve normal grants for other static commands in the list.
- Keep all parser and path failures strict.

**Non-Goals:**

- Prove Bash initial variable state or change the process environment.
- Infer an executable's private argument grammar.
- Approve an unapproved verb or a redirect target.

## Decisions

Netclaw shell analysis owns the call-local exception. It checks parser facts before the coordinator creates approval candidates.

The exception applies only to a bare `$?` argument that the parser classifies as an unknown `EnvVar` on a static output verb. The command must have no redirect.

The exception does not change the argument's value. It only stops that value from invalidating the complete command list.

The coordinator still applies hard denial, protected paths, path policy, grants, and reviewed-safe coverage in their current order. The approval actor keeps durable grants.

Schematic flow:

```text
parse complete source
  -> reject unresolved syntax or unknown executable identity
  -> classify each argument and redirect
  -> ignore only eligible unknown output data for complexity
  -> build every other command candidate
  -> run the existing authorization gates
```

The alternative was to treat every unknown data value as safe. That would hide dynamic path and executable effects. Another alternative was to quote `$?` in agent guidance. That would leave the policy defect in place.

## Risks / Trade-offs

- A parser regression could hide an executable substitution. Tests must keep the child occurrence visible and require its authority.
- An output redirect could write a file. Tests must keep its path and approval checks active.
- A future output verb could have private side effects. The existing exemption set remains the only source for eligible verbs. The bare exit status cannot add an option.

## Migration Plan

This change adds no stored state or configuration. A rollback restores the prior one-time prompt behavior.
