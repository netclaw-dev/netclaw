## Context

`McpClientManager` owns each daemon-scoped MCP client and its immutable tool generation.
It now lists tools at connect time and every five minutes after a successful connection.

`SkillRegistry` owns one atomic file-skill snapshot.
`SkillInventoryRefresher` replaces that snapshot after a file or feed refresh.
`SkillLoadTool` then reads `SkillEntry.FilePath` directly.

MCP prompts have no file path.
They also have server-owned argument descriptors and a server generation.
The design must preserve the current logical skill contract without fake paths.

## Goals / Non-Goals

**Goals:**

- Publish MCP prompts in the existing skill index.
- Load a selected MCP prompt through `skill_load`.
- Preserve one MCP client generation across tools and prompts.
- Preserve file skills during MCP refreshes.
- Preserve MCP prompt skills during file refreshes.
- Filter prompt discovery and use through the existing server grant.
- Validate prompt arguments before the remote request.
- Keep all failures visible.

**Non-Goals:**

- Add MCP resources.
- Add proactive MCP subscriptions.
- Add the MCP completion API.
- Add an HTTP skill catalog.
- Add TUI completion.
- Add human slash invocation for MCP prompts in this slice.
- Add a prompt-specific permission or model tool.

## Decisions

### Use a typed skill source

`SkillEntry` will use a required source variant.

The file source will contain `FilePath` and `SkillDirectory`.
The MCP source will contain the server, prompt, generation, and argument descriptors.

This model prevents a remote skill from carrying a fake path.
It also forces each consumer to select the correct source path.

The alternative kept nullable file and MCP fields on one record.
That model permits invalid combinations and hides missing source checks.

### Keep source inventories in one registry snapshot

`SkillRegistry` will own a file inventory and one MCP prompt inventory for each server.
Each source update will rebuild one combined immutable snapshot under the current lock.

A file refresh will replace only the file inventory.
An MCP refresh will replace only that server's prompt inventory.

File skills will win a logical-name collision.
The MCP manager will log each collision.

The alternative let the file refresher replace all entries.
That path would erase remote prompts after any skill edit or feed refresh.

### Extend the existing MCP generation

`McpServerSnapshot` will contain prompt descriptors beside tool functions.
The connect path will list prompts only when the server declares prompt support.

The poll path will list tools and prompts as one candidate catalog.
It will publish both only after all required list calls succeed.

The combined fingerprint will include prompt names, titles, descriptions, and arguments.
An unchanged catalog will not publish a new generation.

The alternative used an independent prompt generation.
That model could bind one skill descriptor to a different client than its tools.

### Use one source-aware prompt loader interface

`SkillLoadTool` will keep file loads local.
It will delegate an MCP source to `IMcpPromptSkillLoader`.

`McpClientManager` will implement this interface.
It already owns the client, generation, reconnect logic, and MCP server configuration.

The interface will return role-tagged text messages without MCP SDK types.
This boundary keeps the actor tool independent from MCP protocol content classes.

The alternative injected `McpClientManager` into the actor project.
That path would reverse the current project dependency.

### Keep argument support specific to prompt loads

`skill_load` will add an optional `Arguments` string map.
File skills will reject a non-empty map.

An MCP load will reject unknown arguments.
It will also reject a missing required argument.
The adapter will pass accepted values to `prompts/get` without inference.

This change does not define general Agent Skills argument metadata.

### Publish one index for each non-Public audience

The context layer will store separate Team and Personal index values.
A shared publisher will build both values from the same registry snapshot.

The publisher will use `ToolAccessPolicy` for MCP server visibility.
File skills will retain their current audience behavior.

`SkillLoadTool` will pass the turn context to the MCP loader.
The loader will apply the same server check before `prompts/get`.

The alternative used one shared non-Public index.
That path could reveal a Personal-only server name to a Team session.

### Return attributed text as a tool result

The loader will preserve each MCP message role.
It will return the prompt server, prompt name, and generation.

This slice will support text content blocks.
It will fail visibly for any unsupported content block.

The result remains a normal tool result.
It does not enter the system-message authority level.

### Keep MCP prompts model-invocable only

An MCP prompt entry will set `UserInvocable` to false in this slice.
The model can discover and load it through the normal skill index.

Issue #1809 will define the common human-invocation descriptor.
Issue #1811 will add TUI completion and argument help.

## Actor and Persistence Boundaries

No actor message or persisted event changes.
The skill catalog remains daemon memory.

The session actor receives the skill index through the existing context layer.
The tool executor invokes `skill_load` through the current actor-independent tool path.

## Failure and Recovery

- A prompt list failure keeps the last good MCP and skill generations.
- An empty prompt list is a valid prompt catalog.
- A stale skill generation returns an explicit error.
- A missing server grant returns the generic denied result.
- A missing or unknown argument stops before `prompts/get`.
- An unsupported content block returns an explicit error.
- A transport failure follows the current reconnect-without-replay rule.
- A file refresh cannot remove a published MCP prompt inventory.
- An MCP refresh cannot remove a file skill inventory.

## Risks / Trade-offs

- [Risk] A session keeps its start-time index after a remote catalog changes. -> A stale load fails visibly, and a new session receives the new index.
- [Risk] A remote prompt can contain long text. -> The shared tool dispatcher will bound and spill the rendered result.
- [Risk] A prompt can reference tools that the audience cannot use. -> Normal tool discovery and invocation gates remain authoritative.
- [Risk] A prompt can use non-text content. -> The first slice fails visibly instead of dropping content.
- [Risk] The prompt and skill registries publish in two lock domains. -> The generation check rejects any short stale window.

## Migration Plan

1. Add the typed source and source-aware registry behavior.
2. Add per-audience skill index publication.
3. Extend the MCP snapshot and catalog fingerprint.
4. Add prompt load support to `skill_load`.
5. Add smoke, focused, and behavioral proof.

No persisted data migration is necessary.
A rollback removes remote prompt entries and restores file-only skill behavior.

## Open Questions

No open question blocks this slice.
Issue #1808 will select the exact `subscriptions/listen` adapter later.
