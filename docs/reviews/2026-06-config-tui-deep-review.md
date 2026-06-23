# Deep C# implementation review — config/init TUI

_85 findings (15 high, 17 medium, 53 low) from 12 Sonnet reviewers + adversarial verify. {'raw': 129, 'unique': 108, 'verifiedHighMed': 51, 'lowsCarried': 34, 'returned': 85}_

Verdicts: CONFIRMED = verified against code; PLAUSIBLE = real mechanism, runtime-dependent trigger; UNVERIFIED = low-severity, carried without a verify pass.

## HIGH (15)

### [CONFIRMED] concurrency — `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs:1094`
**Concurrent config file writes: background label normalizer races with SaveAsync**

NormalizeSlackChannelNamesToIds (called from the background label-refresh task) calls WriteChannelConfigToDisk at line 1094. SaveAsync also calls WriteChannelConfigToDisk at line 188. SaveAsync never cancels _labelResolutionCts, so a live background task can be writing netclaw.json at the same time a user-triggered Save is writing it. ConfigFileHelper.WriteConfigFile uses File.WriteAllText, which is not atomic. Concurrent writes produce file corruption or silent last-writer-wins overwrites. The ct.IsCancellationRequested guard at line 1039 only fires when StartChannelLabelResolution explicitly replaces the CTS — a normal Save never triggers that cancellation.

_Fix:_ Cancel and await the background label refresh inside SaveAsync before writing to disk: call _labelResolutionCts?.Cancel() at the start of the private SaveAsync overload, then await the outstanding task (track the Task returned by RefreshChannelLabelsAsync) before proceeding to WriteChannelConfigToDisk. Alternatively, serialize writes through a dedicated lock or channel.

_Verifier:_ SaveAsync must cancel and await the outstanding label-resolution task (by tracking the Task returned from RefreshChannelLabelsAsync) before writing to disk; the current fire-and-forget pattern leaves no handle to await or cancel from the Save path.

### [CONFIRMED] concurrency — `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs:1042`
**Background task races on shared mutable adapter view-model state**

RefreshSlackChannelLabelsAsync captures `slack = Step.GetAdapterViewModel<SlackStepViewModel>()` at line 1033, then awaits the probe. After the await (line 1042), it writes slack.LastChannelResolution and at line 1092 calls SetChannelIds which mutates slack.ChannelNamesInput. Concurrently, SaveAsync -> Step.OnEnter -> _mapper.ApplyToStep -> ApplySlack (line 1874) resets slack.BotToken = null, slack.HasPersistedBotToken, slack.ChannelNamesInput, etc. on the same object. No lock or volatile guard exists on these plain auto-properties. The result: the background normalizer can overwrite a freshly-reloaded channel list with a stale pre-probe snapshot (line 1092: SetChannelIds([.. normalized...])), or LastChannelResolution is written after the view-model was reset and the stale result drives the next render.

_Fix:_ Keep a snapshot of channelIds inside RefreshSlackChannelLabelsAsync before the await and verify after the await that the in-memory channel list still matches the snapshot (if not, the save raced — abandon the normalizer write). Long-term, move the disk-write path to an exclusive lock or a sequential async pipeline.

_Verifier:_ The race window is small but real: it requires a slow Slack probe to straddle a concurrent save, after which `NormalizeSlackChannelNamesToIds` overwrites the view-model reset done by `ApplySlack` and persists the stale channel list via `WriteChannelConfigToDisk`.

### [CONFIRMED] concurrency — `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs:1310`
**Blocking sync-over-async on the UI thread for every autosave action**

`AutosaveCompletedAction` is the save path for every in-page mutation — add channel, remove channel, rotate credentials, toggle enable, update audience, DMs, allowed users. All of them funnel through `() => SaveAsync(successMessage).GetAwaiter().GetResult()`. `SaveAsync` itself does network I/O (Slack/Discord/Mattermost channel probes via `ValidateChannelAccessAsync`). Blocking the UI/render thread on a potentially multi-second HTTP call freezes the TUI and, if the calling thread is part of a thread-pool, can cause thread-pool starvation under concurrent autosaves. The pattern at lines 157-158 (`Save()` → `SaveAsync().GetAwaiter().GetResult()`) and 1312 (`SaveAsync(…).GetAwaiter().GetResult()`) are the concrete sites.

_Fix:_ Make all autosave paths properly async: expose `AutosaveCompletedActionAsync` returning `Task`, make the callers (`ApplyAddChannel`, `ApplyAllowedUsers`, `ApplyDirectMessages`, `ApplyCredentials`, `RemoveSelectedChannel`, `ApplyAudienceSelection`, `SetActiveAdapterEnabled`) async, and have the Page `await` them (or fire-and-forget with an unobserved-exception handler). The `Save()` sync wrapper on line 157 should be removed or clearly marked internal-to-tests-only.

_Verifier:_ Every in-page mutation triggers a blocking sync-over-async call that holds the TUI thread through up to three sequential HTTP probes (Slack, Discord, Mattermost), confirmed by the code path from `AutosaveCompletedAction` through `SaveAsync` → `ValidateChannelAccessAsync`; an async overload (`ConfigAutosave.RunAsync`) already exists and is used by the non-mutation save path at line 231, so the fix is straightforward.

### [CONFIRMED] concurrency — `src/Netclaw.Cli/Tui/Wizard/Steps/HealthCheckStepViewModel.cs:55`
**Shared mutable `Results` list written from async task, read from UI render thread without synchronisation**

`Results` is a plain `public List<HealthCheckItem>` (line 55). `RunHealthCheckCoreAsync` (line 147) — running on a threadpool thread — adds items via `runner.Add(...)` and mutates `Results[^1]` (lines 293, 337, 342, 362). The render thread reads `Results` whenever `ResultVersion` emits a new value (page subscriber at InitWizardPage line 124). `List<T>` is not thread-safe; concurrent reads and writes of the list's internal array can produce torn reads, `IndexOutOfRangeException`, or silently wrong output.

_Fix:_ Either use an `ImmutableList` snapshot that the async task replaces atomically (assign via a `ReactiveProperty<IReadOnlyList<HealthCheckItem>>`), or collect results into a thread-local list and publish the snapshot on each `NotifyChanged` call. The `HealthCheckRunner` could hold the list and expose a read-only snapshot property.

_Verifier:_ The race is live on every health-check run: the async task writes to `Results` on a threadpool thread while Termina's render loop reads `Results` via `foreach` on its dispatcher thread, with no synchronisation between them.

### [CONFIRMED] concurrency — `src/Netclaw.Cli/Tui/Wizard/Steps/ProviderStepViewModel.cs:173`
**CTS self-nulling race: `finally { CancelProbe(); }` inside `ProbeProviderAsync` destroys the CTS it was started with**

`ProbeProviderAsync` (line 173) creates `_probeCts = new CancellationTokenSource()` and captures `ct = _probeCts.Token`. Its `finally` block calls `CancelProbe()` (lines 163-168), which cancels, disposes, and sets `_probeCts = null`. If a second `StartProbe()` call races in (e.g. from the OAuth success callback at lines 279, 291), it calls `CancelProbe()` first (destroying the first probe's CTS before the first probe's `finally` runs) and then creates a new `_probeCts`. When the first probe later reaches its `finally`, it now finds and destroys the *second* probe's CTS, cancelling the live probe silently. The `StartOAuthFlow` / `StartBrowserOAuthFlow` paths both call `StartProbe()` from their `onSuccess` callback which runs from an async continuation — exactly the reentrant path.

_Fix:_ Capture the local CTS reference before the async work and dispose only that instance in `finally`: `var localCts = _probeCts = new CancellationTokenSource(); try { ... } finally { localCts.Cancel(); localCts.Dispose(); if (ReferenceEquals(_probeCts, localCts)) _probeCts = null; }`. This removes the self-nulling hazard.

_Verifier:_ The `finally { CancelProbe(); }` block in `ProbeProviderAsync` has no reference-equality guard, so when an OAuth-success-triggered `StartProbe()` races in and replaces `_probeCts` before the original probe's `finally` runs, the first probe's cleanup silently cancels the second probe's live CTS — exactly the scenario the existing comment at `OAuthFlowCoordinator.cs:409-411` tried (and failed) to prevent.

### [CONFIRMED] correctness — `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs:489`
**Left/right audience toggle on ChannelPermissions silently discards changes on Esc**

ChangeSelectedChannelAudience (called by ←/→ on ChannelPermissions, page line 503-508) calls SetChannelAudience which mutates _channelAudiences in memory (line 489) but never calls AutosaveCompletedAction. Every other mutation on the same screen (RemoveSelectedChannel, ApplyAddChannelAsync) does autosave. If the operator presses ←/→ to change a channel's audience and then presses Esc, GoBackWithinManagement fires (line 1281) with no save. The next SaveAsync or load resets _channelAudiences from disk (line 1348), silently discarding the change. The key-binding strip '[←/→] Audience' gives no indication the edit is ephemeral. The DM row (Id='dm') is equally affected when it is focused on the ChannelPermissions list.

_Fix:_ Call AutosaveCompletedAction immediately after SetChannelAudience in ChangeSelectedChannelAudience, matching the pattern used by RemoveSelectedChannel, or add a 'save' key to ChannelPermissions and display a pending-changes indicator so the operator knows unsaved edits exist.

_Verifier:_ The ←/→ audience toggle on the ChannelPermissions screen is the only mutation in that screen group that skips AutosaveCompletedAction, making in-place audience edits silently ephemeral whenever the operator navigates away without pressing Enter through the EditAudience flow.

### [CONFIRMED] correctness — `src/Netclaw.Cli/Tui/Wizard/Steps/HealthCheckStepViewModel.cs:195`
**WriteConfig() called unconditionally before health-check results are evaluated**

orchestrator.WriteConfig() (which writes netclaw.json, secrets.json, identity files, provider credentials, and bootstrap device) is called on line 195 inside the try block, before the allPassed evaluation on line 211. If any step's health check emits a failing result (e.g., the LLM provider returns a bad status), the config is still written and committed to disk. A running daemon's ConfigWatcherService then picks up that config and performs an in-process restart onto potentially incomplete or invalid settings. The comment on line 207 implies writing config is the restart trigger, so this is load-bearing: writing before confirming all checks pass means a failed-validation run corrupts an existing working config.

_Fix:_ Evaluate allPassed (runner.AllPassed) after RunHealthChecksAsync completes and before the WriteConfig call. Only proceed to write config if allPassed is true. The existing comment 'Writing config already triggered a running daemon' should describe this as intentional-only-on-pass.

_Verifier:_ The unconditional write at line 195 is the load-bearing defect: a failed provider health check still commits potentially invalid credentials to disk and fires the daemon's config-reload restart via `ConfigWatcherService`, which can replace a working config with a broken one before `allPassed` is ever consulted.

### [CONFIRMED] correctness — `src/Netclaw.Cli/Tui/Wizard/Steps/HealthCheckStepViewModel.cs:115`
**Unexpected exception in RunHealthCheckCoreAsync permanently wedges the wizard in IsRunning=true / IsComplete=false**

