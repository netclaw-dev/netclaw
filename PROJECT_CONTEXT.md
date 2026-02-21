# Project Context - Netclaw

## What Netclaw Is

Netclaw is a Slack-connected homelab assistant that runs as a .NET 10 host on
owned infrastructure. It is built on top of Akka.Agents, a minimal actor-based
session framework using Akka.NET persistence, pub/sub broadcasts, and
conversation compaction.

## Primary User

- owner-operator running Netclaw on homelab hardware (pi1)
- interacts primarily through Slack
- needs predictable behavior, persistence, and strong safety defaults

## MVP Outcome

Netclaw runs on `pi1`, answers Slack messages in thread, persists session state
across restarts, and compacts long threads without losing working context.

## Product Boundaries (MVP)

In scope:

- single-process host (gateway + actors in same process)
- Slack Socket Mode adapter
- per-thread session actors keyed by `{channelId}/{threadTs}`
- PostgreSQL journal and snapshot persistence
- compaction via summarization reducer
- pub/sub session broadcasts for adapters and future UI subscribers
- default-deny ACL with explicit channel/sender/data grants
- file-based system prompt including opening or zero clause contract
- MCP server integration with policy-gated tool invocation
- minimal operator UX via CLI and management UI mockups/specs

Out of scope (deferred):

- split gateway/agent processes
- sub-agent orchestration and hooks
- web UI implementation (spec/mockup only in this phase)
- telemetry and advanced model capability abstraction layers
- session branching/revert features

## Architectural Decisions to Preserve

- Gall's Law first: simple working system before generalized framework
- Actor transport boundary: adapters consume broadcasts, do not directly drive
  model internals
- Serialization boundary: never persist framework-external chat types directly
- Security boundary: default deny, explicit allow, fail-closed configuration

## Current Phase

Documentation-first planning:

- establish PRDs and engineering specs
- define operator UI/CLI contracts
- bootstrap OpenSpec change planning and baseline capability specs
- then begin implementation through OpenSpec task execution
