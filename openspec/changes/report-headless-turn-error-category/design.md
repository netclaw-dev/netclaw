## Context

`SessionProtocol.ErrorCategory` already crosses the wire by name. `HeadlessChannel` ignored it. See proposal.md for the reason.

## Goals / Non-Goals

**Goals:**

- Reuse `ErrorCategory`. Add one member and one CLI mapping.
- Make an unclassified failure `infra`.

**Non-Goals:**

- A new wire field or a new exit code.
- The harness verdict mapping.

## Decisions

- Add `ModelBehavior` as the last enum member. The wire carries the name, and an older reader maps an unknown name to `Unknown`.
- `HeadlessChannel` keeps the kind of the last `ErrorOutput`. It clears the kind on each `TurnCompleted`.
- `HeadlessFailure` owns the mapping. The switch names every `ErrorCategory` member. A number with no name maps to `infra`.
- The exit code stays 1 for every failure. The category is not in the exit code, because `timeout` already gives 124.
- Cancel uses the shutdown token to tell a SIGINT from a transport fault. A transport cancel is a `ChannelFailure`.
- Alternative: split `ProviderFailure` in the CLI by message text. Rejected, because text is not a contract.

## Risks / Trade-offs

- [A script read cancel as exit 0] -> Cancel now exits 1. This is a breaking change.
- [A new `ErrorCategory` member falls to `infra`] -> The switch lists every member, so a reviewer sees the choice.
