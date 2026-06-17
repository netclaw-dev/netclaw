## Context

The deep review (`docs/reviews/2026-06-config-tui-deep-review.md`) shows the high-severity
TUI bugs are not independent: they fall out of three missing conventions. This design
fixes the conventions once and routes the individual findings through them, rather than
patching ~25 call sites in isolation. Constraint: the Termina event loop is single-threaded;
config viewmodels are `IDisposable`; the repo is default-deny and forbids silent fallbacks.

## Goals / Non-Goals

**Goals**
- One atomic, serialized write path for all config/secrets/device-registry persistence.
- A uniform background-task lifecycle for config viewmodels (track → cancel → await).
- A uniform fail-loud convention for config parse/read on render & autosave paths.
- Deny-by-default for unparseable/unknown security-relevant values.
- Each fix backed by a test that fails before the fix (race, fake-failure, or round-trip).

**Non-Goals**
- Decomposing the two god-object viewmodels (separate follow-on change).
- The 53 low-severity findings (opportunistic later sweep).
- Any happy-path behavior change visible to the operator.

## Decisions

- **Single atomic write seam.** Replace `File.WriteAllText` in the config/secrets/device
  writers with a shared atomic write (write to a sibling temp file, flush, then
  `File.Move(temp, dest, overwrite: true)`). Centralize in `ConfigFileHelper` so
  `ConfigEditorSession`, `WizardConfigBuilder`, and the `devices.json` writer all reuse it.
  Rejected: per-writer ad-hoc temp files (duplicates the logic; drift risk — the exact
  defect class this whole change exists to remove).

- **Serialize writes + background-task lifecycle.** A config viewmodel that spawns a
  background probe/label task stores the `Task` handle and its `CancellationTokenSource`,
  exposes a `CancelAndAwaitBackgroundAsync()`, and calls it at the start of `Save` and in
  `Dispose`. The save path and the background path therefore never write concurrently, and
  a stale post-probe continuation can no longer mutate a reset viewmodel. Rejected: a
  global write lock only (doesn't stop the stale-state clobber — the data race on the
  shared viewmodel object is separate from the file race).

- **Fail-loud convention.** Config parse/read invoked from a render or autosave path is
  wrapped to convert a parse/IO exception into a surfaced status message (and a safe,
  read-only fallback for rendering) instead of throwing into the loop. Distinct from
  **deny-by-default**: when the value is *security-relevant* and unparseable/unknown, the
  fallback is the most-restrictive interpretation (disabled / no-grant) plus a warning —
  never a permissive assumption. Both are explicit and visible; neither is a silent
  degrade (which the constitution forbids).

- **Persist-after-validate for secrets.** Credentials are written to disk only after the
  validating probe succeeds; a failed probe leaves the prior secret untouched.

## Risks / Trade-offs

- **Making probes truly async changes interface shapes** (`ISkillFeedReachabilityProbe`,
  etc.) → mitigation: change the interface + all impls/fakes together; cover with a
  responsiveness/cancellation test.
- **Cancel-and-await before save adds latency** when a probe is mid-flight → acceptable
  (bounded by the probe's own timeout; correctness over a few ms) and only on the rare
  concurrent-save path.
- **Fail-loud fallbacks could mask a real config problem** → mitigation: the fallback is
  always accompanied by a visible status/warning, never silent; security-relevant cases
  deny rather than permit, so the safe direction is preserved.

## Migration Plan

Incremental and behavior-preserving on the happy path: land the atomic-write seam first
(everything else depends on it), then the per-viewmodel lifecycle + fail-loud guards, then
the targeted correctness/secret fixes — each its own commit with its test. No data
migration; existing config files are read unchanged and rewritten atomically.
