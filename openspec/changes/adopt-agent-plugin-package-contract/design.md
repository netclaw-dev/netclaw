## Context

The five-PR stack adds secure Git acquisition for Codex skill packages.
It also adds durable source state, daemon routes, sync publication, and CLI operations.

The current `GitSkillPluginAcquirer` owns Git transport, archive checks, Codex manifest rules, extraction, content scans, and candidate creation.
The current `ServerFeedSkillSyncService` also owns the managed plugin state machine.

This shape makes a Codex compatibility format the core domain.
It also makes each future package format increase two existing complexity hotspots.

This design uses [the engineering glossary](../../../docs/spec/GLOSSARY.md) for durable state, authority, and skill resource terms.

## Goals / Non-Goals

**Goals:**

- Make Agent Plugins 1.0.0 the primary package contract.
- Keep Codex support as an explicit compatibility format.
- Preserve the current Git and file security controls.
- Keep one actor, scheduler, registry, and inventory refresh path.
- Give the CLI a plugin-level lifecycle contract.
- Isolate package rules from Git transport rules.
- Preserve the previous complete package after a failed update.

**Non-Goals:**

- Add marketplace discovery or registration.
- Add private Git authentication or non-GitHub hosts.
- Activate MCP servers, hooks, LSP configuration, commands, or subagents.
- Execute repository files during acquisition.
- Add a second plugin runtime or a second skill registry.
- Preserve the unshipped `skill plugin` CLI or `/api/skills/plugins` route.

## Decisions

### Use a plugin-neutral source model

`ManagedPluginSource.Id` will identify the operator-managed source.
The source ID will control CLI mutations, configuration entries, durable paths, and receipt keys.

The package manifest will supply `PluginName` after acquisition.
The receipt and list result will expose that name separately.

This split permits a valid source record before the first package download succeeds.
It also prevents a repository-derived alias from replacing the standard manifest identity.

The configuration will keep the existing `SkillFeeds` owner for this slice.
That owner already supplies the external sync interval and config watcher path.
A second plugin configuration root would duplicate this data.

Alternative: Use the manifest name as the source key.
That option requires package acquisition before config persistence and makes a package rename change the lifecycle key.

### Separate package rules from Git transport rules

The Git acquirer will retain reference resolution, HTTP policy, archive limits, safe entry enumeration, selected extraction, content scans, and immutable candidate creation.
Package adapters will own manifest location, schema rules, metadata rules, and skill-root selection.

Schematic flow:

```text
ManagedPluginSource
  -> GitPluginAcquirer resolves one commit and downloads one bounded archive
  -> PluginPackageSelector chooses exactly one package adapter
       -> AgentPluginPackageAdapter
       -> CodexPluginPackageAdapter
  -> adapter returns PluginPackageSelection
  -> acquirer extracts only selected skill trees
  -> scanner validates each selected skill and text resource
  -> acquirer seals one immutable candidate directory
```

`PluginPackageSelection` will contain the package name, optional version, selected skill roots, notices, and excluded component diagnostics.
The type will contain no filesystem authority and no executable directive.

Alternative: Create one acquirer for each package format.
That option duplicates Git transport, archive security, resource limits, and immutable publication logic.

### Select one format without fallback after selection

The supported format values will be `auto`, `agent-plugin`, and `codex`.
New sources will default to `auto`.

Auto selection will use this order:

1. A root `plugin.json` with the recognized Agent Plugins schema.
2. A `.codex-plugin/plugin.json` compatibility manifest.

An explicit format will inspect only that format.
After a format is selected, a validation failure will reject that format.
Netclaw will not fall through to another manifest.

This rule prevents a malformed portable manifest from silently selecting a weaker compatibility contract.

Alternative: Merge declarations from all manifests.
That option creates unclear authority and permits one package view to bypass another package view.

### Apply Agent Plugins failure isolation

An invalid portable manifest will reject the plugin candidate.
An invalid discovered portable skill will produce a diagnostic and will not block valid sibling skills.

A scanner security rejection will still reject the complete candidate.
The scanner decision is a Netclaw publication boundary, not a package syntax decision.

