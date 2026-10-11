---
name: run-reminder
description: "Test a scheduled reminder now, in this chat, so the user can answer its approval prompts and save grants for the unattended scheduled run. Use for /run-reminder <id> or when the user asks to test or try a reminder."
argument-hint: "<reminder id>"
allowed-tools: run_reminder
keywords:
  - test reminder
  - run reminder
  - try reminder
  - reminder approvals
metadata:
  author: netclaw
  version: "1.0.0"
license: MIT
compatibility: "netclaw >= 0.27.1"
---

# Run a Reminder Now

A scheduled reminder runs unattended. Nobody can answer an approval prompt
in that run, so a call that needs approval and has no saved grant is denied.
This skill runs the reminder's exact prompt in this chat, with the user
present. The user answers each prompt. An "Always" answer saves a grant that
the scheduled run reads.

The user message gives the reminder ID. If it is missing, call
`list_reminders` and ask which reminder to test.

## Procedure

1. **Warn the user first.** Tell the user that the test does the real work.
   It is not a dry run. For example, a disk-cleanup reminder really deletes
   files, and a reminder that posts to a channel really posts there. Ask the
   user to confirm before you continue.
2. **Get the prompt.** Call `run_reminder` with the reminder ID.
   - If the result starts with `Error:`, show the error to the user and stop.
     A reminder must be tested in a chat with the same audience as the
     reminder. The error tells the user where to run it.
3. **Follow the returned prompt exactly.** Do the steps that the prompt asks
   for, in this chat, as the scheduled run would. Do not change commands,
   folders, or tools to avoid a prompt. The test must use the same calls.
4. **Watch each approval result.** A tool result can end with a line such as
   `[approval: once]` or `[approval: always in this folder]`.
   - `[approval: once]` and `[approval: this chat only]` do NOT carry over to
     the scheduled run. Each scheduled run is a new session. Tell the user
     that this step will be denied when the reminder runs on its schedule.
     Offer to run the step again so the user can choose an "Always" answer.
   - `[approval: always in this folder]`, `[approval: always in this repo]`,
     and `[approval: always anywhere]` save a grant for this audience. The
     scheduled run reads it.
5. **Summarize at the end.** List each step that needed approval:
   - the steps that now have an "Always" grant;
   - the steps that the scheduled run will still deny, and why (a one-time
     or chat-only answer, a denial, or a call that no grant can cover).

## Pitfalls

- Do not use `set_reminder` to make a copy of the reminder for the test. The
  test must use the real reminder's prompt and audience.
- The test does not change the schedule or the reminder history.
- A model can choose other commands on a later run. A grant matches the
  command and the folder that the user approved. A repository or "anywhere"
  grant accepts more change than a folder grant.
