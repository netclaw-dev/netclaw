## 1. PR-A — Trust-bearing record shapes

- [x] 1.1 Convert `SourceProvenance` (`Netclaw.Actors/Channels/SourceProvenance.cs`) to a 2-parameter primary constructor `(TransportAuthenticity, PayloadTaint)`; keep `SourceScope`/`SourceKind` as optional `init`; remove the `Unknown` sentinel defaults.
- [x] 1.2 Remove the `SourceProvenance.StrictDefault()` factory; update its callers to construct an explicit `new SourceProvenance(TransportAuthenticity.Unverified, PayloadTaint.Public)` where a conservative value is genuinely needed.
- [x] 1.3 Make `ChannelInput.Audience`, `Boundary`, `Principal`, `Provenance` (`Netclaw.Actors/Channels/ChannelInput.cs`) `required` and non-nullable.
- [x] 1.4 Make `MessageSource.Audience`, `Boundary`, `Principal`, `Provenance` (`Netclaw.Actors/Channels/MessageSource.cs`) `required`; delete the four sentinel-default initializers.
- [x] 1.5 Remove the four `?? options.DefaultX` fallback arms in `MessageSourceFactory.Create` (`Netclaw.Actors/Channels/ChannelPipeline.cs`); assign trust fields directly from `input`.
- [x] 1.6 Delete `SessionPipelineOptions.DefaultAudience`, `DefaultBoundary`, `DefaultPrincipal`, `DefaultProvenance`.
- [x] 1.7 Update `SlackThreadBindingActor.BuildOptions` and `SlackThreadHistoryFetcher.ConvertMessageAsync` to stamp explicit `Audience`, `Boundary`, `Principal`, `Provenance` onto every `ChannelInput`.
- [x] 1.8 Update `DiscordSessionBindingActor.BuildOptions` and `DiscordThreadHistoryFetcher` to stamp explicit trust context onto every `ChannelInput`.
- [x] 1.9 Update `SignalRSessionActor.BuildOptions` (`Netclaw.Daemon/Gateway`) to stamp explicit trust context.
- [x] 1.10 Update `WebhookExecutionActor.InitializeAsync` (`Netclaw.Daemon/Webhooks`) to stamp explicit trust context onto its `ChannelInput`.
- [x] 1.11 Update `ReminderExecutionActor.InitializeAsync` (`Netclaw.Actors/Reminders`) to stamp explicit trust context onto its `ChannelInput`.
- [x] 1.12 Fix all remaining compiler errors from the required-property change across `Netclaw.Actors`, channel projects, and `Netclaw.Daemon`.
- [x] 1.13 Update affected unit tests (`Netclaw.Actors.Tests`, `Netclaw.Channels.Slack.Tests`, `Netclaw.Channels.Discord.Tests`, `Netclaw.Daemon.Tests`) to construct trust-bearing records with explicit trust context.
- [x] 1.14 Verify PR-A: `dotnet build` clean, `dotnet test` green for affected projects, `dotnet slopwatch analyze` no new violations, `./scripts/Add-FileHeaders.ps1 -Verify` passes.

## 2. PR-B — Elevated-fallback sites become explicit throws

- [x] 2.1 Replace `source?.Audience ?? TrustAudience.Personal` / `source?.Boundary ?? SecurityPolicyDefaults.PersonalBoundary` in `SessionToolExecutionPipeline` (background-job submission) with an explicit `throw new InvalidOperationException` on a missing turn source.
- [x] 2.2 Replace `msg.Audience ?? TrustAudience.Personal.ToWireValue()` in `SubAgentActor` with an explicit `throw` on a missing audience.
- [x] 2.3 Replace `?? new NullPromptInjectionDetector()` in `SlackThreadBindingActor`, `SlackChannel`, `DiscordSessionBindingActor`, and `DiscordChannel` with an explicit `throw`; delete the now-unused `NullPromptInjectionDetector`.
- [x] 2.4 Update tests for the new throw behavior (`RunSubAgent` carries an explicit audience; gateway-dependency fixtures wire a real detector).
- [x] 2.5 Verify PR-B: build clean, tests green, slopwatch clean, file headers verified.

