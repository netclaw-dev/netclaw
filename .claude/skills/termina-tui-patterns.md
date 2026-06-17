---
name: termina-tui-patterns
description: How to do async work correctly in the Termina TUI (R3 + single-threaded render loop). Activate when editing anything under src/Netclaw.Cli/Tui/ that touches a network/disk probe, a background refresh, streaming output, spinners, or when you are tempted to write `.GetAwaiter().GetResult()` in a view-model.
---

# Termina TUI Patterns (async, R3, the render loop)

## The myth that wastes hours

> "Termina has no `SynchronizationContext`, so I can't `await` — I have to
> `.GetAwaiter().GetResult()` to stay on the loop thread."

**This is wrong, and it is the single most common mistake agents make in this
codebase.** Blocking the loop thread on a network probe freezes input *and*
rendering for the entire round-trip (the spinner stops spinning, keys queue up).
"No SyncContext" does **not** mean "no async" — it means async continuations
resume on the **thread pool**, which is fine, because Termina's marshaling
primitive (`RequestRedraw`) is thread-safe and callable from any thread.

The whole TUI already runs async the right way: `netclaw chat` streams live LLM
tokens to the screen, provider/search probes spin without blocking, and this
config editor resolves channel labels *after the page loads*. Copy those. Do not
reach for `GetResult()`.

## How Termina actually works (the mental model)

Termina (package `Termina` 0.12.1, which pulls `R3` 1.3.1) runs **one** loop:
`TerminaApplication.RunAsync` does `await foreach` over an **unbounded
`Channel<object>`**, and after every dequeued event calls `RenderCurrentPage()`.
That loop is the single-threaded *serializer* — exactly one event is processed
and one render happens at a time. It runs on a thread-pool thread with **no
installed `SynchronizationContext`** (`TerminaHostedService` launches it via
`Task.Run`).

Three consequences that define every correct pattern:

1. **`RequestRedraw()` is the only sanctioned hop onto the render loop.** It is
   literally `_eventChannel.Writer.TryWrite(RedrawRequested.Instance)` — lock-free
   and thread-safe. **Any thread may call it.** The loop later dequeues it and
   re-renders, re-reading whatever view-model state you mutated.
2. **Input handlers run synchronously on the loop thread.** Input is delivered
   inside the loop via R3 `Subject.OnNext` (a synchronous in-line fan-out, no
   scheduler). So `Input.OfType<KeyPressed>().Subscribe(HandleKeyPress)` runs on
   the loop thread — the *synchronous prefix* of your handler is on-loop.
3. **There is no R3 `FrameProvider`, no `ObserveOn`, no SyncContext.** You do not
   marshal continuations back to the loop. You mutate `ReactiveProperty`/field
   state from the thread-pool continuation, then `RequestRedraw()`. Cross-write
   races are handled by **cancel-and-await of the background task**, not by locks
   or marshaling (see the discipline below).

## The one pattern to copy (async work → UI, non-blocking)

Cleanest in-repo template: `SkillSourcesConfigViewModel.StartBackgroundProbe` /
`RunProbeAsync` (`src/Netclaw.Cli/Tui/Config/SkillSourcesConfigViewModel.cs`).
The label-refresh in `ChannelsConfigViewModel` is the same mechanics.

```csharp
private CancellationTokenSource? _probeCts;   // owned CTS
private Task? _probeTask;                      // TRACKED task (never .GetResult() it)

// Called from a synchronous (loop-thread) key/selection handler.
private void StartBackgroundProbe(/* inputs */)
{
    _probeCts?.Cancel();
    _probeCts?.Dispose();
    _probeCts = new CancellationTokenSource();

    SetStatus("Validating…", ConfigStatusTone.Neutral); // 1. sync "working" state…
    RequestRedraw();                                     //    …painted on the loop thread

    _probeTask = RunProbeAsync(_probeCts.Token);         // 2. fire-and-forget, TRACKED
}

private async Task RunProbeAsync(CancellationToken ct)
{
    Result result;
    try { result = await _probe.ProbeAsync(ct); }        // 3. await OFF-loop (thread pool)
    catch (OperationCanceledException) { return; }       //    superseded/abandoned → drop

    if (ct.IsCancellationRequested) return;              // 4. re-check before publishing
                                                         //    (a stale result must not clobber)
    Status.Value = Describe(result);                     // 5. mutate ReactiveProperty/fields only
    RequestRedraw();                                     // 6. schedule the re-read. NEVER navigate here.
}

// Tests await this instead of Task.Delay / Thread.Sleep:
internal Task? PendingProbe => _probeTask;
```

The rules baked into that shape:

- **Track the task in a field.** Fire-and-forget is fine, *untracked* is not — you
  need it to cancel-and-await before a save (below) and to expose it to tests.
- **Own a `CancellationTokenSource`;** on restart, `Cancel()`+`Dispose()` the old
  one. Re-check `ct.IsCancellationRequested` *after* the await, before you publish —
  this is what stops a superseded probe from overwriting fresh state.
