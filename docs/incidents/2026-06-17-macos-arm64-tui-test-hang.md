# Incident: `Test-macos-26` hangs in `Netclaw.Cli.Tests` (macOS / ARM64 only)

- **Status:** ROOT CAUSE FOUND for CI hang; broader off-loop R3/Termina publication audit remains open.
- **Date opened:** 2026-06-17
- **Affected:** PR #1368 (`docs/netclaw-validated-ui-components`), CI job `Test-macos-26` (`macos-26`, Apple Silicon / ARM64).
- **Not affected:** `Test-ubuntu-latest`, `Test-windows-latest` (both x64), and local Linux x64 runs.

## Summary

After the config-TUI async rewrite + hardening landed on PR #1368, the `Test-macos-26` CI job
**hangs**: `dotnet test` reaches the `Netclaw.Cli.Tests` assembly, prints
`A total of 1 test files matched the specified pattern.`, and then produces **no further output**
until the 30-minute job timeout kills it (`##[error]The operation was canceled.`). Every other test
assembly in the same run completes normally (e.g. `Netclaw.Daemon.Tests`: 738 passed in 46s). The
ubuntu and windows test jobs pass in ~11–14 min. So a test in `Netclaw.Cli.Tests` hangs **only on
macOS**.

Because xunit waits for *all* tests in an assembly before reporting results, a **single** test that
never completes stalls the whole assembly — this looks like one hung test, not broad slowness.

## 2026-06-17 investigation result

The instrumented PR run `27711731116` uploaded `test-hang-dump-macos-26` and a blame-hang sequence
XML. Linux `dotnet-dump` cannot analyze the macOS ARM64 Mach-O dump, but the sequence XML names the
active unfinished test directly:

```xml
<Test Name="Netclaw.Cli.Tests.Tui.Wizard.HealthCheckStepViewModelTests.ResultsSnapshot_is_safe_to_read_while_results_are_mutated_concurrently" Completed="False" />
```

That test is a new PR-side regression test for the `HealthCheckStepViewModel.ResultsSnapshot()` lock
discipline. The production `Results` path uses one monitor consistently (`HealthCheckRunner.Add`,
`UpdateLast`, `AllPassed`, and `HealthCheckStepViewModel.ResultsSnapshot()`), so the sequence does not
prove a production Termina render-loop deadlock.

The test itself was pathological on macOS ARM64 CI: it started an unbounded writer that appended to
`Results` until cancellation, then performed 50,000 snapshots before canceling the writer. Since each
snapshot copies the full list under the same lock, the work grows with every writer append and can turn
into a CPU/memory/monitor-contention stress test. The uploaded artifact was ~1.8GB, consistent with a
run that ballooned before blame-hang killed it.

Fix: bound the writer-side list growth, run the writer as a dedicated long-running task, and await it
with `WaitAsync` after cancellation. The test still races concurrent `HealthCheckRunner.Add()` calls
against `ResultsSnapshot()`, but no longer creates an unbounded list-copy workload.

Separate finding: the original weak-memory-ordering concern is still valid as a **guidance and audit**
issue. `.claude/skills/termina-tui-patterns.md` incorrectly blessed background continuations mutating
plain fields / `ReactiveProperty` values and then calling `RequestRedraw()`. R3 property setters fan out
synchronously on the publishing thread, and several page subscriptions invalidate `DynamicLayoutNode`s
inline, so `RequestRedraw()` is not a general marshal to the Termina loop. The skill has been rewritten
to require locked snapshots, immutable/atomic publication, or genuine loop-owned mutation for any state
read by render/input.

## Remaining hypothesis: macOS/ARM64 weak memory ordering (vs x64 TSO)

x86/x64 implements a strong memory model (Total Store Order): a write by one thread becomes visible
to other threads in program order without explicit barriers. **ARM64 has a weak/relaxed memory
model** — a plain field write on thread A is **not guaranteed visible** to thread B without an
explicit barrier (`Volatile.Read/Write`, `Interlocked`, `lock`, or a `MemoryBarrier`).

The Termina TUI view-models lean on a pattern that is *safe on x64 by accident of TSO* but may be
**unsound on ARM64**:

