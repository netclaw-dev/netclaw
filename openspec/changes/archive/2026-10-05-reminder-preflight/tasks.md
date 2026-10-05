## 1. Tool

- [x] 1.1 Share `ReminderExecutionActor.BuildPrompt` between the scheduled run and the tool; verify the existing reminder execution tests pass
- [x] 1.2 Add `RunReminderTool` with the audience and interactive-approval gates; verify `ReminderPreflightGrantTests` (wider chat refused)
- [x] 1.3 Add `run_reminder` to the scheduling catalog, the Team defaults, the feature gate, registration, and the Security Access help text; verify `ToolAudienceProfileDefaultsTests`

## 2. Grant proof

- [x] 2.1 Prove that an "Always here" grant saved in a chat test lets the unattended scheduled run pass, and that the run denies without it; verify `ReminderPreflightGrantTests`

## 3. Entry points

- [x] 3.1 Add the `run-reminder` system skill; verify the daemon embeds it and the eval case `skill_activation_run_reminder` exists (not run)
- [x] 3.2 Add `netclaw reminder run <id>`; verify the `reminder-run` smoke tape

## 4. Docs

- [x] 4.1 Update `netclaw-operations` (SKILL.md and references/scheduling.md) and bump its version
- [x] 4.2 Update `docs/runbooks/tool-approval-gates.md` and `docs/spec/configuration.md`