- **The continuation may only mutate status/`ReactiveProperty`/VM fields and call
  `RequestRedraw()`. It must NEVER navigate** (no screen/page changes) — navigation
  off the loop thread races the renderer.
- **Expose the `Task`** (`PendingProbe`) so tests await it deterministically. No
  `Task.Delay`/`Thread.Sleep` in tests (see CLAUDE.md Testing Guidelines).

## The save-vs-background-write discipline

When a background task can **write the same state** a save reads (e.g. the label
refresh normalizes names→ids and persists), the save must cancel-and-await it
first so it can't land a stale snapshot over the fresh save:

```csharp
private async Task CancelAndAwaitLabelRefreshAsync()
{
    _labelResolutionCts?.Cancel();
    var inFlight = _labelRefreshTask;
    if (inFlight is null) return;
    await inFlight;            // the refresh swallows its own exceptions
    _labelRefreshTask = null;
}
// SaveAsync awaits this at its top, in an async method — NOT via .GetResult().
```

Keep the *consumer* async too: the save path is an `async Task`, dispatched
fire-and-forget from the handler (`_ = ViewModel.SaveFromInputAsync();`) or via
`ConfigAutosave.RunAsync`. Do **not** re-block it with `.GetAwaiter().GetResult()`.

## Streaming (the chat reference)

`netclaw chat` is the proof that async-to-front-end works. The daemon's
server-side `IAsyncEnumerable<token>` arrives over SignalR as a callback push that
is mapped onto an R3 `Subject`, and the page subscribes and appends:

- `DaemonClient.cs:78` — `_connection.On<…>("ReceiveOutput", dto => _outputSubject.OnNext(...))`
- `DaemonClient.cs:153` — `public Observable<SessionOutput> SessionOutput => _outputSubject.AsObservable();`
- `ChatPage.cs:78` — subscribe in `OnBound`; `ChatPage.cs:394-402` — append the delta to the
  `StreamingTextNode`; `ChatPage.cs:493` — `RequestRedraw()`.

Same recipe: off-loop producer → mutate node/`ReactiveProperty` → `RequestRedraw()`.

## Spinners and timers: let the node animate itself

Do **not** hand-roll a frame ticker. `SpinnerNode` (via `SpinnerViews`) owns its
own animation timer and bubbles invalidation up the layout tree; `ReactivePage`
subscribes the root node's `Invalidated` and calls `RequestRedraw()` for you. A
hand-rolled spinner tick field is the bug from #1312. For a live elapsed counter,
copy `ElapsedTimeSegment` (an `IAnimatedTextSegment` whose timer fires
`Invalidated.OnNext`). See `src/Netclaw.Cli/Tui/SpinnerViews.cs:16-24`.

## Anti-pattern: `.GetAwaiter().GetResult()` on the loop thread

This **freezes input and rendering** for the whole operation. The "it can't
deadlock because there's no SyncContext" argument is a red herring — no-deadlock
is not the same as non-blocking. Every network-bound `GetResult()` on the loop is
a bug to fix, not a pattern to copy.

Known offenders to migrate (all in `ChannelsConfigViewModel.cs`): `Save()` (`:159`),
`ApplyAddChannel()` (`:545`), the reset path (`:1327`), and `AutosaveCompletedAction`
(`:1417`). The correct shape is already next door — `SaveFromInputAsync` uses the
async `ConfigAutosave.RunAsync`. (`GetResult()` on a *fast local* op is tolerable
but still better avoided; on a *network* op it is never acceptable.)

## Checklist before you write TUI async code

- [ ] Am I about to type `.GetAwaiter().GetResult()`? Stop. Use the tracked-task pattern.
- [ ] Is the network/disk await off-loop, with only the sync "working" setup on-loop?
- [ ] Owned CTS, cancelled+disposed on restart, re-checked after the await?
- [ ] Continuation mutates `ReactiveProperty`/fields + `RequestRedraw()` only — no navigation?
- [ ] Background task tracked in a field, exposed as `PendingX` for deterministic tests?
- [ ] Does any save read state this task writes? If so, cancel-and-await it before the save.

## Key reference files

- `src/Netclaw.Cli/Tui/Config/SkillSourcesConfigViewModel.cs` — cleanest probe template (`StartBackgroundProbe`/`RunProbeAsync`, `PendingProbe`)
- `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs` — label-refresh template (`RefreshSlackChannelLabelsAsync` `:1111`, `StartChannelLabelResolution` `:1730`, `CancelAndAwaitLabelRefreshAsync` `:1748`) **and** the `GetResult()` anti-patterns to avoid
- `src/Netclaw.Cli/Tui/Wizard/Steps/ProviderStepViewModel.cs` — probe + cosmetic timer (`StartProbe`, `:155-244`)
- `src/Netclaw.Cli/Tui/Wizard/Steps/HealthCheckStepViewModel.cs` — streaming results into a locked list + version-counter redraw
- `src/Netclaw.Cli/Tui/ChatPage.cs` / `ChatViewModel.cs` / `Daemon/DaemonClient.cs` — live streaming to the front end
- `src/Netclaw.Cli/Tui/SpinnerViews.cs` — self-animating spinner (don't hand-roll)