- The render loop runs on a thread-pool thread with **no `SynchronizationContext`**.
- Async probe/label-refresh/save continuations therefore resume on **arbitrary thread-pool threads**,
  not the loop thread.
- Those continuations **mutate plain view-model fields** (not just `Task`s) and then call
  `RequestRedraw()`; the loop thread later **reads those same fields** for rendering and input
  handling — **with no lock or barrier between the cross-thread write and the read** (other than
  whatever `RequestRedraw` → `Channel.Writer.TryWrite` happens to provide).

On x64 the stale-read window doesn't exist (TSO). On ARM64 the loop can read a **stale** field value
— e.g. a "work done / task cleared / state ready" flag that was set by a pool-thread continuation but
not yet visible — and **wait forever** for a condition that has, in fact, already occurred. That remains
a plausible mechanism for future ARM64-only TUI bugs, but it is not the proven root cause of the
`27711731116` CI hang named above.

> NOTE: `Task` completion/continuation handoff *is* memory-safe (the TPL inserts barriers), so
> awaiting a `Task` across threads is fine. The risk is **plain non-`Task` fields / collections**
> read on one thread and written on another without synchronization.

## Specific directive — AUDIT THE TUI SKILLS I WROTE

A dedicated agent should audit the agent-facing skill **`.claude/skills/termina-tui-patterns.md`**
(authored during this work) against the ARM64 memory model. It currently asserts, as blessed
guidance:

1. *"'No SyncContext' does not mean 'no async' — it means async continuations resume on the thread
   pool, which is fine, because Termina's marshaling primitive (`RequestRedraw`) is thread-safe."*
2. *"You do not marshal continuations back to the loop. You mutate `ReactiveProperty`/field state from
   the thread-pool continuation, then `RequestRedraw()`."*
3. *"Cross-write races are handled by **cancel-and-await of the background task, not by locks or
   marshaling**."*
4. The "save-vs-background-write discipline" (cancel-and-await before a save).

**Audit questions to answer:**

- Does `RequestRedraw()` (i.e. `_eventChannel.Writer.TryWrite(...)`) establish a **release** barrier,
  and does the loop's dequeue (`await foreach` over the `Channel`) establish a matching **acquire**
  barrier, such that a field written *before* `RequestRedraw` is guaranteed visible to the loop
  *after* it dequeues that redraw event? If yes, the redraw path may be safe **for fields read only
  during a redraw**. If the loop reads those fields on **other** paths (an independently-delivered
  keypress, a timer tick, a different event) there is no such ordering — flag every such read.
- Is guidance #2/#3 sound on ARM64 at all, or does it need to be rewritten to require
  `Volatile.Read/Write` / `Interlocked` / `lock` on any field shared between a pool-thread
  continuation and the loop thread?
