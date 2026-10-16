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
- `HeadlessFailure` owns the mapping. The switch names every current `ErrorCategory` member. A value with no name maps to `infra` with kind `Unknown`. The compiler cannot require an arm for a future member: without a discard arm it raises CS8524 for unnamed values, and the build treats warnings as errors. A member added later falls to `infra`. It needs its own arm to blame the model.
- The wire parser accepts a defined member name only. `Enum.TryParse` also takes a number or a comma list, and a comma list can parse to `ModelBehavior`. Any other string maps to `Unknown`.
- The session actor reads the finish reason of the last empty reply. It reports `ModelBehavior` only for `Stop` or `Length`. No finish reason means the stream broke (`StreamFailure`). Any other reason, such as `content_filter`, is `ProviderFailure`.
- The CLI replaces line breaks in the error text it writes to stderr. Error text is untrusted, and a forged `[turn:failed]` line must not reach a harness.
- `HeadlessFailureCategory` declares `Infra` as the zero value, so `default` never blames the model.
- The exit code stays 1 for every failure. The category is not in the exit code, because `timeout` already gives 124.
- Cancel uses the shutdown token to tell a SIGINT from a transport fault. A transport cancel is a `ChannelFailure`.
- Alternative: split `ProviderFailure` in the CLI by message text. Rejected, because text is not a contract.

## Risks / Trade-offs

- [A script read cancel as exit 0] -> Cancel now exits 1. This is a breaking change.
- [A new `ErrorCategory` member falls to `infra`] -> This is the safe side. The doc comment on `FromSession` tells the author to add an arm to blame the model.
