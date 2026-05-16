## Context

Persistent tool approvals live in `~/.netclaw/config/tool-approvals.json`
(`version: 2`) as `ApprovalEntry` records carrying only `verb` and a nullable
`directory`. When the daemon re-prompts for an approval the operator believes
they already granted, there is nothing to inspect: no record of when a grant
was created, and no log line when a persisted grant is a near-miss at the gate.

Relevant current state:

- `ApprovalEntry` (`Netclaw.Configuration`) — `record` with `Verb` +
  `Directory`. `ToolApprovalEntryComparer` defines canonical equality
  (verb + normalized directory; case rules are platform-correct).
- `ToolApprovalStore` — JSON read/write with an mtime+size cache;
  `AddApproval` is idempotent via `ToolApprovalEntryComparer.Equals`.
  Constructed directly (`new ToolApprovalStore(path)`) at ~11 sites; not
  DI-registered.
- `ApprovalPatternMatching` (`Netclaw.Security`) — `MatchesShellApproval`
  (verb + directory-containment + symlink-segment guard) and `MatchesAny`
  (verb-only, non-shell). Returns `bool`.
- `ToolApprovalActor` — evaluates `GetUnapprovedPatterns`; has no logger.
- `tool-approvals.json` schema gate: `IsCurrentSchema` accepts **only**
  `version == 2`; anything else is quarantined to `.v1.bak`.

## Goals / Non-Goals

**Goals:**

- Record when each persisted grant was first created.
- Let an operator confirm, from logs, that a re-prompt happened despite a
  same-verb grant being present — and see why it did not match.
- Surface grant age in the `netclaw approvals` CLI and TUI.

**Non-Goals:**

- No change to matcher decisions or to the approval prompt body.
- No `tool-approvals.json` schema-version bump and no migration tooling.
- No "last used" / "use count" telemetry — only creation time.
- No timestamps on session-scoped (in-memory) approvals.

## Decisions

### 1. `createdAt` is an optional additive field; `version` stays 2

`ApprovalEntry` gains `DateTimeOffset? CreatedAt` (`[JsonPropertyName("createdAt")]`).
The on-disk schema version stays `2`.

Rationale: `IsCurrentSchema` quarantines any file whose `version != 2`.
Bumping to `3` would move every existing operator's `tool-approvals.json` to
`.v1.bak` and return an empty store — silently wiping every grant. An additive
optional field is backward compatible (an old daemon ignores the unknown
property) and forward compatible (a new daemon reads pre-feature entries as
`CreatedAt == null`). `WhenWritingNull` already in `JsonOptions` means a null
timestamp is simply omitted, exactly as `directory: null` is today.

Alternative considered: version 3 with an in-place migration on load that
stamps legacy entries. Rejected — there is no honest creation time for a
legacy entry, and a load-path rewrite of the file is a riskier operation than
leaving old entries `null`.

### 2. Stamp at write time in `ToolApprovalStore.AddApproval` via `TimeProvider`

`AddApproval` stamps `CreatedAt` when the incoming entry's `CreatedAt` is
`null`, using an injected `TimeProvider`. The store's constructor gains an
optional `TimeProvider? timeProvider = null` parameter resolving to
`TimeProvider.System`.

Rationale: `AddApproval` is the single chokepoint for both the daemon
(`ToolApprovalActor`) and the operator CLI (`netclaw approvals trust-verb`),
so stamping there guarantees identical behavior without touching either
caller. The optional parameter keeps the ~11 direct `new ToolApprovalStore(path)`
sites compiling unchanged; production paths get `TimeProvider.System`
(the CLAUDE.md-blessed production default), and tests that assert on
timestamps pass a `FakeTimeProvider`. This is a documented default, not a
silent fallback.

Idempotency interaction: when `AddApproval` finds an equivalent entry
(`ToolApprovalEntryComparer.Equals`), it already returns `false` without
appending. The existing entry — and its original `CreatedAt` — is left
untouched, so re-granting reports grant age from first grant, which is the
desired semantics.

### 3. `createdAt` excluded from equality and normalization identity