RunWithOrchestrator catches only OperationCanceledException from its own overallCts. If RunHealthCheckCoreAsync throws any other exception (for example an I/O error in a step's ContributeHealthChecksAsync, or an unexpected failure not covered by the inner try/catch blocks), the outer async task faults and returns. IsRunning is never reset to false and IsComplete is never set to true. The wizard is permanently stuck: the health-check step appears to still be running, the operator cannot advance or go back (GoNext checks !IsRunning && !IsComplete before calling StartWithOrchestrator), and there is no visible error message. The bug is a missing catch-all handler in RunWithOrchestrator that sets IsRunning=false, IsComplete=true and surfaces an error.

_Fix:_ Wrap the body of RunWithOrchestrator in a general try/catch that ensures IsRunning=false and IsComplete=true are set in all exit paths, e.g.:
```csharp
catch (Exception ex)
{
    Results.Add(new HealthCheckItem($"Health check failed: {ex.Message}", false));
    IsRunning.Value = false;
    IsComplete.Value = true;
    NotifyChanged();
}
```

_Verifier:_ The unguarded `await orchestrator.RunHealthChecksAsync(runner, ct)` at line 157 is the concrete trigger: any exception from a step's health-check contribution escapes both RunHealthCheckCoreAsync and RunWithOrchestrator's single narrow catch, leaving IsRunning=true/IsComplete=false with no recovery path.

### [CONFIRMED] error-handling — `src/Netclaw.Cli/Tui/Config/SecurityAccessViewModel.cs:650`
**DaemonConfig.ParseExposureMode throws on unknown strings and is called unguarded from the render path**

`ReadExposureModeSummary` (line 646–661) calls `DaemonConfig.ParseExposureMode(value?.ToString())` without a try-catch. `ParseExposureMode` throws `InvalidOperationException` for any unrecognised string (line 157–159 in `DaemonConfig.cs`). `ReadExposureModeSummary` is called from `BuildItems()`, which is the body of the `Items` property (line 127). `Items` is accessed synchronously in the Termina render path (`BuildSecurityMenu`, line 68 of `SecurityAccessPage.cs`) and also in `MoveSelection`/`ActivateSelected`. A hand-edited or migrated config with an unsupported `Daemon.ExposureMode` value will therefore crash the render and leave the Security & Access page permanently broken with an unhandled exception.

_Fix:_ Wrap the `ParseExposureMode` call in `ReadExposureModeSummary` with a try-catch and return a fallback label (e.g., `value?.ToString() ?? "Unknown"`) so the page degrades gracefully. The same guard is also missing in `ExposureModeStepViewModel.ReadExistingMode` (line 558) which is called from `TryPrefillFromExisting` during wizard entry.

_Verifier:_ Any hand-edited or migrated config with an unrecognized `Daemon.ExposureMode` string will throw an unhandled `InvalidOperationException` on every render frame of the Security & Access page, permanently breaking that page; the fix pattern already exists in `ExposureModeDoctorCheck.cs` and just needs to be applied to both `ReadExposureModeSummary` and `ExposureModeStepViewModel.ReadExistingMode`.

### [CONFIRMED] error-handling — `src/Netclaw.Cli/Tui/Config/SkillSourcesConfigViewModel.cs:1935`
**Config file write exceptions propagate unhandled to the TUI event loop**

`SaveExternalConfig` (line 1926) and `SaveSkillFeedsConfig` (line 1938) both call `ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, root)` with no surrounding try/catch. `WriteConfigFile` calls `File.WriteAllText` which throws `IOException`, `UnauthorizedAccessException`, or `PathTooLongException` on disk full, permission denied, or path issues. All callers of these two methods — `ToggleEnabled`, `ToggleLocalSymlinks`, `CycleRemoteSyncInterval`, `RemoveRemoteToken`, `SaveRename`, `SaveLocalPathChange`, `SaveRemoteUrlChange`, `SaveRotatedRemoteToken`, `SaveNewLocalSource`, `SaveNewRemoteSource`, `RemoveSource` — are similarly unguarded. An IO error here will surface as an unhandled exception in the TUI event loop. The same issue exists in `WorkspacesConfigViewModel.Save()` at line 148: the `ConfigFileHelper.WriteConfigFile` call sits outside the existing `try/catch` block.

_Fix:_ Wrap `WriteConfigFile` calls in both `SaveExternalConfig` and `SaveSkillFeedsConfig` with a `try/catch` for `IOException or UnauthorizedAccessException or PathTooLongException` and surface the error via `SetStatus`. Apply the same fix to `WorkspacesConfigViewModel.Save()` at line 148. This matches the existing pattern used for `Directory.CreateDirectory` in the same file.

_Verifier:_ All callers of `SaveExternalConfig` and `SaveSkillFeedsConfig` go through `CommitStructural`/`CommitSourceAction` which have no exception handling, and `WorkspacesConfigViewModel.Save()` line 148 is outside the only try/catch in that method — any disk-write IO error crashes the TUI event loop rather than surfacing via `SetStatus`.

### [CONFIRMED] error-handling — `src/Netclaw.Cli/Tui/Wizard/Steps/HealthCheckStepViewModel.cs:115`
**Unhandled exception in RunWithOrchestrator leaves IsRunning=true, UI permanently frozen**

RunWithOrchestrator catches only OperationCanceledException when overallCts.IsCancellationRequested. Any other exception propagating from RunHealthCheckCoreAsync — for example a TaskCanceledException not matching the filter, an ObjectDisposedException, or an unexpected exception from WriteConfig — escapes unhandled. Because IsRunning and IsComplete are set in RunHealthCheckCoreAsync's body (not a finally block), they stay at IsRunning=true / IsComplete=false. The task stored in HealthCheckCompletion carries the unobserved exception. The UI is stuck: the guard at InitWizardViewModel.GoNext (line 161) checks `!healthStep.IsRunning.Value && !healthStep.IsComplete.Value`, which never becomes true again, so the user can never retry. The only exit is Ctrl+Q.

_Fix:_ Wrap RunHealthCheckCoreAsync in a try/catch-all inside RunWithOrchestrator, or move `IsRunning.Value = false; IsComplete.Value = true; NotifyChanged();` into a finally block at the bottom of RunHealthCheckCoreAsync. All exit paths through that method should set IsComplete=true.

_Verifier:_ The bug is real: any non-cancellation exception from `RunHealthCheckCoreAsync` (most likely from `RunHealthChecksAsync` or `StartIfNeededAndPollAsync`) leaves `IsRunning=true/IsComplete=false` permanently, freezing the UI; the fix is a `finally` block setting both flags or a catch-all in `RunWithOrchestrator`.

### [CONFIRMED] security — `src/Netclaw.Cli/Mcp/McpToolPermissionsViewModel.cs:533`
**BuildAllowedServerList mutates the live in-memory profile object**

`BuildAllowedServerList` (called from `SaveServerAccess`) directly mutates `profile.McpServersMode = ToolProfileMode.Allowlist` (line 533) and `profile.AllowedMcpServers = serverList` (line 539) on the in-memory `ToolAudienceProfile` object returned by `ResolveProfile`. `ResolveProfile` returns one of `Profiles.Public`, `Profiles.Team`, or `Profiles.Personal` — the same object used by `IsServerAllowed`, `IsToolGranted`, and `GetEffectiveMode` for access-control decisions. Mutating it mid-save means subsequent query calls see the post-save (allowlist) mode even if `Save()` is interrupted by an exception after the mutation but before the file write. On a multi-server save loop this also means the second server's `BuildAllowedServerList` call reads a profile that was already coerced to Allowlist, losing the original All-mode expansion for any servers beyond the first.

_Fix:_ Work on a local copy instead of the live profile object. Read `profile.McpServersMode` and `profile.AllowedMcpServers` into local variables at the start, compute the new list, then write those values directly to the serialization dictionary (`audienceSection["McpServersMode"]` and `audienceSection["AllowedMcpServers"]`) without touching the in-memory profile at all.

_Verifier:_ Both defects are real: exception-after-mutation leaves the in-memory ACL in a coerced allowlist state, and the multi-server save loop reads an already-mutated profile for every entry beyond the first of the same audience.

### [CONFIRMED] security — `src/Netclaw.Cli/Tui/Config/SecurityAccessViewModel.cs:634`
**Silent fallback to Personal posture when DeploymentPosture cannot be parsed**

`ReadPosture` (line 634–643) silently returns `DeploymentPosture.Personal` when `Enum.TryParse<DeploymentPosture>` fails (unrecognised string, numeric out-of-range, etc.). `Personal` posture is the most permissive: `SavePosture` maps it to `ShellExecutionMode.HostAllowed` (line 469–470). The consequence is that a config whose stored posture string is unrecognised (e.g., a stale value from a renamed enum member, or a hand-edited file) will be displayed as `Personal` in the Security & Access menu and — if the operator happens to re-save audience profiles — will silently overwrite the profiles with the widest defaults. The daemon's own `TrustContextPolicy.ResolveDeploymentPosture` (line 337) correctly falls back to `DeploymentPosture.Public` when `strictDefaults: true`, so the UI and the runtime are using opposite safe-failure directions. CLAUDE.md forbids silent fallbacks on security-relevant paths.

_Fix:_ Surface a parse failure as an explicit error rather than silently assuming Personal. One option: return `DeploymentPosture?` (nullable) and render a visible warning ('Unknown posture — verify your config') in place of the posture label. Alternatively mirror the runtime fallback and return `DeploymentPosture.Public` to stay fail-closed.

_Verifier:_ The silent `Personal` fallback in `ReadPosture` is directly contradicted by the runtime's own `TrustContextPolicy.ResolveDeploymentPosture` which defaults to the most restrictive `Public` posture, creating an exploitable mismatch: a hand-edited or stale-enum config value would be treated as maximally permissive by the UI but maximally restrictive by the daemon until the operator re-saves through the UI and locks in `HostAllowed`.

### [CONFIRMED] security — `src/Netclaw.Cli/Tui/ProviderManagerViewModel.cs:519`
**Fix-credentials writes secrets to disk before probe succeeds**

In `SubmitFixCredentials`, the updated API key is written to `secrets.json` (line 536–545) and the updated endpoint is written to `netclaw.json` (lines 547–558) before `StartProbe()` is called and before the probe result is checked. If the probe fails the user is left with an invalid credential on disk that replaces the old one, with no rollback. The config write path in ProviderManagerViewModel for normal add flows correctly defers the write to `WriteProviderConfig()` after `result.Success` (line 966–969). The fix-credentials path bypasses that guard entirely. This means a typo in the new API key permanently clobbers the old secret.

_Fix:_ Defer the secrets/config write from `SubmitFixCredentials` to the `IsFixFlow` success branch inside `ProbeProviderAsync` (around line 955). Capture `FixApiKey` and `FixEndpoint` to local state, then write only when `result.Success` is true. This matches the existing pattern for the normal add flow.

_Verifier:_ The fix-credentials path overwrites `secrets.json` and `netclaw.json` before the probe runs and has no rollback, permanently clobbering the old credential if the user types a bad API key.

### [CONFIRMED] security — `src/Netclaw.Cli/Tui/Wizard/Steps/ExposureModeStepViewModel.cs:392`
**Non-atomic write to devices.json corrupts the paired-device registry on interrupted saves**

`WritePairedDevices` (called from `EnsureCurrentClientPaired`) uses `File.WriteAllText` (line 392) — not a write-to-temp-then-rename pattern. If the CLI process is killed or the machine loses power between truncation and the completed write, `devices.json` is left empty or partially written. `ReadPairedDevices` then returns `[]` on a `JsonException`, so the daemon starts with zero valid paired devices and rejects all inbound connections. The identical non-atomic pattern occurs in `WriteBootstrapDevice` (line 472). The security impact is an accidental self-lockout: after a power failure during `netclaw config`, no client can reach the daemon until `netclaw doctor --fix` or a manual device-pair is performed.

_Fix:_ Write to a sibling temp file (e.g. `devices.json.tmp`) and then `File.Move(..., overwrite: true)` to replace atomically. Apply the same pattern in `WriteBootstrapDevice`. `File.SetUnixFileMode` can be applied to the temp file before the rename.

_Verifier:_ Both write sites use `File.WriteAllText` with no temp-file-then-rename guard, and `ReadPairedDevices` silently swallows a `JsonException` from a torn write, so a process kill or power loss during the narrow write window leaves `devices.json` corrupted and the daemon self-locked out.

## MEDIUM (17)

### [CONFIRMED] concurrency — `src/Netclaw.Cli/Tui/Config/SearchConfigEditorPage.cs:161`
**async void subscribe leaks exceptions from RetryValidation path**

BuildProbeWarningDialog subscribes with `Subscribe(async selected => { ... await ViewModel.SubmitCurrentConfigurationAsync(); ... })`. In R3, Subscribe with an async lambda compiles to an async void delegate. SubmitCurrentConfigurationAsync has no top-level try/catch — only ProbeAsync (line 467) does. Any IOException from SaveWithoutProbeOverride (called inside RunDynamicValidationAsync on persist success), or an InvalidOperationException from CommitField with an unexpected path, propagates through the async void context and escapes to the thread pool, crashing the process. The Enter-key path is guarded by SubmitCurrentConfigurationFromInputAsync's try/catch (line 306), but the RetryValidation dialog path is not.

_Fix:_ Wrap the async lambda body in try/catch(Exception ex) that sets Status.Value to an error message and calls RequestRedraw(), mirroring the pattern in SubmitCurrentConfigurationFromInputAsync. Alternatively, call SubmitCurrentConfigurationFromInputAsync (which already wraps) instead of SubmitCurrentConfigurationAsync.

_Verifier:_ The fix is to call `SubmitCurrentConfigurationFromInputAsync` instead of `SubmitCurrentConfigurationAsync` in the RetryValidation case (line 173), which already has the required try/catch wrapper and mirrors the Enter-key path.

### [CONFIRMED] concurrency — `src/Netclaw.Cli/Tui/Config/SkillSourcesConfigViewModel.cs:748`
**Synchronous blocking on async task (`ValidateAddRemoteTokenReachabilityAsync(...).AsTask().GetAwaiter().GetResult()`) can stall the UI thread**

Both `CommitAddRemoteToken` (line 748) and `CommitChangeLocation` (line 775) call `ValidateAddRemoteTokenReachabilityAsync` and `ValidateChangeLocationReachabilityAsync` via `.AsTask().GetAwaiter().GetResult()`. These are called from the UI key-handler path (i.e., from within the Termina render/input loop). The underlying reachability probe uses `HttpClient.Send` (blocking, at line 44 of the `SkillFeedReachabilityProbe`), but wrapping it in a `ValueTask` and then blocking with `.GetResult()` on the UI thread still freezes the terminal UI for the full probe timeout (up to 10 seconds per `timeoutSeconds`). This is a UX correctness issue that also risks starvation under probe load.

_Fix:_ Run the reachability probe on a background thread explicitly (e.g., `await Task.Run(() => _probe.Probe(...))`) and make the commit methods async, or show a progress indicator and defer the commit result to an async path.

_Verifier:_ The finding's attribution of the block to .GetAwaiter().GetResult() is technically imprecise — the blocking occurs inside ValidateAddRemoteTokenReachabilityAsync via synchronous HttpClient.Send before ValueTask.FromResult is called — but the UI-thread freeze of up to 10 seconds is fully confirmed.

### [CONFIRMED] concurrency — `src/Netclaw.Cli/Tui/ProviderManagerViewModel.cs:714`
**RevalidateAsync is fire-and-forget with CancellationToken.None and no cancellation path**

`RevalidateDetailProvider` (line 714) launches `RevalidateAsync` as a fire-and-forget task (`_ = RevalidateAsync(DetailProvider)`). Unlike the main probe path (`ProbeProviderAsync`) which uses a tracked `_probeCts` that `CancelProbe()` / `GoBackToList()` / `Dispose()` cancels, `RevalidateAsync` uses `CancellationToken.None` throughout and stores no task reference. If the user navigates away (triggers `GoBackToList`) or the view model is disposed while a re-validation is in flight, the task continues running and then calls `NotifyStateChanged()` on the disposed VM — which mutates `StateVersion.Value` on a disposed `ReactiveProperty`. Unhandled exceptions from the empty `catch {}` block are also silently discarded.

_Fix:_ Store `RevalidateAsync` in a tracked `Task?` field (similar to `ProbeCompletion`/`EagerProbeCompletion`) and pass `_probeCts.Token` (or a separate dedicated CTS) so `GoBackToList` and `Dispose` can cancel it. Add a null-guard or disposed-flag check before calling `NotifyStateChanged()` in the continuation.

_Verifier:_ The race is real and reproducible whenever the user navigates away while a revalidation probe is in flight: `NotifyStateChanged()` at line 743 sits outside the try-catch, so the `ObjectDisposedException` from writing to the disposed `StateVersion` ReactiveProperty escapes into the fire-and-forgot task and is silently lost.

### [CONFIRMED] concurrency — `src/Netclaw.Cli/Tui/Wizard/Steps/DiscordStepViewModel.cs:300`
**Data race on LastChannelResolution and ChannelEntry.DisplayName between background Task.Run and UI thread**

`StartBackgroundChannelResolution()` writes `LastChannelResolution = result` (line 308) and mutates `entry.DisplayName = resolved.ToDisplayName()` (line 339) from a thread-pool thread. The UI thread reads `LastChannelResolution` in `ContributeHealthChecksAsync` (lines 244, 256) and `OnLeave` (line 178), and reads `entry.DisplayName` during render. `LastChannelResolution` is a plain auto-property field with no `volatile`, `Interlocked`, or lock. `ChannelEntry.DisplayName` is a plain mutable `string` property. There is no memory barrier or synchronization between producer (Task.Run) and consumers. This is a real C# memory-model race: the UI thread can observe a torn or stale reference.

_Fix:_ Replace the background fire-and-forget with an `await`-able prefetch, or guard `LastChannelResolution` with `volatile` and protect the `ChannelEntries` list mutation with a lock or a marshal back to the UI thread (e.g. capture the `SynchronizationContext` before `Task.Run` and post the mutation back). The simplest safe fix: remove the background prefetch and do resolution entirely inside `ContributeHealthChecksAsync`, which already runs serially on the health-check phase.

_Verifier:_ The race is architecturally real with no formal synchronization, but practical impact is low: the background prefetch starts when the user advances past sub-step 2, and the conflicting reads only occur when the user actively navigates to OnLeave or triggers the health-check step — a substantial human-paced time gap that makes a torn read unlikely in practice, but not impossible (e.g., a fast network response racing the user's immediate next keypress).

### [CONFIRMED] correctness — `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs:582`
**Single() in ApplyAddChannelAsync throws when resolved channel ID equals "dm" and DMs are enabled**

After AddChannelAsync resolves and stores the new channel (line 579), it calls GetChannelRows().Single(entry => entry.row.Id == channelId) at line 582-585 to position _channelRowIndex. GetChannelRows() includes a DM row with Id='dm' whenever AllowDirectMessages is true (line 411-417). If the probe returns a channelId of exactly "dm" — possible for a Mattermost channel with that internal ID, or for a Discord guild channel that coincidentally resolves to that string — Single() finds two matching rows and throws InvalidOperationException, crashing the AddChannel flow with an unhandled exception propagated through ApplyAddChannel -> GetAwaiter().GetResult() (line 526).

_Fix:_ Replace Single() with FirstOrDefault() on non-action, non-DM rows, or match explicitly against `!row.IsDirectMessage && !row.IsAction && row.Id == channelId`. Also guard the result against null/not-found rather than assuming the channel is always present in the rows list.

_Verifier:_ The bug is real but requires the improbable combination of a user entering "dm" as a channel ID, the probe accepting it, and AllowDirectMessages being true — medium severity is correct.

### [CONFIRMED] correctness — `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs:477`
**Arrow-key audience changes on the channel-permissions list are never persisted**

`ChangeSelectedChannelAudience` (called from the page on LeftArrow/RightArrow at `ChannelsConfigPage.cs:504,507`) mutates `_channelAudiences` and calls `NotifyContentChanged()` but does NOT call `AutosaveCompletedAction` or `WriteChannelConfigToDisk`. Every other mutation in the channel-permissions screen (`RemoveSelectedChannel`, `ApplyAudienceSelection`, `OpenChannelPermissionsAfterInitialSetup`) autosaves. If the user presses ←/→ to cycle the audience and then navigates away without pressing Enter (which would open `EditAudience` screen and call `ApplyAudienceSelection`), the audience change is silently lost on the next `SaveAsync` reload (which calls `LoadAudienceDrafts(savedDraft)`, clobbering in-memory state with the persisted state).

_Fix:_ Either call `AutosaveCompletedAction(...)` at the end of `ChangeSelectedChannelAudience` (matching every other mutation), or remove the ←/→ shortcut from the channel-permissions screen and rely solely on the Enter → EditAudience → Enter flow that does save.

_Verifier:_ Every other mutation on the channel-permissions screen calls `AutosaveCompletedAction`; `ChangeSelectedChannelAudience` is the sole exception, so its audience change is silently lost on the next `SaveAsync` reload.

### [CONFIRMED] correctness — `src/Netclaw.Cli/Tui/Config/SkillSourcesConfigViewModel.cs:29`
**Blocking HTTP probe freezes the TUI input loop for up to 10 seconds**

`SkillFeedReachabilityProbe.Probe()` calls `client.Send(request, cts.Token)` — the synchronous blocking overload — clamped to a 10-second timeout. This method is invoked directly on the TUI event-loop thread from `ProbePendingRemoteThenReview()` (line 1138), `TestSource()` (line 1332), `ValidateChangeLocationReachabilityAsync` (line 1462), `ValidateAddRemoteTokenReachabilityAsync` (line 1096/1103), and both `SaveRemoteUrlChange` / `SaveRotatedRemoteToken`. During the probe the entire TUI is frozen: no render, no keypress, no Ctrl+Q. A server that holds the TCP connection open without responding will stall the UI for the full 10-second window. The `ValueTask.AsTask().GetAwaiter().GetResult()` wrappers in `CommitAddRemoteToken` (line 748) and `CommitChangeLocation` (line 775) look like async code but those methods return `ValueTask.FromResult(...)` synchronously — the probe itself is the blocking call.

_Fix:_ Move `SkillFeedReachabilityProbe.Probe` to a true async implementation using `client.SendAsync` and switch `ISkillFeedReachabilityProbe` to return `Task<SkillFeedReachabilityResult>`. Run the probe on a background thread via `Task.Run` so the TUI event loop stays responsive, updating the status bar with a 'Testing...' indicator while the probe is in-flight. Alternatively, cap the probe timeout to 3–4 seconds for UI flows specifically.

_Verifier:_ Every probe call site is synchronous on the TUI event-loop thread with no background-thread offload, freezing rendering and input for up to 10 seconds (the effective clamp in `Probe()`, line 33); the finding is accurate and the medium severity is appropriate since the freeze is bounded by the clamp rather than user-configured timeouts which can be 30–60 s.

### [CONFIRMED] correctness — `src/Netclaw.Cli/Tui/Config/TelemetryAlertingConfigViewModel.cs:289`
**Editing a webhook without re-entering the auth header silently preserves the old header; there is no way to intentionally clear it**

In SaveWebhookForm (line 278-298), `newAuth = !string.IsNullOrWhiteSpace(authDraft)`. When editing an existing webhook with HasAuthHeader=true and leaving the auth field blank, newAuth=false so `target.Headers = ...` is skipped (line 289-295). The persisted `target.Headers` from the freshly loaded JSON is left unchanged — the old header is preserved. This is the intended "preserve" behavior documented by EditingHasPersistedAuthHeader. However, there is no mechanism to intentionally remove a persisted auth header: entering a blank value preserves it, and there is no "clear header" gesture. A user who wants to remove an auth header has no path to do so through the TUI.

_Fix:_ Add an explicit removal gesture (e.g., entering a single hyphen `-` in the auth field, or a dedicated "[D] Delete header" keybinding). When the removal signal is present, set `target.Headers = null` or `new Dictionary<...>()` before persisting.

_Verifier:_ A user who has set an auth header on a webhook has no TUI path to remove it — blank input silently preserves the old header and no deletion gesture exists.

### [CONFIRMED] correctness — `src/Netclaw.Cli/Tui/Wizard/Steps/SlackStepViewModel.cs:299`
**BuildChannelAudiences uses channel name as ChannelAudiences key when channel resolution failed — runtime ACL cannot look up by name**

`ResolveChannelAudienceKey(entry)` (line 315) returns `entry.Id` (the channel name, e.g. `general`) when `LastChannelResolution is null` or when the name cannot be matched to a resolved ID. `ContributeConfig` writes this as the key into `SlackConfigSection.ChannelAudiences`. The Slack runtime adapter resolves channel IDs, not names — it expects the canonical Slack channel ID (e.g. `C012AB3CD`) as the audience map key. When health check was skipped or channel resolution failed, the wizard silently writes name-keyed entries that the runtime ACL will never match, effectively dropping the audience configuration without any error. The CLAUDE.md constitution prohibits silent fallbacks on security paths.

_Fix:_ If `LastChannelResolution` is null or contains unresolved channels, either (a) block the wizard from proceeding (require successful channel resolution before config is written), or (b) write an explicit warning to the health-check results and omit `ChannelAudiences` from the config so the runtime falls back to posture defaults rather than silently using a dead key.

_Verifier:_ The mechanism is real — name-keyed `ChannelAudiences` entries are silently written when resolution is skipped, and the runtime ACL key-lookup exclusively uses Slack channel IDs; the effective impact is partially mitigated because `AllowedChannelIds` is also null in the same path (blocking all channels anyway), but the silent, dead config write on a security path still violates the constitution's no-silent-fallbacks rule.

### [CONFIRMED] design — `src/Netclaw.Cli/Tui/Config/SecurityAccessViewModel.cs:127`
**Items property triggers a full disk read on every access, including every key press and every render**

`public IReadOnlyList<SecurityAccessItem> Items => BuildItems()` (line 127) is a computed property with no caching. `BuildItems()` calls `ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath)` which reads and deserialises the config file from disk. In `SecurityAccessPage`, `ViewModel.Items` is accessed in `BuildSecurityMenu` (render path) and also in `MoveSelection` (line 140) and `ActivateSelected` (line 160). A single ↑/↓ key press in the menu therefore triggers two full file reads (one in `MoveSelection`, one in the triggered render). Similarly `CurrentPosture` (line 135) is a property that reads disk; it is called four to six times inside a single `BuildAudienceProfile` render. The cumulative overhead is perceptible on slow filesystems or NFS homes.

_Fix:_ Cache the loaded config dictionary for the duration of a single render cycle, or snapshot it in `OpenPostureEditor`/`OpenAudienceList` and invalidate the snapshot on explicit saves. At minimum, local variables should be used inside methods that call `CurrentPosture` or `Items` more than once.

_Verifier:_ Every call to `Items` or `CurrentPosture` unconditionally reads and deserialises the config file from disk; a single ↑/↓ keypress in the menu triggers at least two full file reads, and the posture/audience render paths each trigger four or more, with no caching anywhere in the call chain.

### [CONFIRMED] design — `src/Netclaw.Cli/Tui/Config/SkillSourcesConfigViewModel.cs:161`
**God-object: `SkillSourcesConfigViewModel` mixes two disjoint persistence backends, inline config serialization, probing, and all 11 screen transitions — 2,267 lines**

The viewmodel directly owns: (1) `ExternalSkillsConfig` (local folder) JSON read/write — `LoadExternalConfig`, `SaveExternalConfig`, `BuildExternalSkillsSection`; (2) `SkillFeedsConfigDocument` (remote feeds) JSON read/write — `LoadSkillFeedsSection`, `SaveSkillFeedsConfig`, `BuildSkillFeedsSection`; (3) `ISkillFeedReachabilityProbe` with a `_saveAnywayFingerprint` probe-bypass mechanism; (4) inline decryption (`TryDecryptExistingApiKey`) and encryption (`ProtectApiKeyForConfig`); (5) the entire add-local / add-remote multi-screen wizard (11 `SkillSourcesScreen` values); (6) display-string formatting helpers. The two persistence backends are not abstracted at all — both `SaveExternalConfig` and `SaveSkillFeedsConfig` rebuild the entire config root from disk, mutate it, and write it back, leading to a read-modify-write per operation (6 disk reads in a single `ToggleEnabled` for a remote source).

_Fix:_ Extract a `LocalSkillSourceRepository` and `RemoteSkillFeedRepository` for the two config backends. Move the add-flow state machine (`_pendingLocalPath`, `_pendingRemoteUrl`, `_pendingRemoteAuthMode`, `_pendingRemoteApiKey`, `_pendingRemoteTimeoutSeconds`, `_saveAnywayFingerprint`, `_editingAction`) into a `SkillSourceAddFlowState` struct. Move probe/validation methods to a `SkillSourceValidator`. The viewmodel becomes a thin coordinator.

_Verifier:_ The "6 disk reads in a single ToggleEnabled" claim is an overcount — the actual path issues 3 redundant reads (load, save, reload), not 6 — but the core god-object design finding is entirely accurate and the redundant read-modify-write pattern is confirmed at every mutation site.

### [CONFIRMED] error-handling — `src/Netclaw.Cli/Tui/Config/SecurityAccessViewModel.cs:552`
**ConvertConfigObject throws unguarded from every audience-profile mutation path**

`LoadAudienceProfiles` (line 552–558) calls `ConvertConfigObject<ToolAudienceProfiles>`, which throws `InvalidOperationException` when the stored JSON cannot be deserialised (line 857–862). This method is called from `ToggleToolGroup`, `CycleFileAccess`, `CycleIncomingAttachments`, `GetSelectedProfile`, and `AudienceHasOverrides`. None of those callers catch the exception. If `Tools.AudienceProfiles` is present in the config but has a schema mismatch (e.g., after a migration that changed the shape of `ToolAudienceProfiles`), every keystroke on the Audience Profile sub-page will throw, crashing the render loop. The same unguarded `ConvertConfigObject` path exists in `ReadAudienceProfilesSummary` (line 621) and `AudienceProfilesCustomized` (line 567).

_Fix:_ Catch `InvalidOperationException` in `LoadAudienceProfiles` and fall back to `BuildPostureProfiles(ReadPosture(config))` with a status warning to the operator. Do the same in `AudienceProfilesCustomized` (treat as uncustomised on failure) and `ReadAudienceProfilesSummary`.

_Verifier:_ A stale or migrated `Tools.AudienceProfiles` JSON blob would cause every keystroke on the Audience Profile sub-page — and every render of the Audience List page — to throw an unhandled `InvalidOperationException`, crashing the TUI render loop.

### [CONFIRMED] error-handling — `src/Netclaw.Cli/Tui/ConfigDashboardViewModel.cs:245`
**SkillSourcesSummary and TelemetrySummary call LoadSection unconditionally — throws on malformed config during dashboard layout render**

`SkillSourcesSummary` calls `ConfigFileHelper.LoadSection<ExternalSkillsConfig>(config, "ExternalSkills").Sources.Count` and `LoadSection<SkillFeedsConfig>(config, "SkillFeeds").Feeds.Count` (lines 247–248) without any exception handling. `LoadSection` deserializes the raw JSON section via `JsonSerializer.Deserialize<T>` — if the section exists but is malformed (e.g. `Sources` is a number instead of a list after a hand-edited config), deserialization throws a `JsonException`. This exception propagates out of `Summarize`, through `StatusFor`, and into `BuildLayout` / `BuildList` in `ConfigDashboardPage`. `BuildLayout` is called from the Termina render loop, so an unhandled exception here can crash the dashboard page entirely. The same applies to `TelemetrySummary` at line 272 via `LoadSection<NotificationsConfig>`. All other summary methods use `TryGetPathValue` which returns false on type mismatches.

_Fix:_ Wrap the `LoadSection` calls in `try/catch (Exception)` and return a fallback string like `"– config error"` on failure. Alternatively, refactor to use `TryGetPathValue` for the count fields, consistent with how other summaries read config.

_Verifier:_ A hand-edited config with e.g. `"Sources": 42` instead of an array will throw a `JsonException` from `JsonSerializer.Deserialize` inside `DeserializeSection`, propagate unhandled through the render loop, and crash the dashboard page — no guard exists anywhere in the call chain.

### [CONFIRMED] resource-leak — `src/Netclaw.Cli/Tui/Config/SearchConfigEditorPage.cs:24`
**_contentSubscriptions CompositeDisposable is never disposed on page teardown**

SearchConfigEditorPage declares `private readonly CompositeDisposable _contentSubscriptions = []` (line 24) and populates it via `.DisposeWith(_contentSubscriptions)` inside BuildProbeWarningDialog (line 180). The DynamicLayoutNode lambda calls `_contentSubscriptions.Clear()` on each rebuild (line 74), which disposes outstanding subscriptions — correct for the rebuild cycle. However, the page has no Dispose() override, and ReactivePage<T>.Dispose() (confirmed by decompiling Termina 0.12.1) only disposes its own private `_subscriptions` and `_layoutSubscriptions` fields. When the framework disposes the page, _contentSubscriptions itself is never disposed. If the page is torn down while a ProbeWarning dialog subscription is live (e.g., the user quits during the dialog), that subscription is leaked.

_Fix:_ Add `protected override void Dispose(bool disposing)` (or override `Dispose()`) in SearchConfigEditorPage and call `_contentSubscriptions.Dispose()` there. Pattern: `public override void Dispose() { _contentSubscriptions.Dispose(); base.Dispose(); }`.

_Verifier:_ The leak is real but bounded to the ProbeWarning dialog subscription lifetime — it terminates when the upstream observable completes (page/process shutdown), so in practice this is a short-lived leak on normal Ctrl+Q quit, not an indefinite one.

### [CONFIRMED] security — `src/Netclaw.Cli/Tui/Config/BrowserAutomationConfigViewModel.cs:292`
**IsServerEnabled falls back to true for unrecognized JSON shapes — violates default-deny posture**

IsServerEnabled (line 275-296) returns true in two fallback branches: when raw is a JsonElement that is an Object but lacks an Enabled property (line 293), and when raw is any other type (line 296). This means an MCP server entry that exists in the config file but omits the Enabled field is treated as enabled=true. In a default-deny repository, the invariant should be: absent = disabled. A hand-edited or externally synthesized config entry without Enabled could silently activate a browser MCP server without the operator ever explicitly enabling it via the TUI. This is the 'silent fallback to permissive default' anti-pattern prohibited by CLAUDE.md.

_Fix:_ Change both fallback branches to return false. A server entry must have an explicit `"Enabled": true` to be considered enabled. If the intent is backward compatibility (entries created before Enabled was added), document that explicitly and add a note to the migration guidance rather than silently enabling.

_Verifier:_ Any hand-edited or externally generated config entry for the Playwright or ChromeDevTools MCP server that omits the `Enabled` field will be silently treated as enabled=true, violating the repo's explicit no-silent-fallback and default-deny rules; both fallback `return true` branches (lines 293 and 296) are reachable in practice.

### [CONFIRMED] security — `src/Netclaw.Cli/Tui/Config/SkillSourcesConfigViewModel.cs:2111`
**Plaintext API keys in config are silently accepted and used without any user notification**

`TryDecryptExistingApiKey` at line 2116 checks `ISecretsProtector.IsEncrypted(apiKey)` and, if the key is not prefixed with `"ENC:"`, returns the raw value as `plaintext` with no error or warning. A manually edited or migrated `netclaw.json` with a plaintext bearer token will be probed and used without informing the operator that the credential is stored unprotected. This contradicts the CLAUDE.md rule: 'No silent fallbacks... on security-relevant paths'. The token is subsequently used in the Authorization header (line 42 in the probe) and is exposed in `Draft.Value` (a `public ReactiveProperty<string>`) during the RotateToken flow.

_Fix:_ When `TryDecryptExistingApiKey` detects a non-encrypted key, set a status warning (via the existing `SetStatus` path) informing the user that the stored credential is unencrypted and recommend rotating it. Alternatively, opportunistically re-encrypt it on next read by calling `ProtectApiKeyForConfig` and writing the encrypted value back before use. At minimum, add a log or status message: 'Skill server token is stored as plaintext; use Rotate token to re-encrypt.'

_Verifier:_ The plaintext fallback in `TryDecryptExistingApiKey` is genuinely silent: `error` stays `string.Empty` so none of the three callers' `SetStatus` guards fire, and no other warning path exists for the unencrypted case — violating the CLAUDE.md "no silent fallbacks on security-relevant paths" rule.

### [CONFIRMED] security — `src/Netclaw.Cli/Tui/Wizard/Steps/SlackStepViewModel.cs:163`
**DM trust-audience derives from AllowedUserIds count but RestrictToSpecificUsers state is not the authoritative gate**

In `OnLeave()` at line 163, the DM audience is computed via `ChannelAudienceDefaults.ForDirectMessage(posture, ParseUserIds(AllowedUserIdsInput).Count)`. The count of parsed user IDs is used as the discriminant (`count == 1` → `Personal`, otherwise posture-based). If the user sets `RestrictToSpecificUsers = true` and enters 2+ user IDs, the DM audience becomes posture-based (potentially `Team` or `Public`) despite restriction being chosen — an implicit security escalation. The same pattern exists identically in `DiscordStepViewModel.cs:166` and `MattermostStepViewModel.cs:204`. The count-based discrimination was designed for the `allowedUserCount == 1` = personal-use case, but when a user explicitly picks "restrict to specific users" with 2+ IDs, `Team` or `Public` audience is inconsistent with their intent. This is a trust-level mismatch, not just a UI mismatch.

_Fix:_ When `RestrictToSpecificUsers = true`, force the DM audience to `Personal` regardless of user count, since the user has expressed an explicit restriction intent. The current `ForDirectMessage` signature conflates two orthogonal axes (posture and restriction intent) via a count heuristic. Either add a `bool restrict` overload or apply `TrustAudience.Personal` directly when `RestrictToSpecificUsers` is true.

_Verifier:_ The trust escalation (`Personal` → `Team`) is bounded to the explicitly allow-listed users (unauthenticated senders are still denied by `IsAllowedUser`), so this is a privilege escalation within the trusted set, not an open-access bypass — medium severity is correct.

## LOW (53)

### [CONFIRMED] concurrency — `src/Netclaw.Cli/Tui/Wizard/Steps/ProviderStepViewModel.cs:184`
**Fire-and-forget `RunProbeTimerAsync` task with no error surface**

`_ = RunProbeTimerAsync(ct)` is used at lines 184, 281, 293 (and also in `ModelManagerViewModel` line 347 and `ProviderManagerViewModel` lines 474, 491, 907). `RunProbeTimerAsync` loops on `Task.Delay(1000, ct)` and writes `ProbeElapsedSeconds.Value++`. If `ProbeElapsedSeconds` (a `ReactiveProperty<int>`) is disposed before the timer task observes cancellation — e.g. the user navigates away and the ViewModel is disposed before the CTS fires — the write to the disposed `ReactiveProperty` will throw `ObjectDisposedException`. Because the task is fire-and-forget, this exception is unobserved and silently terminates the timer. In R3, writing to a disposed `ReactiveProperty` throws immediately.

_Fix:_ Await `RunProbeTimerAsync` as part of the probe sequence (cancel and await it in `CancelProbe`/`finally`), or guard the write with a null/disposed check. At minimum, wrap the `ProbeElapsedSeconds.Value++` write in a try/catch for `ObjectDisposedException`.

_Verifier:_ The race window is very narrow (between `Task.Delay` completion and the next line), the result is a silently swallowed `ObjectDisposedException` in a fire-and-forget task rather than any data corruption or user-visible failure, making this low-severity despite the mechanism being real.

### [CONFIRMED] correctness — `src/Netclaw.Cli/Tui/Config/TelemetryAlertingConfigViewModel.cs:282`
**Webhook edit silently becomes a no-op when index is out of range at persist time**

SaveWebhookForm (line 280-307) captures `editing = _editingWebhookIndex` before calling PersistWebhooks. Inside PersistWebhooks the webhook list is reloaded from disk (line 350). If the file was externally modified between BeginEditWebhook and SaveWebhookForm — reducing the webhook count — then `editing is { } index && index < webhooks.Count` (line 282) evaluates false, producing `new WebhookTarget()`. But the guard at line 297 is `if (editing is null)`, which is false (editing = 0), so the new target is never appended. The entire edit is silently discarded: PersistWebhooks writes the unchanged webhook list, ReloadState reports success with message "Webhook X updated. Saved.", and the UI shows the result as saved — but the user's changes are gone. The same race applies to RemoveSelectedWebhook, though there the stale-index case correctly removes a different webhook rather than silently doing nothing.

_Fix:_ When `editing is { } index && index >= webhooks.Count`, treat this as an explicit error: set Status to an error message ("Webhook list changed unexpectedly; reload and retry."), return without calling ConfigFileHelper.WriteConfigFile, and set saved = false. Do not silently report success.

_Verifier:_ The race requires an external process to shorten the webhook list between BeginEditWebhook and SaveWebhookForm — an unlikely but real scenario; severity is medium in theory but low in practice for a local single-user CLI tool.

### [CONFIRMED] correctness — `src/Netclaw.Cli/Tui/ProviderManagerViewModel.cs:777`
**GoBack from Loading or List state routes to dashboard/exit instead of staying — user can accidentally quit during eager probe**

`GoBack()` handles `AddSelectType`, `AddName`, `AddSelectAuth`, `AddOAuthDeviceFlow`, `AddBrowserOAuthFlow`, `AddCredentials`, `AddValidating`, `AddComplete`, `Details`, `FixCredentials`, `RemoveConfirm`, and `RenameProvider` explicitly. `ProviderManagerState.Loading` and `ProviderManagerState.List` are not handled and fall through to the `default` branch (line 830), which in the embedded-config scenario immediately navigates to `/config` and in the standalone scenario calls `Shutdown()`. If the user presses Esc during the eager probe startup (state = `Loading`), or from the normal list (state = `List`), the outcome is correct for `List` (user wants to leave) but during `Loading` the eager `ProbeAllConfiguredAsync` task is still running with `CancellationToken.None` against each provider. The tasks are not cancelled and continue posting `NotifyStateChanged()` to a view model that may be disposed.

_Fix:_ Add an explicit `case ProviderManagerState.Loading:` that cancels the eager probe (set a CTS for `ProbeAllConfiguredAsync`) and then falls through to the existing dashboard/exit logic. The existing `CancelProbe()` only covers `_probeCts`, not `EagerProbeCompletion`.

_Verifier:_ The `Loading`→`default` routing during eager probe is confirmed, and the tasks genuinely continue posting to a potentially-disposed view model, but the observable impact is a background-task orphan with post-disposal write attempts rather than a security or data-loss bug — real but low urgency.

### [CONFIRMED] design — `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs:22`
**God-object: `ChannelsConfigViewModel` owns parsing, validation, probing, persistence, rendering state, and a full embedded channel picker — 2,283 lines**

The viewmodel owns: (1) the entire `ChannelPickerStepViewModel` sub-wizard (with its own sub-steps, validation, and adapter sub-VMs); (2) `ChannelsConfigPersistenceMapper` (nested class, 500+ lines doing config load/build); (3) `_channelAudiences` state duplicated beside the `Step` sub-VM's own channel-list state; (4) `ChannelResolveOutcome`, `ChannelAccessValidation`, `ChannelAccessOutcome` private records; (5) static helpers (`IsSlackChannelId`, `Clamp`, `Wrap`, `Pluralize`, `Normalize`, `NormalizeChannelId`); (6) background label-resolution lifecycle; (7) screen-machine state (`_activeAdapterType`, `_managementMenuIndex`, eight screen enum values, five credential staging fields). The `ChannelsConfigPersistenceMapper` and its draft types (`ChannelsConfigDraft`, `SlackChannelDraft`, etc.) are defined at the bottom of the same file. Split responsibility: the mapper+drafts belong in their own file; validation types belong in their own file; screen-routing logic belongs in a coordinator.

_Fix:_ Extract `ChannelsConfigPersistenceMapper` and the draft/record types to a separate file. Extract the multi-screen state machine (ManageChannels, AddChannel, AllowedUsers, DirectMessages, RotateCredentials, ResetConfirm navigation) into a `ChannelsManagementCoordinator`. Separate the channel-probing logic (`ValidateChannelAccessAsync`, `ResolveSingleChannelAsync`, label refresh) into a `ChannelProbeService`. This brings the viewmodel down to a thin coordinator that wires the pieces together.

_Verifier:_ The god-object characterization is accurate — the file genuinely owns parsing, persistence, probing, screen routing, validation types, and draft types in one 2,282-line file — but this is a pure maintainability/cohesion concern with no correctness or security impact, which makes "medium" an overstatement; "low" is appropriate for a design smell that carries no runtime risk.

### [CONFIRMED] design — `src/Netclaw.Cli/Tui/Config/SecurityAccessViewModel.cs:135`
**Repeated config-file reload per property access causes TOCTOU and performance issues**

`CurrentPosture` (line 135) re-reads and deserializes `netclaw-config.json` from disk on every access (`ReadPosture(ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath))`). This property is called in `BuildItems()` (which is a computed property called from the page on every layout invalidation), `ApplySelectedPosture()`, `IsSystemDefaultAudience()`, `AudienceHasOverrides()`, `ResetSelectedAudienceProfile()`, and `SavePosture()`. Multiple calls within a single user interaction (e.g., `ApplySelectedCascadeOption` at line 269 checks `_pendingPosture`, then `SavePosture` calls `CurrentPosture` twice) may see different values if the config file is modified externally between calls — a TOCTOU condition. On SSD this is also slow: a layout redraw calls `ConfigFileHelper.LoadJsonDict` multiple times per frame.

_Fix:_ Cache the loaded config in a private field, invalidated only after a save operation. Read once at the start of each operation and pass it through, rather than re-reading per property access.

_Verifier:_ The TOCTOU risk is theoretical in this single-user local TUI (no realistic concurrent writer), so the real impact is repeated synchronous disk I/O per render frame rather than a security or data-corruption hazard; severity should be low (performance/design smell), not medium.

### [CONFIRMED] design — `src/Netclaw.Cli/Tui/Sections/SectionEditorInfrastructure.cs:169`
**AddSectionEditor<T> registers duplicate SectionEditorRegistry descriptors on every call**

Each call to `AddSectionEditor<T>` unconditionally calls `services.AddSingleton<SectionEditorRegistry>()`. With 3 calls (config path) or 4 calls (init path), 3–4 `ServiceDescriptor` entries are accumulated for the same type. `GetRequiredService<SectionEditorRegistry>()` resolves to the last descriptor (MS DI convention), which instantiates one registry with all `SectionEditorRegistration` entries in scope — correct today. But the N-1 dead descriptors make `GetServices<SectionEditorRegistry>()` return N instances (each receiving all registrations), meaning any consumer that iterates the open-generic enumerable gets N duplicated registries with overlapping duplicate-ID collisions and N editor lifecycles. A future audit test or framework introspection will trigger the `InvalidOperationException` guard in the constructor on the second instance.

_Fix:_ Replace `services.AddSingleton<SectionEditorRegistry>()` with `services.TryAddSingleton<SectionEditorRegistry>()` (requires `using Microsoft.Extensions.DependencyInjection.Extensions`). This ensures exactly one descriptor is registered regardless of how many times `AddSectionEditor` is called, while still receiving all accumulated `SectionEditorRegistration` entries because resolution is lazy.

_Verifier:_ The defect is structurally real but the blast radius is limited to the latent path — no current code calls `GetServices<SectionEditorRegistry>()`, so the `InvalidOperationException` cannot fire today; severity is lower than medium because the trigger requires a future code change, not a runtime condition or existing code path.

### [CONFIRMED] error-handling — `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs:160`
**ValidateChannelAccessAsync runs all three adapter probes sequentially even when an earlier probe blocks save**

ValidateChannelAccessAsync at line 866 awaits ValidateSlackChannelsAsync, then ValidateDiscordChannelsAsync, then ValidateMattermostChannelsAsync in sequence. Each probe can involve a network round-trip. If the Slack probe returns a blocking issue (line 936), the function still performs two additional network-bound probes unnecessarily. More importantly, if a Slack probe call throws an unhandled exception after the ct check (e.g., a malformed probe result), the function does not short-circuit: the Discord and Mattermost probes still run, and the Slack error may be buried in the result list. The structurally parallel probes are never run in parallel (Task.WhenAll), so a UI that sets Status.Value to 'Validating...' during sequential multi-second probes will appear unresponsive.

_Fix:_ Short-circuit after the first blocking issue: if `slack.BlockingIssue is not null`, skip the remaining probes and return immediately. For the non-blocking (unresolved-only) path, consider Task.WhenAll for all three probes to reduce wall-clock time.

_Verifier:_ The bug is real but the practical impact is low: in the common case only one adapter is enabled (making the others return None immediately), and even in multi-adapter setups the wasted probes merely add latency without corrupting data or silently suppressing errors; the exception sub-claim is wrong (an exception aborts the method immediately).

### [CONFIRMED] error-handling — `src/Netclaw.Cli/Tui/ProviderManagerViewModel.cs:267`
**Silent exception swallow in `ProbeAllConfiguredAsync` concurrent probe tasks**

The `probeTasks` lambda at line 267 wraps the probe call in a bare `catch { item.Health = ProviderHealthStatus.Unhealthy; }` with no exception type filter and no logging. This catches `OperationCanceledException`, `OutOfMemoryException`, and every other exception class. Because the tasks are composed with `Task.WhenAll`, an `OutOfMemoryException` or similar fatal exception would be silently swallowed and the item would simply display as 'Unhealthy' with no stack trace, error message, or diagnostic. The outer `EagerProbeCompletion` task is stored but never awaited or observed for exceptions either (see `OnActivated`, line 188).

_Fix:_ Change `catch` to `catch (Exception ex)` and log `ex` to `ProbeDiagnosticsLog` or at minimum capture the exception in `ProbeResult.ErrorMessage`. At minimum re-throw `OutOfMemoryException` and `StackOverflowException` (via `ExceptionDispatchInfo`).

_Verifier:_ The bare catch is a real diagnostic gap — exceptions produce no log or error message — but fatal exceptions like OOM/SOE would crash the process before reaching the catch anyway, so the actual impact is missing diagnostic context on network/probe errors rather than hidden fatal crashes; severity is low rather than medium.

### [CONFIRMED] resource-leak — `src/Netclaw.Cli/Tui/Config/SearchConfigEditorViewModel.cs:496`
**HttpClient created with `new HttpClient()` is never disposed when factory is absent**

CreateHttpClient() (line 496-497) returns `_httpClientFactory?.CreateClient(string.Empty) ?? new HttpClient()`. When _httpClientFactory is null (the default in tests and when injected as null), each call to ProbeAsync creates a fresh HttpClient and passes it to BraveSearchBackend, SearXngBackend, or DuckDuckGoBackend (lines 472-479). None of those backend types implement IDisposable, so the HttpClient they hold is never disposed. Each "test" or "retry" press leaks an HttpClient and its underlying socket. While the production path injects an IHttpClientFactory (safe), the constructor default is null, making the leak the common path in unit tests and in any deployment that skips factory injection.

_Fix:_ Either (a) require IHttpClientFactory — remove the nullable and make it a hard dependency so the factory path is always used; or (b) track the created HttpClient in a field, dispose it in SearchConfigEditorViewModel.Dispose(), and never create a new one mid-flight. Option (a) is safer and matches the rest of the codebase pattern.

_Verifier:_ The leak is real but confined to the null-factory path, which is not exercised in production (factory is always DI-injected there) and not triggered by current tests that omit the factory; severity is lower than medium because the production path is safe, but the null default is a latent trap.

### [CONFIRMED] resource-leak — `src/Netclaw.Cli/Tui/Config/SkillSourcesConfigPage.cs:140`
**_contentSubscriptions is not registered with the page-level Subscriptions disposable**

`_contentSubscriptions` (line 20) is a standalone `CompositeDisposable` that is only ever `Clear()`-ed inside the `DynamicLayoutNode` rebuild callback (line 74). It is never added to the page-level `Subscriptions` via `DisposeWith`. When the page is torn down, `Subscriptions.Dispose()` is called, but any live subscription in `_contentSubscriptions` — specifically the `SelectionConfirmed` subscription added at line 140 for the validation dialog list — is not disposed. If a validation dialog is open at the moment navigation away from the page occurs, the `SelectionConfirmed` subscription on the `SelectionListNode` remains live, keeping a closure over `HandleValidationDialogAction` (and thus the page and viewmodel) reachable until the node is GC'd. `_pickerSubscriptions` is correctly registered at line 35.

_Fix:_ Add `_contentSubscriptions.DisposeWith(Subscriptions);` in `OnBound()` alongside the existing `_pickerSubscriptions.DisposeWith(Subscriptions)` at line 35. Change the in-callback `_contentSubscriptions.Clear()` to `_contentSubscriptions.Dispose(); _contentSubscriptions = new CompositeDisposable();` — or use a fresh local per rebuild — so the field remains valid after Dispose.

_Verifier:_ The leak is real but practically narrow: it only bites when the user navigates away while a validation dialog is open (a rare transient state), and the retained objects are a single closure and a UI node that will be GC'd once no other live reference holds them — making this a temporary retention rather than a permanent leak, which lowers severity from medium to low.

### [CONFIRMED] security — `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs:1136`
**GetEffectiveSecret reads secrets.json from disk on every probe call with no caching**

GetEffectiveSecret (line 1136-1146) calls ConfigFileHelper.ReadDecryptedSecret when the draft value is blank and hasPersistedSecret is true. ReadDecryptedSecret loads and deserializes secrets.json on each call, then decrypts the stored value (line 246-252). During ValidateChannelAccessAsync, this can be called up to 5 times per save (Slack bot, Discord bot, Mattermost bot, and again during label refresh). Each call reads the secrets file and runs the decryption primitive. While this is not a leak in the classic sense, repeatedly decrypting and loading the plaintext token into short-lived locals without pinning or zeroing the memory extends the window the cleartext token is accessible in GC-managed heap memory. Additionally, if the file is read between a write by another process, partially-written secrets may be deserialized silently.

_Fix:_ Cache the decrypted token for the lifetime of the save/validate operation (pass it as a parameter into the probe methods rather than re-reading secrets each call). Use SecureString or at minimum zero the string after use where the platform permits. This is defense-in-depth given the token is already in managed memory elsewhere.

_Verifier:_ The redundant reads/decryptions are real and confirmed, but this is a performance/code-quality defect rather than a security vulnerability: the tokens are already resident in managed memory throughout the TUI session, there is no attacker-controlled race window, and `SecureString` is deprecated by Microsoft for managed code; the appropriate fix is caching the secret within the save/validate operation, not memory pinning.

### [CONFIRMED] security — `src/Netclaw.Cli/Tui/Wizard/Steps/ExposureModeStepViewModel.cs:326`
**EnsureCurrentClientPaired reads devices.json twice on the orphaned-token path, enabling a window for a stale device list**

`EnsureCurrentClientPaired` calls `DeviceRegistryInspector.Read(paths)` at line 325, which internally reads `devices.json` to produce the snapshot including `LocalTokenMatchesDevice`. Then, when the token is present but not matched (`HasLocalDeviceToken && !LocalTokenMatchesDevice`), the code re-reads `devices.json` at line 353 via `ReadPairedDevices`. Between these two reads, another process could update `devices.json` — for example, the daemon accepting a new pair request. The second read would then produce a different device list than the snapshot used for the matching decision. The new device entry would be appended at line 354 alongside any devices added by the external write, which is safe in isolation. However, if the external write has already paired the local token (fixed the orphan), the guard at line 326 won't catch it on the second read, and a duplicate device entry is written for the same underlying token.

_Fix:_ Read `devices.json` exactly once in `EnsureCurrentClientPaired`, pass the device list to a helper that performs both the token-match check and the append operation, then write once. This eliminates the TOCTOU and the deduplication gap.

_Verifier:_ The TOCTOU window is real and the duplicate-entry outcome is confirmed by code, but the practical trigger requires a concurrent daemon write to devices.json during the wizard save path — an unlikely race in normal single-user self-hosted use — and the impact is data redundancy (two valid entries for one token), not an authentication bypass or privilege escalation, so medium severity overstates the risk.

### [PLAUSIBLE] concurrency — `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs:1633`
**Fire-and-forget `_ = RefreshChannelLabelsAsync(...)` with unobserved exceptions**

`StartChannelLabelResolution` at line 1625 launches `RefreshChannelLabelsAsync` as a discarded task (`_ = RefreshChannelLabelsAsync(type, _labelResolutionCts.Token)`). The method itself catches `OperationCanceledException` and generic `Exception` and routes them to `Status.Value`, which is correct. However, if the method itself throws before those handlers (e.g., a `NullReferenceException` in `NotifyContentChanged()` or a framework assertion), the exception is silently swallowed by the discard. Additionally, the CTS replacement pattern (`_labelResolutionCts?.Cancel(); _labelResolutionCts?.Dispose(); _labelResolutionCts = new CancellationTokenSource()`) at lines 1630-1632 has a race: if the background task reads `_labelResolutionCts.Token` concurrently after `Cancel()` but before `Dispose()`, an `ObjectDisposedException` can emerge from the token. Should use `CancelAsync()` + defer dispose after the new CTS is created, or use a local captured reference before disposal.

_Fix:_ Capture the old CTS in a local before assigning the new one, cancel it, then dispose it after the new token is captured. Use `#pragma warning disable CS4014` + `_ =` only after wrapping with `.ContinueWith(t => { if (t.IsFaulted) logger.Error(t.Exception); })`. Alternatively, store the Task and await it on disposal.

_Verifier:_ The fire-and-forget discard is real but the blast radius is narrow: the pre-try guard code at lines 294-299 would need to throw for an exception to escape the catch, and the CTS dispose race is theoretical given single-threaded TUI dispatch; downgrade from medium to low.

### [PLAUSIBLE] correctness — `src/Netclaw.Cli/Tui/Config/SecurityAccessViewModel.cs:246`
**ApplySelectedPosture reads CurrentPosture twice with a TOCTOU window around the 'already active' guard**

`ApplySelectedPosture` (line 246) reads `CurrentPosture` at line 249 to check whether the new selection matches the current value. `CurrentPosture` is a property that reads `netclaw.json` from disk on every call (line 135). If another process (e.g., a daemon restart, `netclaw doctor --fix`) writes the config between the `OpenPostureEditor` call (line 238, which reads `CurrentPosture` for the initial selection) and the `ApplySelectedPosture` call (line 249), the 'already active' message could fire when the selection actually differs from the on-disk value, or — more critically — the wrong posture could pass the `posture == CurrentPosture` guard and proceed to `SavePosture`. This is a TOCTOU race on a security-critical config value.

_Fix:_ Snapshot the config at the start of `OpenPostureEditor` and reuse the snapshot throughout the posture selection flow, rather than re-reading from disk at each step. Alternatively, pass the loaded config dictionary into `ApplySelectedPosture` as a parameter to make the read explicit and singular.

_Verifier:_ The TOCTOU mechanism is real but the finding overstates the security impact — the guard at line 249 is a UX shortcut, not a security gate, and SavePosture always writes the user-selected value, so a stale CurrentPosture read cannot cause a wrong posture to be persisted.

### [PLAUSIBLE] correctness — `src/Netclaw.Cli/Tui/ConfigDashboardPage.cs:71`
**rows.IndexOf lookup can match the wrong item when two dashboard rows share a prefix after status formatting**

`BuildList` constructs `rows` by formatting each `ConfigDashboardItem` as `"{item.Label,-22}  {status}"` when the item has a non-empty status. The `SelectionConfirmed` subscriber then does `rows.IndexOf(selected[0])` (line 71) and the `Invalidated` subscriber does `rows.IndexOf(highlighted.Value)` (line 86) to map back to the item. `List<string>.IndexOf` uses `string.Equals` (ordinal by default), so this is a linear scan for exact string equality. Because the formatted string includes both the padded label AND the live status text, two items with different labels could only collide if their formatted strings are identical — which is unlikely in practice. However, if the status text is empty for multiple items (e.g. two terminal-row items both format as their bare label), the first match is always returned. Currently both "Run Full Doctor" and "Quit" have empty status and different labels so no collision exists today, but this is fragile: adding a new terminal item that starts with the same label prefix as an existing non-terminal item with exactly 22 chars of label padding will silently select the wrong item.

_Fix:_ Map selections by index rather than by string value. Either use `_entryList.HighlightedIndex` if the API exposes it, or maintain a parallel `List<ConfigDashboardItem>` alongside `rows` so the callback uses index directly from `rows` and looks up `ViewModel.Items[index]` without a secondary search.

_Verifier:_ No collision exists in the current item set — all labels are distinct and the status reader returns semantically unique strings — so this is a latent fragility rather than an active bug, warranting low rather than medium severity.

### [PLAUSIBLE] correctness — `src/Netclaw.Cli/Tui/Sections/ConfigEditorStateStore.cs:38`
**SectionEditorStateAction.Set with null Value is silently accepted, persists JSON null, and returns a confusing type on readback**

`SectionEditorStateAction` is a positional record with `object? Value = null` and no constructor guard. When `Action == Set` and `Value == null`, line 38 executes `section[action.Key] = action.Value!` (null-forgiving suppresses the compiler warning) storing `null` into the in-memory dictionary. `WriteState` then serializes it as `"key": null` in JSON. On the next `LoadState`, the entry deserializes as a `JsonElement` with `ValueKind == JsonValueKind.Null`. `NormalizeValue` has no arm for `JsonValueKind.Null`, so it falls through to `_ => value`, returning the boxed `JsonElement` (not `null`). A caller that checks `value is null` after `TryGetValue` returns `true` will see `false` and proceed with a `JsonElement` object instead of the expected `null`. Contrast with `SectionSecretAction`, which throws `ArgumentNullException` when `Action == Set && value == null`.

_Fix:_ Add the same guard to `SectionEditorStateAction` — either use a validating constructor (like `SectionSecretAction`), or add an arm `JsonElement element when element.ValueKind == JsonValueKind.Null => null` to `NormalizeValue`. Also add `JsonElement element when element.ValueKind == JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText())` to handle nested object state values correctly.

_Verifier:_ The defect is real but dormant — no current production caller triggers Set with a null value; the severity is low (not medium) because exploiting it requires a new caller that violates the existing factory pattern.

### [PLAUSIBLE] correctness — `src/Netclaw.Cli/Tui/Sections/ConfigEditorStateStore.cs:79`
**NormalizeValue has no arm for JsonValueKind.Null or JsonValueKind.Object — returns raw JsonElement**

When state written as a nested object (e.g. an array-of-objects or a sub-dict) is read back and the value's `ValueKind` is `Object` or `Null`, `NormalizeValue` falls through to `_ => value` and returns the `JsonElement` as-is. A caller expecting `Dictionary<string, object>` (for an object) or `null` (for null) receives a `JsonElement` instead. The `ConfigFileHelper.NormalizeNodeValue` at `ConfigFileHelper.cs:272` already handles both cases correctly and could be reused or consulted as the pattern.

_Fix:_ Add two missing arms to the switch: `JsonElement element when element.ValueKind == JsonValueKind.Null => null` and `JsonElement element when element.ValueKind == JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText())`. Align with `ConfigFileHelper.NormalizeNodeValue` to avoid the same drift in the future.

_Verifier:_ The missing switch arms are real but the current production call sites (`TryReadHost` via `?.ToString()` and `ReadTrustedProxies` via `_ => []`) both accidentally tolerate a raw `JsonElement` fallback, so there is no observable misbehavior today; severity is lower than rated because impact is limited to future callers that store objects or nulls.

### [PLAUSIBLE] design — `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs:33`
**Duplicated channel state between `_channelAudiences` and `ChannelPickerStepViewModel` sub-VM fields can desync**

`_channelAudiences` (line 33, `Dictionary<ChannelType, Dictionary<string, TrustAudience>>`) stores audience assignments in the viewmodel. The canonical channel IDs are stored separately in the sub-VMs accessed via `GetChannelIds` (which calls `ChannelCsv.ParseCsv(Step.GetAdapterViewModel<...>().ChannelNamesInput, ...)`). This means channel IDs live in the wizard sub-VM string fields, and audiences live in the parent VM dictionary — two different representations of one logical entity. `SetChannelIds` mutates the sub-VM fields; `SetChannelAudience` mutates `_channelAudiences`. When the Slack name-normalization path (`NormalizeSlackChannelNamesToIds`) rewrites a channel name to its ID, it must update both: `SetChannelIds` and `RemapChannelAudiences`. If any path forgets the remap (or if a future path adds a channel without adding a default audience), the audience map silently diverges from the ID list, causing a channel to silently fall through to the posture default. After `SaveAsync` the state is reloaded and re-synced via `LoadAudienceDrafts`, but between edits within a save boundary the two are loosely coupled.

_Fix:_ Introduce a per-adapter `ChannelEntry` list (ID + audience) as the single mutable model, replacing both the sub-VM string fields and `_channelAudiences`. The `ChannelsConfigPersistenceMapper` serializes and deserializes to/from this list. Audience assignment and ID list operations then operate on one collection, eliminating the remap step.

_Verifier:_ The split is real and the remap/fallback mechanism is correctly described, but the actual risk is latent — all current paths maintain the invariant, the consequence of desync is falling back to the posture default (not an open channel), and `SaveAsync` re-syncs state from disk after every save, limiting the blast radius to the in-session window.

### [PLAUSIBLE] resource-leak — `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs:1633`
**Fire-and-forget `_ = RefreshChannelLabelsAsync(...)` drops exceptions and its result**

`StartChannelLabelResolution` at line 1625–1634 creates a new `CancellationTokenSource`, then launches `_ = RefreshChannelLabelsAsync(type, _labelResolutionCts.Token)`. Because the `Task` is discarded, any unhandled exception thrown by the async method (e.g. from internal logic after the `catch (OperationCanceledException)` and `catch (Exception ex)` blocks in `RefreshChannelLabelsAsync`) will be an unobserved task exception. More critically, the method mutates `slack.LastChannelResolution`, writes to `Status`, and calls `NotifyContentChanged()` — all of which interact with the TUI thread. If the `ViewModel` is disposed before the background task completes, `_labelResolutionCts` is cancelled and disposed (line 1269–1271 of `Dispose`), but the async continuation may still execute a frame later and access the disposed `Status` `ReactiveProperty`.

_Fix:_ Store the task and observe it (e.g., assign to a tracked field and await in a try/catch), or ensure the ViewModel does not touch disposed reactive properties by capturing a cancellation guard before the `await` continuation executes.

_Verifier:_ The primary claim of unobserved exceptions from async logic is refuted by the blanket catch; the real (but narrow) risk is a secondary ObjectDisposedException thrown from within that catch's Status.Value assignment after Dispose races past the CTS cancellation guard, which is only confirmable by inspecting Termina.Reactive's ReactiveProperty dispose behavior.

### [UNVERIFIED] concurrency — `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs:1625`
**Label-resolution CTS replace-cancel-dispose is not atomic; concurrent calls can double-dispose**

`StartChannelLabelResolution` (line 1625) does: `_labelResolutionCts?.Cancel(); _labelResolutionCts?.Dispose(); _labelResolutionCts = new CancellationTokenSource(); _ = RefreshChannelLabelsAsync(...)`. If the UI receives two rapid adapter-open events on the same thread this sequence is safe (single-threaded). However, the identical pattern is in `Dispose()` (lines 1269-1270). If `Dispose()` races with a late-arriving UI callback that also calls `StartChannelLabelResolution`, the CTS can be disposed twice. The pattern is also fragile because the fire-and-forget task captures `_labelResolutionCts.Token` *before* the field is potentially replaced by a subsequent call; the cancellation check at `ct.IsCancellationRequested` inside `RefreshChannelLabelsAsync` correctly uses the captured token, but exceptions after `_labelResolutionCts` is replaced-and-disposed raise against an already-disposed CTS.

_Fix:_ Adopt the local-capture pattern: `var cts = _labelResolutionCts = new CancellationTokenSource(); _ = RefreshChannelLabelsAsync(type, cts.Token);` and in Dispose, `var old = Interlocked.Exchange(ref _labelResolutionCts, null); old?.Cancel(); old?.Dispose();`.

### [UNVERIFIED] concurrency — `src/Netclaw.Cli/Tui/ProviderManagerViewModel.cs:188`
**EagerProbeCompletion fire-and-forget task from ProbeAllConfiguredAsync uses CancellationToken.None for all concurrent provider probes**

`OnActivated` calls `EagerProbeCompletion = ProbeAllConfiguredAsync()` as a fire-and-forget (line 188). Inside `ProbeAllConfiguredAsync`, each provider probe is launched as `await _probe.ProbeAsync(item.Entry, CancellationToken.None)` (line 272). `Dispose()` calls `CancelProbe()` (line 1096) which only cancels `_probeCts` (the single-provider probe CTS) — it has no effect on the eager concurrent probes. If the view model is disposed while N concurrent `ProbeAsync` calls are in flight (each with `CancellationToken.None`), they all continue running and then call `item.Health = ...` and `NotifyStateChanged()` on the disposed object, incrementing a disposed `ReactiveProperty<int>` and triggering `RequestRedraw()` on a detached view model. In practice the probes are short HTTP requests, but for a slow or unreachable self-hosted provider this can be a multi-minute leak.

_Fix:_ Create a dedicated `CancellationTokenSource _eagerProbeCts` in `OnActivated`, pass its token to each `ProbeAsync` call in `ProbeAllConfiguredAsync`, and cancel+dispose it in `Dispose()`. Alternatively, add a `_disposed` volatile bool flag and check it before calling `NotifyStateChanged()` in the probe continuations.

### [UNVERIFIED] correctness — `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs:2110`
**ReadConfiguredChannels loads DefaultChannelName with '#' prefix that is not deduplicated against AllowedChannelIds**

ReadConfiguredChannels at line 2122-2123 loads the legacy Slack.DefaultChannelName field and normalizes it by prepending '#' if missing. The AllowedChannelIds array is loaded at line 2113 without any '#' prefix. Distinct(StringComparer.Ordinal) at line 2128 is case- and character-sensitive, so 'general' (from AllowedChannelIds) and '#general' (from DefaultChannelName) are considered different and both appear in the result. When ApplyToStep stores this list as 'general, #general' in vm.ChannelNamesInput, and GetChannelIds re-parses with trimHash:true, both become 'general' and deduplicate to one entry. The editor renders only one row, but the intermediate vm.ChannelNamesInput state contains the redundant '#general' entry. This is benign today because every downstream read calls trimHash, but it means the raw ChannelNamesInput string carries a spurious '#general' until the next save.

_Fix:_ In ReadConfiguredChannels, normalize the defaultChannelName entry by stripping '#' before adding it (matching the trimHash behavior downstream), so Distinct(Ordinal) deduplicates correctly: `channels.Add(defaultChannelName.TrimStart('#'));`.

### [UNVERIFIED] correctness — `src/Netclaw.Cli/Tui/Config/ExposureModeConfigPage.cs:129`
**Enter key in non-saved state passes through to the StepView even when GoNext is the intended action for Enter**

`HandleKeyPress` (line 120) handles Escape explicitly, then checks `IsSaved.Value && Enter` (line 129). When `IsSaved` is false and Enter is pressed, control falls through to `ViewModel.StepView.HandleKeyPress(key)` (line 135). The step view's sub-step inputs (text inputs and selection lists) already handle Enter to advance the sub-step via `callbacks.AdvanceStep`. This dual-path is intentional and works for sub-step inputs. However, the top-level `GoNext` call at line 43–51 in the ViewModel (which checks validation and writes config) is ONLY reached via `StepViewCallbacks.AdvanceStep` when the step view is not in the `IsSaved` state. The mode-selection list's `confirmed` callback (line 93 of `ExposureModeStepView.cs`) calls `callbacks.AdvanceStep()` which is wired to `ViewModel.GoNext`. This means the Enter key at the mode-selection sub-step correctly reaches the ViewModel's `GoNext` through the step view, but validation is checked inside `GoNext` only after `_orchestrator.GoNext()` returns false (lines 51–64). If a future sub-step type fails to wire `AdvanceStep`, Enter would be silently swallowed.

_Fix:_ This is low risk with the current step implementations but the layered wiring (page → step view → callback → GoNext) makes the control flow non-obvious. Adding a comment in `HandleKeyPress` that explains why Enter in non-saved state is forwarded to the step view (rather than directly to GoNext) would reduce maintenance risk.

### [UNVERIFIED] correctness — `src/Netclaw.Cli/Tui/Config/SearchConfigEditorViewModel.cs:527`
**ParseBackend silently falls back to DuckDuckGo for unrecognized backend string**

The private ParseBackend (line 527-533) uses a default arm `_ => SearchBackend.DuckDuckGo`. If a user has configured a valid-but-future backend string (e.g., a typo or a backend added in a newer schema), CommitField('Search.Backend', 'typo') silently resets their backend to DuckDuckGo, saves it on the next Enter, and they get no error. The same pattern applies in SearchEditorPersistenceMapper.ParseBackend (line 130-136). CLAUDE.md prohibits silent fallbacks: "When something fails or is misconfigured, fail loudly."

_Fix:_ Return null from ParseBackend for unrecognized values (make it return SearchBackend?) and propagate a validation error when null is returned, or throw InvalidOperationException with the unrecognized value to surface it immediately. The persistence mapper's ParseBackend should default gracefully (DuckDuckGo is a safe config read default) but the UI path that accepts user input should reject unrecognized strings.

### [UNVERIFIED] correctness — `src/Netclaw.Cli/Tui/Config/SecurityAccessViewModel.cs:182`
**`Activate` dispatches on string label equality — fragile coupling between menu and action routing**

The security access menu dispatch at lines 183-196 uses `switch (item.Label)` with literal strings `"Security Posture"`, `"Enabled Features"`, `"Audience Profiles"` to route to editors. `BuildItems()` produces these labels and must stay in exact sync with the switch. If a label is changed for localisation or UX reasons, the routing silently falls through to the fallback `Navigate?.Invoke(item.Route)` which is `null` for those items, causing a no-op instead of navigation. Additionally, a typo in either location produces the same silent fallback.

_Fix:_ Replace the string-comparison dispatch with a typed discriminator: introduce a `SecurityAccessAction` enum on `SecurityAccessItem` (or use the existing `Route` field plus null to distinguish navigate-vs-in-place items), and switch on the enum. The `BuildItems()` and `Activate` methods are then refactored to be in lockstep without relying on string equality.

### [UNVERIFIED] correctness — `src/Netclaw.Cli/Tui/Config/SkillSourcesConfigViewModel.cs:1541`
**Save-anyway fingerprint for URL change uses API key length, allowing same-length new key to skip re-probe**

In `SaveRemoteUrlChange` (line 1541), the save-anyway fingerprint is constructed as `$"change-url|{source.Name}|{normalizedUrl}|{apiKey?.Length ?? 0}"`. Similarly, `SaveRotatedRemoteToken` at line 1590 uses `$"rotate-token|{source.Name}|{feed.Url}|{token.Length}"`. A user who enters a bad token of length N, sees a probe failure ('Press Enter again to save anyway'), then edits the token to a different value of the same length N (which calls `MarkDirty()` clearing the fingerprint) and re-enters — the fingerprint is cleared by `MarkDirty`, so the probe re-fires correctly. However, if the user presses Enter twice in rapid succession without editing (same Draft.Value), the second Enter in `SaveRotatedRemoteToken` matches the fingerprint and bypasses the probe, saving the unverified token. This is the documented 'save anyway' escape hatch, but the intent of two presses is to override a known-failing probe, not to silently skip the probe on repeated submission of the same value.

_Fix:_ This is the documented escape-hatch behavior. Add a comment at the fingerprint construction sites to make clear that same-length token repetition is intentionally treated as 'save anyway'. If true bypass-prevention is needed, include the Draft value's hash (not the raw token) in the fingerprint rather than just its length.

### [UNVERIFIED] correctness — `src/Netclaw.Cli/Tui/Config/TelemetryAlertingConfigViewModel.cs:325`
**Save() rebuilds the Telemetry section from scratch, discarding any unknown fields in the original**

Save() (line 313-339) replaces `root["Telemetry"]` with a hard-coded dictionary containing only Enabled and Otlp.Endpoint (line 327-334). Any additional fields a future version of TelemetryOptions might write — or fields that a user manually added for experimentation — are silently erased on the next save. Currently TelemetryOptions only has these two fields so this is not a live bug, but it creates a maintenance trap: adding a field to TelemetryOptions without updating this writer will silently wipe user values on the first TUI save. The PersistWebhooks path (line 352) correctly uses ConfigFileHelper.LoadSection to preserve unmanaged delivery-policy fields, so there is precedent for the safe pattern.

_Fix:_ Instead of constructing a fresh dictionary, load the existing Telemetry section as a Dictionary<string,object> (using LoadRawSection which already exists at line 505), mutate only Enabled and Otlp.Endpoint, then write it back. This is consistent with how PersistWebhooks handles the Notifications section.

### [UNVERIFIED] correctness — `src/Netclaw.Cli/Tui/Config/TelemetryAlertingConfigViewModel.cs:235`
**RemoveSelectedWebhook clamped row may land on AddRowIndex, a non-webhook row, after deleting the last webhook**

After PersistWebhooks succeeds in RemoveSelectedWebhook (line 234-235), ReloadState is called which sets Webhooks.Value to the shorter list. ListRowCount is then AddRowIndex (OtlpRowCount + 0) + 1 = 3. Math.Clamp(SelectedRow.Value, 0, 2) when SelectedRow was 2 (the only webhook at rowIndex 2) produces 2, which equals the new AddRowIndex (the "+ Add webhook" row). This is acceptable UX — focus lands on Add — but if the next key press is Delete (which calls RemoveSelectedWebhook), IsWebhookRow(AddRowIndex) returns false and nothing happens. The real issue is that no visible feedback is given that focus moved from a webhook row to the Add row; the status bar is set to the success message which overrides any navigation hint.

_Fix:_ After deletion, explicitly clamp to min(SelectedRow - 1, AddRowIndex - 1) (i.e., the previous webhook, or OtlpRowCount if the list is now empty) to ensure focus lands on a webhook or an OTLP row rather than the Add row. This gives a more predictable post-delete position.

### [UNVERIFIED] correctness — `src/Netclaw.Cli/Tui/ProviderManagerViewModel.cs:816`
**GoBack from AddComplete calls ConfirmAdd which writes provider config a second time**

`GoBack()` has `case ProviderManagerState.AddComplete: ConfirmAdd(); break;` (line 816–818). `ConfirmAdd()` (line 577) checks `!_newProviderPersisted` before calling `WriteProviderConfig()` to avoid a double write. However, when the user reaches `AddComplete` via the normal probe-success path, `WriteProviderConfig()` was already called inside `ProbeProviderAsync` (line 969) and `_newProviderPersisted` was set to `true`. So the guard correctly suppresses the second write. The subtlety is that `ConfirmAdd` also calls `ClearAddState()` which sets `_newProviderPersisted = false` (line 1075). If a future code path reaches `AddComplete` without going through the probe success branch (e.g., a test seam or a state jump), the guard would not fire and `WriteProviderConfig` would be called with stale `NewApiKey`/`NewProviderType`. This is a latent correctness hazard rather than a current bug given the existing state machine, but the semantics of Esc-from-AddComplete ("confirm and go back") are non-intuitive.

_Fix:_ Document clearly in a comment on the `AddComplete` case that Esc from the success screen is treated as an implicit confirm. Consider changing the key hint on the AddComplete screen from Esc to Enter-to-confirm-and-return to make the UX intent explicit and avoid the unusual `GoBack = ConfirmAdd` semantic.

### [UNVERIFIED] correctness — `src/Netclaw.Cli/Tui/Wizard/Steps/DiscordStepViewModel.cs:244`
**Health check reuses cached LastChannelResolution from background prefetch without verifying channel IDs match current input**

At line 244, `if (LastChannelResolution is { Success: true })` uses the cached background-resolution result directly without checking whether `ChannelIdsInput` has changed since the result was produced. `StartBackgroundChannelResolution()` is triggered in `TryAdvance` at sub-step 2, capturing `ChannelIdsInput` at that moment. If the user navigates back, edits `ChannelIdsInput` at sub-step 2 (triggering a new background task), and then advances to health check before the new task completes, `LastChannelResolution` may still hold the old result from the previous channel set. The early return on line 249 then reports wrong channel names and skips re-resolution.

_Fix:_ Either snapshot `ChannelIdsInput` inside `LastChannelResolution` (include it in the result type) and compare at line 244, or clear `LastChannelResolution = null` whenever `ChannelIdsInput` changes in the view. The `ResetConfig()` path already nulls it, but mid-flow edits via `SyncInputToViewModel` do not.

### [UNVERIFIED] correctness — `src/Netclaw.Cli/Tui/Wizard/Steps/HealthCheckStepViewModel.cs:220`
**`Results[^1]` index access after `UpdateLast` can panic if `Results` list is empty**

`HealthCheckRunner.UpdateLast` at line 42 in HealthCheckRunner.cs guards with `if (Results.Count > 0)`, so that call site is safe. However, in `RunHealthCheckCoreAsync` at line 220, the code reads `Results[^1].Passed` directly after `StartIfNeededAndPollAsync` returns — a method that itself directly writes `Results[^1]` at lines 293, 337, 342, and 362 without guard. If `Results` is somehow empty at that point (e.g., if `runner.Add(new HealthCheckItem(ProgressLabel(wasRunning), null))` at line 214 failed due to an exception that was swallowed) the `Results[^1]` at line 220 would throw `IndexOutOfRangeException`, crashing the health-check task with no user-visible error.

_Fix:_ Change line 220 to `else if (Results.Count > 0 && Results[^1].Passed is null)` (which is already present) but additionally guard the direct `Results[^1] = ...` writes in `StartIfNeededAndPollAsync` behind `if (Results.Count > 0)` checks, mirroring the pattern in `HealthCheckRunner.UpdateLast`.

### [UNVERIFIED] correctness — `src/Netclaw.Cli/Tui/Wizard/Steps/HealthCheckStepViewModel.cs:71`
**TryAdvance() triggers a fire-and-forget health check run that is then overwritten if GoNext() is called again**

TryAdvance() (line 71) assigns `HealthCheckCompletion = RunHealthCheckAsync()` as a fire-and-forget when !IsRunning && !IsComplete. However, the caller (WizardOrchestrator.GoNext) does not use the value returned by TryAdvance — it returns true (handled internally), so the check stays on screen. If the orchestrator calls TryAdvance again before IsRunning becomes true (e.g. rapid Enter presses during the async startup of the health check), RunHealthCheckAsync is called a second time and the first task's reference is overwritten in HealthCheckCompletion. Both tasks run concurrently, both write IsRunning/IsComplete, and both append to the shared Results list — producing duplicate entries and undefined completion order. The InitWizardViewModel.GoNext path guards with `if (!healthStep.IsRunning.Value && !healthStep.IsComplete.Value)` before calling StartWithOrchestrator, but the TryAdvance() path (which is the fallback path if WizardOrchestrator.GoNext is called directly) has no such guard.

_Fix:_ Set `IsRunning.Value = true` synchronously before launching the task in TryAdvance(), so that a second call sees IsRunning=true and returns without starting a second run. Alternatively remove TryAdvance as a trigger for the health check and require callers to go through StartWithOrchestrator exclusively.

### [UNVERIFIED] correctness — `src/Netclaw.Cli/Tui/Wizard/Steps/IdentityStepViewModel.cs:81`
**PrefillFromExistingConfig called on every OnEnter — user edits overwritten when navigating back**

PrefillFromExistingConfig (line 81) is called every time the identity step is entered, including when navigating back from a later step. It uses `??=` for CommunicationStyle and UserName (so user entries are preserved if non-null), but for AgentName and UserTimezone it uses `ReadString(...) ?? AgentName` — i.e., the existing config value wins if present, even when the user already edited the field in this wizard session. If a user changes AgentName from 'Netclaw' to 'MyBot', then navigates to the next step and comes back, AgentName will be reset to the value from the on-disk config (since the field is not null-guarded). This is especially visible in re-init flows where ExistingConfig contains the previous run's values.

_Fix:_ Guard all prefill assignments with null-or-empty checks on the current field value, matching the pattern used for CommunicationStyle/UserName, so that any field the user has already edited is not overwritten: `AgentName = string.IsNullOrWhiteSpace(AgentName) ? (ReadString(context, ...) ?? AgentName) : AgentName;`

### [UNVERIFIED] correctness — `src/Netclaw.Cli/Tui/Wizard/WizardConfigBuilder.cs:82`
**PreserveExistingUpdateChannel reads the config file a second time via ConfigurationBuilder after LoadJsonDict already loaded it**

WizardConfigBuilder constructor loads the existing config at line 31 via ConfigFileHelper.LoadJsonDict into _existingConfig. PreserveExistingUpdateChannel (line 77) then creates a new ConfigurationBuilder and re-reads the same file from disk. This means there is a window where a concurrent write between construction and WriteConfigFile (rare but possible when the daemon's ConfigWatcherService is also writing) could cause the two reads to see different data. More practically, the double-read is simply redundant — _existingConfig already has the Daemon.UpdateChannel value if present.

_Fix:_ Read UpdateChannel from _existingConfig directly instead of re-reading the file. The already-loaded dictionary can be accessed with ConfigFileHelper.GetSectionOrNull(_existingConfig, 'Daemon') and the UpdateChannel key read from that.

### [UNVERIFIED] correctness — `src/Netclaw.Cli/Tui/Wizard/WizardConfigBuilder.cs:536`
**WriteSecretsFile has an operator precedence ambiguity in the final write gate**

Line 536 reads: `if (hasDirectSecrets || contributionChanged && (_secretsFileExists || HasUserSecretData(merged)))`. In C#, `&&` has higher precedence than `||`, so this parses as `hasDirectSecrets || (contributionChanged && (_secretsFileExists || HasUserSecretData(merged)))`. This means: if there are direct secrets, always write regardless of whether the file exists or has user data. That is likely intentional for the fresh-install case (first-time secrets write). However, the intent of the gate appears to be 'write only if there is something meaningful to write' — and hasDirectSecrets alone bypasses the _secretsFileExists / HasUserSecretData guards entirely. If a step contributes a placeholder or empty section via AddSection, secrets.json is written unconditionally even to a fresh config with no real secrets.

_Fix:_ Make the intent explicit with parentheses: `if ((hasDirectSecrets || contributionChanged) && (_secretsFileExists || HasUserSecretData(merged) || hasDirectSecrets))` or use an early-return pattern with clearly named intermediate booleans.

### [UNVERIFIED] design — `src/Netclaw.Cli/Tui/Config/ExposureModeConfigViewModel.cs:22`
**ExposureModeConfigViewModel creates a ProviderDescriptorRegistry with an empty registry on every construction**

The constructor (line 22–32) initialises `WizardContext` with `Registry = new ProviderDescriptorRegistry([])`. This is a hollow registry with no provider descriptors. `ExposureModeStepViewModel` does not use the provider registry, so this has no functional impact today. However, any future step that is added to the single-step orchestrator that does query `context.Registry` would receive an empty registry silently, rather than an exception, potentially leading to the 'no providers available' experience without any diagnostic.

_Fix:_ Pass the real `ProviderDescriptorRegistry` (from DI) into `ExposureModeConfigViewModel` if there is any chance the wizard context will be shared with steps that use provider data. If the empty registry is truly intentional and permanent, add a comment stating this is by design.

### [UNVERIFIED] design — `src/Netclaw.Cli/Tui/Config/SearchConfigEditorViewModel.cs:510`
**IsDirty reads config from disk on every access**

ComputeIsDirty() (line 510-515) calls `_mapper.Load(_paths)` which reads and deserializes the netclaw.json config file from disk every time IsDirty is evaluated. The page can call IsDirty repeatedly during rendering and keybinding evaluation. Each call is a synchronous disk read that blocks the render loop thread. Under normal file sizes this is fast but not free; on network-mounted config paths or slow disks this will visibly stall rendering.

_Fix:_ Cache the persisted baseline at construction time and on each ReloadPersistedDraft() call (a field like `_persistedSnapshot`). Update the snapshot after every successful save. ComputeIsDirty() then compares in-memory values only, with no I/O.

### [UNVERIFIED] design — `src/Netclaw.Cli/Tui/Config/SkillSourcesConfigViewModel.cs:501`
**RouteRequested delegate is declared and called but never wired — dead code in both ViewModels**

`SkillSourcesConfigViewModel` declares `internal Action<string>? RouteRequested` at line 201 and invokes `RouteRequested?.Invoke("/config")` at line 505 alongside `Navigate?.Invoke("/config")`. `WorkspacesConfigViewModel` does the same at lines 28/160. `Navigate` is the Termina framework's page-router delegate and is wired by the framework on `RegisterRoute`. `RouteRequested` is never assigned anywhere in the codebase (confirmed: grep found no assignment site in `Program.cs` or any page). Every call to `RouteRequested?.Invoke(...)` is thus a guaranteed no-op in production. The only observable navigation is `Navigate?.Invoke(...)`. Having two invocations with one always a no-op is misleading and could cause confusion if a future test wires `RouteRequested` instead of `Navigate`.

_Fix:_ Remove the `RouteRequested` property and all its call sites from both `SkillSourcesConfigViewModel` and `WorkspacesConfigViewModel`. Navigation is already handled correctly by `Navigate?.Invoke("/config")`.

### [UNVERIFIED] design — `src/Netclaw.Cli/Tui/Config/SkillSourcesConfigViewModel.cs:27`
**`SkillFeedReachabilityProbe` creates a new `HttpClient` per probe call — resource waste and no connection reuse**

`SkillFeedReachabilityProbe.Probe` at line 35 does `using var client = new HttpClient { Timeout = timeout }` inside the method body, so a new `HttpClient` (and its underlying `HttpClientHandler` / socket) is created and immediately disposed on every probe invocation. This suppresses connection pooling and, on .NET, can exhaust ephemeral ports under repeated rapid probes (socket TIME_WAIT). The correct pattern per Microsoft guidance is to reuse `HttpClient` instances or use `IHttpClientFactory`.

_Fix:_ Make `SkillFeedReachabilityProbe` hold a single `HttpClient` field (or accept `IHttpClientFactory`). The timeout should be applied per-request via `CancellationTokenSource` rather than as `HttpClient.Timeout`, so a single client can serve probes with different timeouts.

### [UNVERIFIED] design — `src/Netclaw.Cli/Tui/Sections/ConfigEditorSession.cs:96`
**Duplicate PruneEmptySections implementations between ConfigEditorSession (secrets) and ConfigFileHelper (config)**

There are two independent `PruneEmptySections` implementations: one at `ConfigEditorSession.cs:168` operating on `Dictionary<string, object>` with an iterative descent, and one at `ConfigFileHelper.cs:293` using `TryGetPathValue` + `RemovePath` with mutual recursion. The comment at line 108–113 documents the intentional divergence but also notes that the two engines must stay in sync. Over time, a fix to one is unlikely to be applied to the other. The `ConfigFileHelper` version is also indirectly recursive (`RemovePath` calls `PruneEmptySections` which calls `RemovePath`), making its depth bounded by path length but harder to audit for infinite-loop correctness.

_Fix:_ Consolidate behind a single `PruneEmptySections(Dictionary<string,object> root, IReadOnlyList<string> segments)` helper in `ConfigFileHelper` and make the secrets path call it directly. The only real divergence is that `ConfigEditorSession.SetSecretPathValue` uses `GetOrCreateSection` (throws on scalar collision) while `ConfigFileHelper.SetPathValue` overwrites — that divergence lives in the write path, not in prune, so consolidation of the prune logic is safe.

### [UNVERIFIED] design — `src/Netclaw.Cli/Tui/Sections/ConfigEditorStateStore.cs:23`
**ConfigEditorStateStore.Apply performs N disk read+write pairs for N contributions**

Each call to `stateStore.Apply(actions)` does a full `LoadState()` (file read + deserialize) and `WriteState()` (serialize + file write) round-trip. In `ConfigEditorSession.Apply` this happens once per `contribution` because `Apply` is called once per section. In `ConfigEditorSession.ApplyEditorStateActions` (the static batch path), a single `ConfigEditorStateStore` instance is reused across contributions, but each `stateStore.Apply(contribution.StateActionsOrEmpty)` still triggers a full read-modify-write. For the wizard's multi-section commit (N sections, each with state actions), this produces N redundant reads and N sequential writes when 1+1 would be sufficient.

_Fix:_ Expose an internal batch method on `ConfigEditorStateStore` that accepts `IEnumerable<IEnumerable<SectionEditorStateAction>>`, performs a single `LoadState`, applies all action batches, then a single `WriteState`. Update `ApplyEditorStateActions` to use it. The per-`Apply` path (called from `ConfigEditorSession.Apply`) already only writes once per call so it is acceptable.

### [UNVERIFIED] design — `src/Netclaw.Cli/Tui/Wizard/Steps/HealthCheckStepViewModel.cs:71`
**TryAdvance() on HealthCheckStepViewModel starts a fire-and-forget health check via RunHealthCheckAsync in standalone mode**

TryAdvance() at line 71 calls `HealthCheckCompletion = RunHealthCheckAsync()` when !IsRunning && !IsComplete. RunHealthCheckAsync (line 133) is the no-op standalone path. However, in normal wizard operation, InitWizardViewModel.GoNext() intercepts the health check step before calling orchestrator.GoNext(), so TryAdvance is never reached via normal flow. The inconsistency is that TryAdvance — which always returns true — means the orchestrator never moves past the health check step even if it somehow ends up calling GoNext on the orchestrator. In tests that directly call orchestrator.GoNext(), the wizard will not advance past health check and TryAdvance returns true (handled internally) rather than false (step complete), which is the correct semantic for 'health check is an endpoint' but could mislead test authors.

_Fix:_ Document the intentional design: TryAdvance always returns true because the health check step is a terminal step — the wizard never advances past it via the orchestrator path. Add a comment to that effect, and ensure InitWizardViewModel.GoNext() always intercepts this step (which it does) so TryAdvance is never the primary code path.

### [UNVERIFIED] design — `src/Netclaw.Cli/Tui/Wizard/Steps/ProviderStepViewModel.cs:101`
**TryAdvance always returns false — orchestrator never naturally advances the provider step; View calls SetSubStep directly for all transitions**

TryAdvance() at line 101 returns false unconditionally with the comment 'step complete'. This means every Enter keypress on the provider step is treated as 'step complete — move to next'. The actual sub-step navigation (provider selection → auth method → credentials → validation → model) is driven entirely by the View calling SetSubStep directly (lines 98, 115 in the view, and via StartProbe success callbacks). The orchestrator's GoNext will therefore advance to IdentityStep the moment TryAdvance returns false — even if the user is on sub-step 0 and hasn't selected a provider yet. The Page must prevent this by intercepting Enter for the provider step, which it does via the step view's HandleKeyPress. This creates a hidden coupling: if a new code path triggers GoNext on the orchestrator while the provider step is active, the wizard silently advances past it.

_Fix:_ TryAdvance should guard against premature advancement by checking the current sub-step and returning true (handled) if the step is not yet complete (e.g., sub-step < 4, or model not selected). This makes the step self-protecting rather than relying entirely on the view's capture logic.

### [UNVERIFIED] design — `src/Netclaw.Cli/Tui/Wizard/WizardOrchestrator.cs:160`
**Two-phase config write in `WriteConfig` creates a latent conflict risk between `ContributeConfig` and `BuildContribution`**

For steps implementing `ISectionEditor`, `WriteConfig` calls both `step.ContributeConfig(configBuilder)` (writes typed section objects) and then `sectionEditor.BuildContribution(step)` (applies field-action overrides that win). The comment at line 172-174 acknowledges this: "the two must stay in agreement… so the clobbered typed write is a genuine no-op". `ExposureModeStepViewModel` illustrates the problem concretely: `ContributeConfig` writes `Daemon.Host`, `Daemon.TrustedProxies`, and `Webhooks` when `IncludeWebhookToggle && SelectedMode != Local`, but `BuildContribution` (used from the config editor where `IncludeWebhookToggle = false`) only writes `Daemon.ExposureMode` and deletes `Daemon.Host`/`Daemon.TrustedProxies` for non-reverse-proxy modes — it does not write `Webhooks` at all. When `IncludeWebhookToggle` is `false` (config-editor path), `WebhooksEnabled` cannot be set and `ContributeConfig` skips it; but if future code sets `WebhooksEnabled` before calling `WriteConfig` in config-editor mode, `BuildContribution` silently drops it. The two emission paths need explicit reconciliation or `ContributeConfig` should be removed from steps that are `ISectionEditor`.

_Fix:_ For steps implementing `ISectionEditor`, remove `ContributeConfig` (make it a no-op) and rely solely on `BuildContribution` as the single emission path. This eliminates the double-write and the comment-documented fragile invariant.

### [UNVERIFIED] error-handling — `src/Netclaw.Cli/Tui/Config/SearchConfigEditorPage.cs:282`
**Fire-and-forget `_ = ViewModel.SubmitCurrentConfigurationFromInputAsync()` in key handler**

`SearchConfigEditorPage` at line 282 (key handler for Enter) launches `_ = ViewModel.SubmitCurrentConfigurationFromInputAsync()` discarding the task. If this async method throws an unhandled exception it is silently lost — no UI error, no user feedback. The method is async and performs validation + HTTP probe work, meaning any error in that chain (network exception, config write failure, etc.) disappears.

_Fix:_ Await the task in a try/catch within the page, or ensure `SubmitCurrentConfigurationFromInputAsync` catches and surfaces all exceptions to a status message before returning.

### [UNVERIFIED] error-handling — `src/Netclaw.Cli/Tui/Config/SkillSourcesConfigViewModel.cs:2185`
**Bare catch in SuggestNameFromUrl swallows all exception types including OutOfMemoryException**

`SuggestNameFromUrl` at line 2185 contains `catch { return "custom-feed"; }` — a bare catch with no filter. This suppresses any exception type thrown by `new Uri(url)`, including `ThreadAbortException`, `OutOfMemoryException`, and `StackOverflowException`. Since `TryNormalizeFeedUrl` has already validated the URL as an absolute HTTP/HTTPS URI before `SuggestNameFromUrl` is called, the `Uri` constructor will never throw `UriFormatException` at this point. The bare catch masks bugs — for example, a `NullReferenceException` on `uri.Host` would silently become `"custom-feed"`.

_Fix:_ Replace `catch` with `catch (UriFormatException)` (the only exception `new Uri(string)` throws for malformed input). Alternatively, use `Uri.TryCreate` to avoid exceptions entirely: `return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? NormalizeSourceName(uri.Host) : "custom-feed";`

### [UNVERIFIED] error-handling — `src/Netclaw.Cli/Tui/ProviderManagerViewModel.cs:721`
**Fire-and-forget `_ = RevalidateAsync(DetailProvider)` without exception observation**

`RevalidateDetailProvider` at line 714 launches `_ = RevalidateAsync(DetailProvider)` and discards the task. `RevalidateAsync` has an exception handler (`catch { item.Health = ProviderHealthStatus.Unhealthy; }`) that swallows all exceptions silently. If `_probe.ProbeAsync` throws beyond the catch (e.g., a `ThreadAbortException` or the catch itself throws), the unhandled exception on the discarded task is silently lost — no UI feedback, no logs. The same issue affects `RevalidateAsync` at line 724 where `item.Entry is null` path calls `ProbeAsync` with a null `GetProbeCredential(null)` argument.

_Fix:_ Await `RevalidateAsync` (make `RevalidateDetailProvider` async), or add a `.ContinueWith` that faults visibly. Also guard against `item.Entry` being null before invoking the first overload.

### [UNVERIFIED] error-handling — `src/Netclaw.Cli/Tui/Sections/SectionEditorInfrastructure.cs:53`
**SectionFieldAction.Set with null Value has no contract guard, persists JSON null into config**

`SectionFieldAction` is a positional record with `object? Value = null` and no validation. When `Action == Set` and `Value == null`, `ConfigEditorSession.ApplyFieldActions` calls `ConfigFileHelper.SetPathValue(config, action.Path, action.Value)`, which executes `current[segments[^1]] = value!` (null-forgiving). This persists `"key": null` into `netclaw.json`. For schema fields that are required or non-nullable, this produces a JSON doc that fails `ConfigSchemaDoctorCheck` at runtime — a silent pre-persistence violation. `SectionSecretAction` (same file, line 55) throws `ArgumentNullException` for `Set+null`. This is an asymmetric contract.

_Fix:_ Add a validating constructor to `SectionFieldAction` mirroring `SectionSecretAction`: throw `ArgumentNullException` when `action == Set && value == null`. Alternatively, convert it from a positional record to a class with a constructor guard.

### [UNVERIFIED] error-handling — `src/Netclaw.Cli/Tui/Wizard/Steps/MattermostStepViewModel.cs:229`
**Mattermost ContributeConfig persists null ServerUrl without validation — runtime connection fails silently**

`ContributeConfig` at line 229 writes `ServerUrl = string.IsNullOrWhiteSpace(ServerUrl) ? null : ServerUrl.Trim()`. If `ServerUrl` is null or whitespace, a null value is written to `MattermostConfigSection.ServerUrl`. `ContributeHealthChecksAsync` at line 252 calls `BeginAdapterCheck("Mattermost", MattermostEnabled, (ServerUrl, "server URL"), (BotToken, "bot token"))` which would catch this — but only if the health check runs. If the health check is skipped or not reached (e.g. the user cancels early and the wizard writes config anyway), a null ServerUrl is persisted and the daemon fails to connect with no indication of the config source of the problem.

_Fix:_ This is acceptable as long as the health check always runs before config write in the normal wizard flow. Validate that `WriteConfig()` in the orchestrator is never called without `RunHealthChecksAsync()` completing. If the wizard can write config while skipping health checks (early exit path), add a validation guard in `ContributeConfig` itself.

### [UNVERIFIED] resource-leak — `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs:68`
**`WizardContext` created in `ChannelsConfigViewModel` constructor uses a `new ProviderDescriptorRegistry([])` that is never disposed**

At line 69–75 a `WizardContext` is created with `Registry = new ProviderDescriptorRegistry([])`. `ProviderDescriptorRegistry` is not itself `IDisposable`, so there is no direct leak. However, `WizardContext` is an `IDisposable` (holding a `ReactiveProperty<string> StatusMessage`) and is properly disposed in `ChannelsConfigViewModel.Dispose()` at line 1275. The real issue is that a fresh `ProviderDescriptorRegistry` seeded with an empty array is semantically wrong for the channels config: any code path inside `ChannelPickerStepViewModel` or its child adapters that calls `_context.Registry.Get(...)` will throw `InvalidOperationException` with a confusing "unknown provider" message. This is a latent correctness defect, not a resource leak.

_Fix:_ Inject the real `ProviderDescriptorRegistry` (or a no-op singleton) rather than constructing an empty registry that will fail loudly on any lookup.

### [UNVERIFIED] resource-leak — `src/Netclaw.Cli/Tui/InitWizardViewModel.cs:209`
**Individual wizard step view models are not disposed in Dispose — ProviderStepViewModel and HealthCheckStepViewModel hold reactive properties**

`InitWizardViewModel.Dispose()` calls `_orchestrator.Dispose()` which (in `WizardOrchestrator.Dispose()`, line 261) calls `step.Dispose()` on each `IWizardStepViewModel` in `_allSteps`. However `_stepViews` (the `Dictionary<string, IWizardStepView>`) is never iterated or disposed in `InitWizardViewModel.Dispose()`. `IWizardStepView` implementors like `ProviderStepView` and `HealthCheckStepView` may hold CompositeDisposable or other resources. More importantly, `_healthCheckStep` (an `IWizardStepViewModel`) is correctly disposed through the orchestrator, but `ProviderStep` is exposed as a public `ProviderStepViewModel` property and is the same object that's in `_allSteps` — so it is disposed through the orchestrator. The `_sectionEditors` registry is disposed. The step *views* (not view models) however are not disposable by interface so this is likely fine unless a future step view adds subscriptions in its constructor.

_Fix:_ Add a `foreach` loop over `_stepViews.Values` in `Dispose()` that calls `Dispose()` on any value implementing `IDisposable`. Alternatively, verify that no `IWizardStepView` implementation is `IDisposable`; if confirmed, document that assumption with a comment.

### [UNVERIFIED] resource-leak — `src/Netclaw.Cli/Tui/Sections/SectionEditorInfrastructure.cs:133`
**Partial construction of SectionEditorRegistry leaks IDisposable editors if a later registration throws**

In `SectionEditorRegistry`'s constructor (line 133), editors are created via `ActivatorUtilities.CreateInstance` and added to `_editors` one at a time (line 145). If creation or the duplicate-ID check throws for editor `i`, editors `[0..i-1]` that are `IDisposable` are already in `_editors` but `Dispose()` is never called — the partially-constructed registry is discarded without cleanup. The `InvalidOperationException` for duplicate IDs at line 141 makes this a startup-time-only risk, but any editor that opens file handles, subscriptions, or allocates unmanaged resources will leak.

_Fix:_ Wrap the construction loop in a try/catch: on exception, call `Dispose()` on all `IDisposable` entries already in `_editors` before re-throwing. Alternatively, construct all instances into a temporary list and validate before committing to `_editors`.

### [UNVERIFIED] security — `src/Netclaw.Cli/Tui/Config/SecurityAccessViewModel.cs:680`
**GetProfile silently returns the Public profile for unknown TrustAudience values**

`GetProfile` (line 680–687) returns `profiles.Public` for any `TrustAudience` value not explicitly handled in the switch (`_ => profiles.Public`). This fallback is the most restrictive tier, which is the correct fail-closed direction. However, `AudienceConfigName` (line 698) delegates to `AudienceLabel` (line 689) which returns `audience.ToString()` for unknown values. If a caller passes an out-of-range enum value, `SaveAudienceProfile` (line 543–546) writes the profile under an unrecognised key (e.g., `Tools.AudienceProfiles.4`), which adds an unknown property to the config rejected by `ConfigSchemaDoctorCheck` (`additionalProperties: false`).

_Fix:_ Throw `ArgumentOutOfRangeException` from `GetProfile` for unrecognised `TrustAudience` values, consistent with the pattern used in `ExposureModeExtensions.ToWireValue` and `RequiresRemoteAuthentication`. This makes the failure loud rather than producing a silently-corrupt config key.
