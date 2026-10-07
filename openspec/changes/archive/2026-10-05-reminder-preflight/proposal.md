## Why

A scheduled reminder runs unattended. Nobody can answer an approval prompt in
that run, so a call that needs approval and has no saved grant is denied. The
owner of a reminder needs one attended run of the exact prompt, in a chat, to
save the grants that the scheduled run reads (PRD-008, PRD-002).

## What Changes

- Add the `run_reminder` agent tool (grant category `scheduling`). It returns
  the exact prompt of an existing reminder, with an instruction to carry it out
  in the current chat.
- `run_reminder` refuses a chat with a wider audience than the reminder, and a
  turn with no interactive approval. A reminder above the caller audience reads
  as not found (the existing #2240 rule).
- Add the `run-reminder` system skill. `/run-reminder <id>` loads it. The skill
  warns that the steps are real, calls `run_reminder`, follows the prompt, and
  summarizes which steps have "Always" grants.
- Add `netclaw reminder run <id>`. It opens a normal chat with
  `/run-reminder <id>` as the hidden first message.
- The Team default tool allowlist gains `run_reminder`. A disabled scheduling
  subsystem hides it.

In scope: the tool, the skill, the CLI verb, docs. Out of scope: a separate
unattended manual run, a special approval mode, prompt option changes, and a
reminder history entry for a test.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-scheduling`: add the `run_reminder` agent tool requirement and the
  `netclaw reminder run` CLI scenario.

## Impact

- Code: `RunReminderTool`, `ReminderExecutionActor.BuildPrompt` (shared by
  the scheduled run and the tool), tool catalog and registration, CLI
  `Program.cs` and `ReminderCommand`.
- Skills: new `run-reminder`; `netclaw-operations` scheduling guidance.
- Security: the test runs as a normal attended turn with the chat's real
  authority. The tool never widens authority. Grants go to the chat audience,
  which equals the reminder audience.
- Operations: a test does real work and can post to the reminder's channel.
  The skill warns before the run.