`ToolApprovalEntryComparer.Equals` continues to compare verb + directory only.
`ToolApprovalEntryComparer.Normalize` copies `CreatedAt` through unchanged.

Rationale: two grants for the same `(verb, directory)` are the same grant
regardless of when they were stamped — otherwise idempotent add breaks and the
file accumulates duplicates. The `ApprovalEntry` record's compiler-synthesized
`Equals`/`GetHashCode` will now include `CreatedAt`, but the type's existing
doc-comment already directs callers to `ToolApprovalEntryComparer` and away
from record equality, so no consumer regresses.

### 4. Near-miss diagnostics: pure explainer in `Netclaw.Security`, logged by the actor

`ApprovalPatternMatching` gains a pure function that, given an unapproved
shell candidate and the persisted entries, returns the set of same-verb
near-misses with a classified reason (directory-not-under-grant,
symlink-segment-on-path, verb-case-mismatch). `ToolApprovalActor` acquires an
`ILoggingAdapter` (`Context.GetLogger()`) and, for each pattern it reports
unapproved, logs one diagnostic line per near-miss including the grant's
`CreatedAt`.

Rationale: keeping the explainer pure and in `Netclaw.Security` makes it
unit-testable in isolation and reuses the exact path/symlink logic that
`MatchesShellApproval` uses, so the diagnostic cannot drift from the real
matcher. The actor is the only place that knows the audience/tool/session
context and already holds the persisted snapshot, so it is the natural log
site. The explainer is invoked **only** on the unapproved branch, so the hot
approved path pays nothing.

Non-shell tools (`MatchesAny`) approve on a verb match alone, so the only
possible non-shell near-miss is a verb-case mismatch; that case is folded into
the same explainer rather than given a separate path.

### 5. Display: relative text, `added —` placeholder for null

CLI `list` and TUI rows render `CreatedAt` as relative text ("added 3 days
ago") computed against the current time. A null `CreatedAt` renders the fixed
string `added —`. `list --json` emits the raw `createdAt` (ISO-8601 or
absent-as-null, consistent with how `directory: null` is already handled under
`WhenWritingNull`).

Rationale: relative text answers the operator's real question ("is this grant
stale?") at a glance. The user chose relative over absolute. A null timestamp
must render a stable, honest placeholder rather than today's date or a blank,
so legacy entries are visibly distinguishable from fresh ones.

## Risks / Trade-offs

- **Record equality now includes `CreatedAt`** → A future caller using
  `HashSet<ApprovalEntry>` / `Distinct()` would treat differently-stamped
  entries as distinct. Mitigation: the existing `ApprovalEntry` doc-comment
  already forbids relying on record equality for approval semantics; the
  store and matcher use `ToolApprovalEntryComparer` exclusively.
- **`--json` null representation** → Under `WhenWritingNull`, a null
  `createdAt` is omitted rather than emitted as literal `null`. Mitigation:
  this matches the existing treatment of `directory: null`; the spec
  scenario's "`createdAt` is `null`" is satisfied by property absence, which
  every JSON parser surfaces as null/undefined. Tests SHALL deserialize and
  assert the property, not string-match the raw JSON.
- **Clock skew / non-monotonic time** → `CreatedAt` is wall-clock and may go
  backwards across a system clock change. Mitigation: acceptable — the field
  is human-facing provenance, not an ordering key; the matcher never reads it.
- **TUI regression risk** → The approvals list is a Termina surface that
  xUnit cannot drive. Mitigation: validate with the interactive tape harness
  per the testing policy before marking done.

## Migration Plan

No migration step. Existing `version: 2` files are read as-is; their entries
deserialize with `CreatedAt == null`. The first `AddApproval` after upgrade
rewrites the file with `createdAt` on newly added entries only; pre-existing
entries keep no timestamp until they are revoked and re-granted. Rollback: an
older daemon reading a file that now contains `createdAt` ignores the unknown
property — no quarantine, no data loss.

## Open Questions

- None blocking. Relative-time granularity (e.g. "today" vs "3 hours ago" vs
  "just now") is left to implementation using the standard humanizer pattern
  already used elsewhere in the CLI, if one exists; otherwise a minimal
  buckets helper.
