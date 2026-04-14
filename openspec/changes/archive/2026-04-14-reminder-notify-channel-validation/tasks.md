## 1. Resolver abstraction

- [x] 1.1 Create `src/Netclaw.Actors/Reminders/IReminderTargetResolver.cs` with the interface and `ReminderTargetResolution` record (Success, ErrorMessage, ResolvedChannelId, ResolvedUserId)

## 2. Slack adapter

- [x] 2.1 Create `src/Netclaw.Channels.Slack/SlackReminderTargetResolver.cs` wrapping `ISlackTargetResolver` and mapping `SlackTargetResolutionResult` to `ReminderTargetResolution`
- [x] 2.2 Register the adapter as `IReminderTargetResolver` in `src/Netclaw.Daemon/Configuration/SlackChannelRegistrationExtensions.cs`

## 3. Tool validation wiring

- [x] 3.1 Add `IReminderTargetResolver? targetResolver = null` constructor parameter to `SetReminderTool`
- [x] 3.2 Update the `ReportToChannel` parameter description to advertise `#channel-name`, `@username`, and raw IDs as valid inputs
- [x] 3.3 Insert validation block in `ExecuteAsync` after schedule parsing: fail loudly when resolver is null and target is supplied, call resolver when registered, replace `reportToChannel` with canonical ID on success, surface resolver error message on failure
- [x] 3.4 Ensure auto-extracted session channels bypass the resolver (no API call)
- [x] 3.5 Add `IReminderTargetResolver? targetResolver = null` parameter to `WithReminderTools` extension in `src/Netclaw.Actors/Tools/ToolRegistrationExtensions.cs`
- [x] 3.6 Resolve `IReminderTargetResolver` (optional) in `src/Netclaw.Daemon/Program.cs` and pass to `WithReminderTools`

## 4. Tests

- [x] 4.1 Add inline `TestResolver` stub in `src/Netclaw.Actors.Tests/Reminders/SetReminderToolTests.cs`
- [x] 4.2 Add `Rejects_invalid_report_to_channel_when_resolver_fails` case (resolver returns failure → tool error, no `SaveReminderCommand` sent)
- [x] 4.3 Add `Resolves_hash_channel_name_to_canonical_id` case (#general → C0123ABC persisted)
- [x] 4.4 Add `Rejects_report_to_channel_when_no_resolver_registered` case (null resolver + supplied target → specific error)
- [x] 4.5 Add `Auto_extracted_session_channel_skips_resolver` tripwire case (resolver throws if invoked)

## 5. Documentation + skill sync

- [x] 5.1 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md` `set_reminder` section to document the new accepted input formats and eager validation
- [x] 5.2 Bump `metadata.version` in the skill's YAML frontmatter per the skill-sync rule in CLAUDE.md

## 6. Verification

- [x] 6.1 Run `dotnet build` — clean across the solution
- [x] 6.2 Run `dotnet test src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj` — all reminder tests green including the existing `Self_targeting_captures_session_id`
- [x] 6.3 Run `dotnet slopwatch analyze` — no new violations
- [x] 6.4 Run `openspec validate reminder-notify-channel-validation` before archival
