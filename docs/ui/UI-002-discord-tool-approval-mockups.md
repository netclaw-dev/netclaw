# UI-002: Discord Tool Approval Mockups (Planning)

Source PRDs: `PRD-009`, `PRD-003`, `PRD-002`, `PRD-001`

## Purpose

Define planning mockups for Discord tool-approval UX with both desktop and
mobile flows. This is a planning artifact only (no implementation), created to
support OpenSpec change `discord-channel-with-interactions`.

## UX Principles

- default-deny posture is always explicit in prompt copy
- deterministic choices only: Approve once, Approve for this chat, Approve always, Deny
- interaction-first UX when available, deterministic text fallback always present
- no hidden fallback behavior; fallback state is visible to the user

## Desktop Mockup

```
+--------------------------------------------------------------------------------+
| #ops-bot-thread                                                     DISCORD DESK |
+--------------------------------------------------------------------------------+
| Netclaw wants to run: `git push origin main`                                  |
| Risk: writes to remote branch                                                  |
| Session: discord/ch-7/th-42                                                    |
|                                                                                |
| [Approve once] [Approve for this chat] [Approve always] [Deny]                |
|                                                                                |
| Fallback status: Interactions healthy                                          |
+--------------------------------------------------------------------------------+
```

Behavior notes:

- button labels map 1:1 to approval decision enums
- prompt includes session identifier and concise risk statement
- footer line indicates whether interactive callbacks are healthy

## Mobile Mockup

```
+--------------------------------------+
| Discord Mobile Thread                |
+--------------------------------------+
| Netclaw: run `gh pr create`?         |
| Risk: repository write operation     |
| Session: discord/ch-9/m-5544         |
|                                      |
| [Approve once]                       |
| [Approve for this chat]              |
| [Approve always]                     |
| [Deny]                               |
|                                      |
| If buttons fail, reply A/B/C/D       |
+--------------------------------------+
```

Behavior notes:

- vertical stack supports narrow-screen touch targets
- deterministic fallback instruction is always visible in mobile layout

## Deterministic Text Fallback Mockup

```
Netclaw approval required:
Tool: shell_execute
Command: git push origin main
Session: discord/ch-7/th-42

Reply with:
  A) Approve once
  B) Approve for this chat
  C) Approve always
  D) Deny
```

Fallback parsing rules (planning):

- accepts `A|B|C|D` (case-insensitive)
- accepts canonical phrases (`approve once`, `approve for this chat`, `approve always`, `deny`)
- invalid replies trigger deterministic re-prompt with same options

## Traceability

- `PRD-009` Input Adapters and unified transport contract
- `PRD-003` Ops console and mobile triage planning requirements
- `PRD-002` Explicit approval and default-deny security posture
- `PRD-001` MVP operator workflows and deterministic session behavior