## 3. PR-C — `ToolExecutionContext` / `RunSubAgent` audience typing

- [x] 3.1 Change `ToolExecutionContext.Audience` (`Netclaw.Tools.Abstractions`) from `string?` to `TrustAudience?`.
- [x] 3.2 Update the write sites that build `ToolExecutionContext` (`SessionToolExecutionPipeline`, `LlmSessionActor`, `SubAgentActor`, daemon REST reminder path) to set the typed audience directly.
- [x] 3.3 Update read sites (`SpawnAgentTool`, `SubAgentSpawner`, `SkillLoadTool`, `SkillReadResourceTool`, `ToolAccessPolicy`, `CheckBackgroundJobTool`, `SetReminderTool`, `ToolRegistry`) to consume the typed audience.
- [x] 3.4 Change `RunSubAgent.Audience` (`Netclaw.Actors/SubAgents/SubAgentProtocol.cs`) from `string?` to `TrustAudience?`.
- [x] 3.5 Delete `SecurityPolicyDefaults.ParseAudienceOrPublic`; retype `ResolveAudienceWithFallback` (and `MemoryPolicyScopeResolver.ResolveAudience`) to take `TrustAudience?` so no wire-string parsing remains on the read path.
- [x] 3.6 Update affected tests for the typed audience.
- [x] 3.7 Verify PR-C: build clean, tests green, slopwatch clean, file headers verified. Eval suite not triggered — PR-C is an internal type change and does not alter model-facing tool schemas, grant categories, or definitions.

## 4. PR-D — Persisted records: required trust fields, reject legacy documents

- [x] 4.1 Add a shared `LegacyTrustFieldGuard` helper (`Netclaw.Actors/Persistence/`) that, given a job/reminder JSON document, returns which `audience`/`boundary` keys are absent or explicitly null.
- [x] 4.2 Make `BackgroundJobDefinition.Audience`/`Boundary` (`Netclaw.Actors/Jobs/BackgroundJobProtocol.cs`) `required`; reject a legacy document in `BackgroundJobDefinitionStore` — log an error and exclude it from `Get`/`List`.
- [x] 4.3 Make `ActiveJobInfo.Audience`/`Boundary` (`Netclaw.Actors/Jobs/ActiveJobInfo.cs`) `required` — compile-time only; `ActiveJobInfo` is protobuf-serialized and proto3 defaults a missing audience to `Public` (fail-closed).
- [x] 4.4 Make `ReminderDefinition.Audience`/`Boundary` (`Netclaw.Actors/Reminders/ReminderProtocol.cs`) `required` and non-nullable; reject a legacy document in `ReminderDefinitionStore` — log an error, exclude it from `Get`/`List`, preserve the file (do not prune it as corrupt JSON).
- [x] 4.5 Fix in-process construction sites that omit the now-required trust fields (`SetReminderTool`, `ReminderManagerActor`, `ReminderExecutionActor` dead null-checks).
- [x] 4.6 Add tests: a legacy reminder document missing trust fields is rejected and preserved (regression test — excluded from `Get`/`List`, error logged, file kept); the legacy-job equivalent; and current documents round-trip verbatim.
- [x] 4.7 Verify PR-D: build clean, tests green, slopwatch clean, file headers verified.

## 5. Cross-cutting verification and documentation

- [ ] 5.1 Manual smoke: restart the daemon with the Personal-DM Slack configuration from PR #993; confirm `shell_execute` is permitted (no Public downgrade).
- [ ] 5.2 Manual smoke: place a known legacy `*.job.json` / reminder document (no trust fields) in the persistence directory; confirm the store rejects it loudly (error logged, job/reminder not loaded) and leaves the file in place.
- [ ] 5.3 Update operator-facing docs / runbook with the upgrade note: legacy job/reminder documents missing trust fields are rejected at load and must be recreated or have `audience`/`boundary` added.
- [ ] 5.4 Run `/opsx-verify` against this change, then `/opsx-sync` and `/opsx-archive`.