- Enumerate the concrete cross-thread fields and decide each. Known candidates in
  `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs`: `_labelRefreshTask`, `_channelAudiences`
  (`Dictionary`, mutated by `ReconcileResolvedChannels` off-loop and read/written by on-loop
  handlers), the `Step` channel state (`SetChannelIds` / `RemapChannelAudiences` off-loop),
  `_channelRowIndex`, `IsSaved`/`Status`/`Screen` (`ReactiveProperty` — check R3's own thread-safety),
  and `_pendingConfigWrite` (believed loop-only, but `Dispose` reads it — confirm Dispose runs on the
  loop thread). Other VMs: `SkillSourcesConfigViewModel` (`_probeTask`, status), `ProviderManager`,
  the wizard `HealthCheckStepViewModel` (its `Results` list **is** lock-synchronized — a good model to
  generalize from), `ExposureModeStepViewModel`/device-pairing.
- Also audit any *other* skill authored in this work for the same x64-only assumption.

**Deliverable of the audit:** a corrected `termina-tui-patterns.md` that is ARM64-correct (require
explicit synchronization on cross-thread shared state, or genuinely marshal mutations onto the loop),
plus a list of the specific VM fields that need `Volatile`/`Interlocked`/`lock` or on-loop marshaling.

## What has been ruled out

- **Sync-over-async deadlock (the original theory):** fixed and proven. The four
  `.GetAwaiter().GetResult()` bridges in `ChannelsConfigViewModel` are gone; a deterministic
  single-worker-`SynchronizationContext` regression test deadlocks the old code and passes the new.
  `grep` confirms zero unbounded sync-over-async in production TUI. **Yet macOS still hangs** — so
  this was at most one cause, or never the CI culprit.
- **Real-network probe without a timeout:** the only real-`HttpClient` fallback is
  `SearchConfigEditorViewModel.CreateHttpClient()` (`?? new HttpClient()`), but every probe-triggering
  Search test injects a stub/gated factory — none hit real network.
- **HealthCheck/daemon polls:** bounded (90s reload, 5-min overall) and use stub HTTP handlers.
- **Local reproduction (x64):** the full `Netclaw.Cli.Tests` suite passes in 5–14s under a forced
  single-worker and 2-worker `MaxConcurrencySyncContext` on Linux x64. Consistent with the hang being
  ARM64-architecture-specific rather than a generic SC-saturation deadlock.

## How to get the hang-dump evidence

CI was instrumented in this PR (`.github/workflows/pr_validation.yml`, the `dotnet test` step):
`--blame-hang-timeout 300s --blame-hang-dump-type full --results-directory ./TestResults`, plus a
"Show hang sequence" step and an **"Upload hang dump"** artifact step. On a hang the macOS job now:
(a) aborts after 300s of no test activity, (b) writes a **full process dump** + a **`*.Sequence.xml`**
(names the in-flight test) into `./TestResults`, (c) **fails fast** (~minutes, not 30), and
(d) uploads the artifact `test-hang-dump-macos-26`.

To collect and analyze:

1. **Find the run + read the test name from the log (no download needed):**
   ```bash
   gh run list -R netclaw-dev/netclaw --branch docs/netclaw-validated-ui-components --workflow pr_validation -L 5
   # open the failed Test-macos-26 job; the "Show hang sequence" step prints the stuck test name
   gh run view -R netclaw-dev/netclaw --job <macos-test-job-id> --log | grep -A20 "HANG SEQUENCE"
   ```
2. **Download the dump artifact:**
   ```bash
   gh run download <run-id> -R netclaw-dev/netclaw -n test-hang-dump-macos-26 -D ./hangdump
   ls ./hangdump   # *.Sequence.xml (names the test) + *.dmp (full process dump)
   ```
3. **Analyze the dump** (managed thread stacks — find the thread blocked in a TUI view-model):
   ```bash
   dotnet tool install -g dotnet-dump   # if needed
   dotnet-dump analyze ./hangdump/*.dmp
   # at the prompt:
   #   clrthreads            # list managed threads
   #   parallelstacks        # grouped stacks — look for the stalled one
   #   clrstack -all         # or: setthread <n>; clrstack   on the suspect thread
   # Look for a thread parked in ChannelsConfigViewModel / a Termina render loop / a spin or wait on a
   # plain field, and for a continuation thread whose field write should have unblocked it.
   ```
   > A full dump from an Apple Silicon runner is an arm64 Mach-O core. If `dotnet-dump` struggles,
   > `lldb` with the SOS plugin on an arm64 host works too.
4. **Reproduce on hardware:** run the named test (or the whole assembly) on an Apple Silicon Mac /
   arm64 runner: `dotnet test src/Netclaw.Cli.Tests --blame-hang-timeout 120s`. If it reproduces,
   bisect the specific field/await; consider building a stress harness that hammers the suspect
   continuation↔loop field handoff under `DOTNET_TieredCompilation=0` and on arm64 to surface the
   ordering bug deterministically.

## Relevant artifacts

- PR: #1368. Commits: `67142a61` (async migration), `8621887b` (lifecycle hardening),
  `43c0e3b9` (slopwatch SW003), + the CI blame-hang/artifact instrumentation commit.
- Follow-up issue for the (separate) off-loop-mutation-race deferral: netclaw-dev/netclaw#1426.
- Skill to fix: `.claude/skills/termina-tui-patterns.md`.
- First observed hang run (pre-fix): actions run `27668131402`. Post-rebase hang: run `27705574077`,
  job `81953063966`.
