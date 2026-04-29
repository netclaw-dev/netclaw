## Context

Sessions currently have no concept of "which project am I working on."
`WorkingContext` tracks only `RecentFiles`. Project-scoped identity files
(`AGENTS.md`, `CLAUDE.md`) are not loaded automatically — the
`netclaw-projects` skill tells the agent to manually `file_read` them, but
that content is ephemeral (lost on compaction and crash recovery).

The immutable session directory (`~/.netclaw/sessions/{sanitized_id}/`)
exists and is used for state isolation (inbox, media, file access scoping).
It is NOT shown to the agent as an explicit path — only `media_dir` appears
in the `[session]` block.

`IContextLayerProvider` with `ContextLayerTiming.EveryTurn` provides the
mechanism for injecting project instructions on every LLM call.

## Goals / Non-Goals

**Goals:**

- Per-session mutable project directory that persists across crash/restart
  via `WorkingContext` in `SessionSnapshot`
- Project-scoped identity files loaded automatically from the project
  directory and injected as a `[project-instructions]` context layer on
  every turn
- `[working-context]` block includes the project directory so the agent
  knows which project it's in
- Session directory path exposed explicitly in the `[session]` block
- Backward compatible: sessions without a project directory work unchanged
- `set_working_directory` tool gated by audience trust profile
- Eval: compare automatic context injection vs behavioral (agent reads file
  itself) for project context loading

**Non-Goals:**

- Tracking shell CWD or defaulting `ProcessStartInfo.WorkingDirectory`
  (separate concern, can be added later)
- Resolving relative file paths against project directory (file tools
  continue to use absolute paths)
- Detecting `cd` commands in shell input to update project directory
- Walking up directory trees to find identity files (project directory
  points directly at the project root)
- Multi-project sessions (one project directory per session)
- File watcher / auto-reload when identity files change on disk (EveryTurn
  re-read is sufficient)

## Decisions

### D1: Two-directory model — session directory vs project directory

**Decision:** Maintain two distinct concepts: immutable session directory for
state isolation, mutable project directory for project context.

| Concept | Mutability | Source | Purpose |
|---------|-----------|--------|---------|
| Session directory | Immutable | Derived from session ID | State isolation: inbox, media, `{session_dir}` token |
| Project directory | Mutable, persisted | Set by `set_working_directory` tool | Project context: identity files, `[project-instructions]` |

**Rationale:** The session directory is a daemon-owned namespace. Mixing
mutable project state into it would break the invariant that session
directories are deterministic from session ID.

### D2: Project directory starts null, not defaulted

**Decision:** New sessions start with `ProjectDirectory` as null. No default.

**Rationale:** The session directory is not a meaningful project root —
it's a daemon-internal staging area. Defaulting the project directory to it
would cause the walker to look for `AGENTS.md` inside
`~/.netclaw/sessions/{id}/`, which is never correct. Null means "no project
selected yet" and the `[project-instructions]` block is simply not emitted.

**Alternatives considered:**
- Default to session directory — rejected because it's not a project.
- Default to workspaces directory — rejected because it's a container for
  projects, not a project itself.

### D3: set_working_directory as standalone tool

**Decision:** Provide a standalone `set_working_directory` tool for setting
the project directory. Profile-managed: not exposed to Public/Team audiences
by default.

**Rationale:** The agent needs to set the project directory from
conversation context — e.g., "go work on the Akadonic project." This
requires a deliberate action, not a side effect of shell navigation.
The tool validates the target is a real directory and is within the
audience's allowed roots.

**Alternatives considered:**
- `set_cwd` parameter on `shell_execute` — rejected because the agent often
  needs to set the project without running a shell command.
- Shell `cd` detection — rejected because navigating within a project
  (`cd src/`) is not the same as switching projects.

### D4: Project identity file loading — single directory, no walk

**Decision:** Load project identity files by checking a fixed set of
candidate filenames at the project root. No directory tree walking.

**Candidate files (checked in order, first match wins):**
1. `.netclaw/AGENTS.md`
2. `CLAUDE.md`
3. `AGENTS.md`
4. `CONTEXT.md`

**Rationale:** The project directory IS the project root. Walking up is
unnecessary — if the agent is working on Akadonic, the project directory
points to `/home/user/workspaces/akadonic/`, and that's where `AGENTS.md`
lives. This eliminates the walker class, audience root boundary logic, and
stop conditions entirely.

### D5: Project instructions in system prompt, not a context layer

**Decision:** Include project identity file content in the system prompt at
position [0] via `SystemPromptAssembler.Assemble()`, alongside the global
SOUL/AGENTS/TOOLING layers. No separate `IContextLayerProvider`. Call
`SetSystemPrompt()` again when the project directory changes.

**Rationale:** The project's `AGENTS.md` serves the same role as the global
`AGENTS.md` — it's identity context the model should always have. Putting it
in the system prompt at position [0] means it sits in the cached prefix,
stable across turns. A project switch busts the cache once, then
re-stabilizes immediately. An `EveryTurn` context layer would put it in
the volatile tail where it's never cached — strictly worse.

This also guarantees the project context is always present regardless of
model capability. Smaller models that might not reliably follow behavioral
instructions ("read AGENTS.md on recovery") get the context for free.
Should be validated via eval against the behavioral alternative.

### D6: Project directory persistence via WorkingContext

**Decision:** Add `ProjectDirectory` as a protobuf field (tag 2) on
`WorkingContext`. Changes are captured in the next `TurnRecorded` event
via `SessionSnapshot`.

**Rationale:** `WorkingContext` already survives compaction by design.
No new persistence event needed.

### D7: Session directory visibility

**Decision:** Add the session directory path to the `[session]` block so
the agent knows its full session directory, not just `media_dir`.

**Rationale:** The agent currently has to infer the session directory from
`media_dir` by going up one level. Making it explicit improves the agent's
situational awareness and allows identity file guidance (in `TOOLING.md`)
to reference it directly.

## Risks / Trade-offs

**[Risk: Cache bust on project switch]** →
Changing the project directory re-runs `SetSystemPrompt()`, which changes
position [0] and invalidates the prompt cache prefix. Mitigation: project
switches are rare, deliberate actions. The cache re-stabilizes immediately
on the next turn.

**[Risk: WorkingContext serialization change]** →
Adding `ProjectDirectory` to `WorkingContext` is a protobuf schema change.
Old snapshots without the field will deserialize with `null`, which is the
correct backward-compat default. No migration needed.

**[Risk: Large project identity files]** →
Project identity files are included in the system prompt at position [0],
which benefits from prompt caching. Large files increase the cached prefix
size but don't cause per-turn overhead. Project identity files are typically
small (under 2K tokens).
