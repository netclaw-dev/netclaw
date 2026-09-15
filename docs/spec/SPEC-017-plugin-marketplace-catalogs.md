# SPEC-017: Plugin Marketplace Catalogs

Source PRDs: `PRD-004` (CLI-015), `PRD-002`

This spec extends [SPEC-004](SPEC-004-cli-contract.md).
Use the [engineering glossary](GLOSSARY.md) for shared terms.
Issue [#2185](https://github.com/netclaw-dev/netclaw/issues/2185) tracks this capability.

## Product Boundary

The Agent Plugins 1.0.0 standard defines a package, not a catalog.
Netclaw uses a subset of the [Claude marketplace catalog shape](https://code.claude.com/docs/en/plugin-marketplaces) for the first catalog release.
This choice does not make Netclaw a Claude Code plugin runtime.
Package import still selects Agent Plugins 1.0.0 before host compatibility formats.
Catalog registration is an operator choice, not a curated trust mark.

The first release reads `.claude-plugin/marketplace.json` from a public GitHub repository.
The catalog file supplies a name and a `plugins` array.
Each supported entry supplies a name and a source.
The daemon records the catalog name as metadata.
The operator's marketplace ID remains the durable command key.

The supported source forms are a contained `./` path in the catalog repository and a public GitHub source object.
A GitHub source object uses `"source": "github"`, supplies `repo`, and can supply an exact `sha`.
An object without `sha` follows the repository default branch.
An object with `ref` remains unsupported in this release because a ref can name either a branch or a tag.
Other source forms remain visible as unsupported entries with explicit notices.
The install command rejects an unsupported entry before it writes a plugin source.

For example, `./plugins/review` is a contained catalog path.
The daemon rejects `../review` because it escapes the catalog root.
For example, a GitHub object with `repo` and `sha` can select an exact commit.
An object with `ref` reports an unsupported source notice and cannot install.
For example, `review@team` selects one entry in the `team` catalog.
The daemon rejects an unqualified `review`, even when only one catalog contains it.

## Authority and Durable State

The daemon owns marketplace ID validation, GitHub reference resolution, catalog checks, and all durable writes.
The CLI owns only the authenticated request, confirmation, and result presentation.
The sync actor owns one active pass and queued incompatible requests as actor-local state.
The catalog participant owns call-local fetch and parse state.

`SkillFeeds.Marketplaces` stores public catalog source configuration.
The schema must accept its canonical fields and reject unknown fields.
The daemon stores one atomic validated snapshot per marketplace under a managed path.
The snapshot records its source ID, Git commit, catalog name, entries, and notices.
The daemon reads only the last validated snapshot for search and show.

An installed catalog plugin remains a `ManagedPluginSource` with a distinct source ID.
The source stores its marketplace ID and entry name as optional origin metadata.
The catalog entry name remains separate from the installed package manifest name.
The package receipt remains the source of truth for the installed revision.
A catalog refresh never rewrites an installed source location or package receipt.
A marketplace removal leaves installed sources intact.

For example, catalog registration stores a catalog snapshot but creates no package receipt.
A catalog entry cannot grant a shell tool, MCP server, hook, or subagent authority.
The daemon reports ignored executable catalog fields as notices.
The installed package path still passes Git acquisition, archive checks, and the content scanner.

## Ordered Command Flow

This flow is schematic. It omits ordinary transport setup and the final response format.

```text
marketplace add -> daemon authorization
  validate marketplace ID and public GitHub repository
  fetch one bounded Git revision
  validate marketplace.json and each source path
  write an atomic snapshot
  persist the canonical source configuration
  return catalog status; publish no skill

catalog install -> daemon authorization -> durable snapshot lookup
  require one qualified and installable entry
  resolve its public GitHub package source
  persist the managed plugin source with catalog origin
  wait for the daemon to apply the new source configuration
  request a source-scoped pass through the existing sync actor
  acquire, scan, publish, and verify only that package

skill sync -> existing sync actor -> complete pass
  sync each enabled private skill feed
  sync each enabled installed Git plugin
  refresh each registered catalog snapshot
  refresh the skill inventory once after package work
  report each source result and the inventory result
```

Marketplace add fetches and validates the catalog before source configuration persistence.
An invalid catalog does not enter `SkillFeeds.Marketplaces`.
A snapshot write failure returns an error and prevents source configuration persistence.
An unused snapshot cannot grant authority if a later config write fails.

## Pass Scope and Failure Rules

`netclaw skill sync` requests the complete pass.
`netclaw plugin update --all` requests a plugin-only pass.
A named plugin command requests a source-scoped package pass.
`netclaw plugin marketplace update <id>` requests a catalog-only pass.

The actor coalesces requests only when their scopes and retry policies match.
It queues an incompatible request behind the active pass.
A named plugin request never joins a complete pass as its own result.
The actor keeps one scheduler, one pass owner, and one final inventory owner.
Catalog-only work does not refresh the skill inventory.

A failed catalog refresh reports a catalog source failure and keeps the last valid snapshot.
It does not stop private feed or installed package work in a complete pass.
The command does not silently present a stale snapshot as current.
An initial add with no valid snapshot fails before catalog configuration persistence.

For example, a catalog HTTP 500 leaves a prior catalog commit available for search.
The source row reports the HTTP failure and the prior snapshot revision.
A malformed JSON update also keeps the prior snapshot but reports validation failure.

The result source kind distinguishes `server-feed`, `git-plugin`, and `catalog`.
Catalog rows report the snapshot commit and entry count without changing existing count meanings.
The wire response adds catalog fields and preserves feed and plugin fields.

## Implementation Sequence and Proof

1. Add canonical marketplace config, schema fields, paths, and snapshot checks.
2. Add a bounded public GitHub catalog reader that reuses package transport and archive checks.
3. Add a catalog participant and explicit request scopes to the existing sync actor.
4. Add authenticated catalog routes and a thin CLI presentation layer.
5. Add qualified catalog install and source origin metadata.
6. Update the operational system skill, CLI help, website issue, and relevant eval cases.

Tests must prove invalid source data fails before config persistence.
Tests must prove a persisted package source matches the runtime path and reference rules.
Config load and save tests must preserve marketplace sources and installed package origins.
Actor tests must prove incompatible requests do not coalesce.
Integration tests must prove a catalog failure does not block a healthy feed.
Smoke tests must prove add installs nothing and explicit install publishes one package.