A Codex compatibility candidate will keep its existing whole-candidate validation rule.
Compatibility behavior will not redefine portable behavior.

Unsupported Agent Plugins components will produce diagnostics.
They will not make an otherwise valid package fail.

Alternative: Reject every candidate that contains one invalid skill.
That option conflicts with Agent Plugins failure isolation.

### Keep package versions as metadata

The portable adapter will accept any string value for `version`.
It will not require SemVer.

The existing update policy will compare version strings for exact equality.
If no version exists, the policy will compare commit identities.

The adapter will accept portable names with lowercase letters, numbers, hyphens, and periods.
Source IDs will retain safe portable path rules for managed directories.

Alternative: Require SemVer for every format.
That option conflicts with the Agent Plugins metadata contract.

### Keep one sync actor and add one managed plugin participant

`ServerFeedSkillSyncActor` will continue to own the active pass, waiters, timer, and lifetime token.
The actor will call one external sync coordinator.

The coordinator will retain server-feed processing and call the managed plugin participant.
The plugin participant will return source rows and resolved inventory sources.

The managed plugin participant will own startup publication, update eligibility, durable rejections, alerts, cleanup, and plugin source results.
The coordinator will own the final inventory refresh and complete response.

Alternative: Add a separate plugin actor and timer.
That option duplicates pass coordination and can expose inconsistent inventory snapshots.

### Use a plugin-level CLI and daemon API

The CLI will expose `netclaw plugin install|list|update|enable|disable|remove`.
`netclaw skill sync` will remain the command for the complete external skill sync pass.

The daemon will expose `/api/plugins` for plugin lifecycle operations.
The existing authenticated daemon policy will protect every route.

`plugin update` will request the shared sync pass.
It will then report the selected source result.
`plugin update --all` will report all managed plugin source results.

The CLI will parse safe RFC 9457 problem details from daemon failures.
It will use the status code only when the response has no valid safe detail.

Alternative: Keep `netclaw skill plugin`.
That option makes one supported component define the package lifecycle namespace.

### Preserve authority and state ownership

| Decision or data | Owner | Lifetime |
|---|---|---|
| CLI arguments, confirmation, and wait state | CLI command | Call-local |
| Route authorization and source validation | Daemon API and management service | Call-local |
| Active pass, waiter set, and lifetime token | Sync actor | Actor-local |
| Source configuration | Configuration store | Durable |
| Receipts and rejected commits | Plugin state store | Durable |
| Immutable package revisions | Managed plugin directory | Durable |
| Current accepted skills and prompt index | Inventory refresher | Process-local snapshot |

Manifest metadata cannot grant tool, subagent, shell, MCP, or file authority.
The runtime will expose accepted skills only through existing logical skill tools and audience policy.

## Risks / Trade-offs

- **Risk: A format adapter weakens archive checks.** → The Git acquirer will remain the sole archive and extraction authority.
- **Risk: Portable failure isolation publishes an incomplete skill set.** → The list and sync results will report each skipped skill.
- **Risk: A package rename confuses operators.** → The stable source ID and current manifest name will appear as separate fields.
- **Risk: New API names break a stack consumer.** → The old routes and commands have not shipped, so this change removes them before release.
- **Risk: The coordinator split changes sync order.** → Integration tests will preserve one pass ID, source independence, and one final inventory refresh.
- **Risk: Auto selection hides ambiguity.** → The selector uses fixed precedence and never falls through after selection.
- **Risk: A no-skill portable package appears installed.** → The result will report zero supported skills and all excluded components.

## Migration Plan

1. Update PRD-004, SPEC-004, the glossary, and this OpenSpec change.
2. Rename unshipped configuration, state, API, and CLI contracts to plugin-neutral terms.
3. Add the package selector and the Agent Plugins adapter.
4. Convert Codex rules into a compatibility adapter.
5. Extract the managed plugin sync participant from the server-feed service.
6. Update system skills and website issue #119.
7. Run contract, integration, CLI process, eval, native smoke, and repository checks.

A source revert can restore the old stack because no release contains its config or database schema.
After release, future changes must preserve the plugin source and receipt wire contracts.

## Open Questions

No question blocks the first implementation slice.
Marketplace source ownership remains a later product decision.
