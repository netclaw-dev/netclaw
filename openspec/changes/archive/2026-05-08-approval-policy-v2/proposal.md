## Why

Directory-scoped approvals (PR #896 / #927 / #937) landed on `dev` but real use exposes two unshipped problems we want to fix together before the feature ever reaches a release: (1) we prompt for too much — even read-only `grep`/`ls`/`git status` against the user's project trigger an approval, because the only declared safe space is the per-session scratch dir; (2) when we do prompt, the on-disk store mingles verb chains, full normalized commands, directory roots, and bash-fragment garbage in a single flat string list, and the prompt's `Patterns` / `Directory Roots` split is not something users can reason about. Aaron's persisted `tool-approvals.json` accumulated 50 entries including nonsense like `done`, `for pid`, `awk {print $2})`, `do threads=$(grep`. We have no users yet — this is the moment to do a clean breaking redesign instead of compounding the problem.

## What Changes

- **BREAKING** `tool-approvals.json` schema goes to `version: 2`. Each entry is a typed `(verb, directory)` pair (`{ "verb": "git remote", "directory": "/abs/path/" | null }`). v1 files are quarantined to `.v1.bak` on first read and a fresh v2 store is written. No automatic translation.
- **BREAKING** Approval matcher operates on `ApprovalEntry` objects, not on opaque strings. The "is this string a verb? a path? a normalized command?" inspection logic is deleted.
- **BREAKING** Approval prompt UX: 5-button row replaces today's 4. New: `Once`, `This chat`, `Always here`, `Always anywhere`, `Deny`. `Always anywhere` and `Deny` are styled as danger. Prompt body shows the cwd in the header and verbs as bullets, eliminating the `Patterns` / `Directory Roots` split. Resolution message replaces the dual sections with a single line ("Saved: jsonlint, git pull in ~/repos/foo/" or "Saved: freshdesk anywhere").
- New: **safe-verbs ∩ safe-space short-circuit.** A curated per-OS safe-verbs list (`safe-verbs.linux.json`, `safe-verbs.windows.json`) plus the agent's existing safe spaces (`session_dir`, optional `project_dir` from `WorkingContext`) form a three-position policy: auto-run when the verb is on the list and cwd is under a safe-space root; prompt otherwise; hard-deny list unchanged. Mutation inside a safe space still prompts (`git push` in your repo still prompts).
- New: `ScopedShellSafeVerbPolicy` mirrors `ScopedFileAccessPolicy` and reuses `ToolAudienceProfileResolver` for audience-aware root resolution and symlink-segment protection. Public audience inherits the same `session_dir`-only restriction file_read has.
- New: `ShellTool` cwd defaults to `project_dir` if set, else `session_dir`. Today it inherits the daemon process's cwd, which is a security and UX bug (`src/Netclaw.Actors/Tools/ShellTool.cs:81-82`).
- New: Compound shell commands extract every verb chain and present them as one prompt grouped by the cwd. One click on `Always here` / `Always anywhere` persists N `(verb, dir)` pairs at once.
- New: `ShellTokenizer` refuses to extract verb chains from bash control-flow blocks (`for`/`while`/`do`/`done`/`then`/`fi`/`case`/`esac`) or unbalanced quotes/brackets — only `Once` / `Deny` are offered for messy input, with a hint that complex commands cannot persist.
- New: `netclaw approvals trust-verb <verb> [--audience]` CLI command writes a `(verb, null)` entry — the global wildcard. Used both interactively and by the agent at schedule-creation time. `list` and `revoke` updated to label entries as `<verb> in <dir>` or `<verb> anywhere`.
- New agent guidance, three coordinated touch-ups: AGENTS.md instruction to call `set_working_directory` early when working on a project (load-bearing, with consequences spelled out); rewrite of the `set_working_directory` tool description to read as "expand your trust boundary" rather than "set cwd"; shell-tool failure path returns a hint pointing at `set_working_directory` when a call is denied because cwd is outside both safe spaces.
- New schedule-creation flow: when the agent helps the user set up a scheduled task, it identifies the verbs the task needs and proactively suggests pre-approval (`netclaw approvals trust-verb <verb>`) before the schedule fires unattended.
- New eval cases for `set_working_directory` adoption: positive (project-scoped session calls it early), negative (no-project session does NOT call it preemptively), recovery (denied shell call → agent reads hint → calls it on next turn).

## Capabilities

### New Capabilities

None. All changes fit inside existing capabilities.

### Modified Capabilities

- `tool-approval-gates`: replaces the flat-string approval store with a typed `(verb, directory)` model; introduces the safe-verbs ∩ safe-space short-circuit; redesigns the prompt UX to a 5-button row and rewrites the resolution message; adds bash-fragment refusal at extraction time.
- `session-cwd`: shell cwd default falls back to `project_dir` then `session_dir` (was: daemon process cwd); shell-tool failure path returns a `set_working_directory` hint when denial reason is "cwd outside safe spaces".
- `netclaw-cli`: `netclaw approvals trust-verb` subcommand; `list` and `revoke` reflect the v2 entry shape.

## Impact

- **Storage:** breaking schema change. v1 file quarantined to `tool-approvals.json.v1.bak` on first read; users start with an empty v2 store. Existing approvals do NOT carry over.
- **Code:** new `ScopedShellSafeVerbPolicy` (mirrors `ScopedFileAccessPolicy`). Modifications across `Netclaw.Configuration`, `Netclaw.Security`, `Netclaw.Actors.Tools`, `Netclaw.Actors.Protocol`, `Netclaw.Channels.Slack`, `Netclaw.Channels.Discord`, `Netclaw.Cli`. Reuses `ToolAudienceProfileResolver` and the symlink-segment guard.
- **Config:** ships `safe-verbs.linux.json` and `safe-verbs.windows.json` with the daemon; users can override at `~/.netclaw/config/safe-verbs.<os>.json`.
- **Agent identity:** AGENTS.md gains load-bearing guidance about `set_working_directory`; bumps require eval suite to pass (positive + negative + recovery cases).
- **Skills:** `feeds/skills/.system/files/netclaw-operations/SKILL.md` updated for schedule-creation pre-approval flow and the new approval prompt shape.
- **Security:** safe-space short-circuit is gated by safe-verbs list ∩ safe-space root ∩ symlink-free path. Mutation in safe spaces still prompts. Public audience continues to be restricted to `session_dir` only. Hard-deny list unchanged. No change to ACL evaluation order; this only relaxes the interactive approval gate (layer 2) for a narrowly defined set of verb-and-location combinations.
- **Operational:** `netclaw approvals list` + `revoke` semantics change shape. Operators editing the JSON by hand will hit the v1 quarantine flow on the first daemon read after upgrade.
