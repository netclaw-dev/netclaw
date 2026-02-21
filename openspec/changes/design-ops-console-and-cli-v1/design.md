# Design: Ops Console and CLI v1

## Context

Netclaw requires an operations-first management experience but MVP prioritizes
documentation and contract definition before implementation.

## Goals / Non-Goals

Goals:

- define a dense ops-console IA with security visibility
- define CLI commands for onboarding, validation, and diagnostics
- keep parity between UI operations and CLI equivalents

Non-goals:

- delivering production web UI code in this change
- implementing all CLI command handlers

## Decisions

### Decision 1: UI delivery stack target is Blazor Server

Specify ASP.NET Core + Blazor Server for in-process management UI delivery to
minimize runtime complexity.

### Decision 2: CLI remains the authoritative operational interface

Every critical UI workflow has a CLI equivalent so operators can recover from
UI outages.

### Decision 3: Security posture must remain visible in top-level UI

Exposure mode and policy deny signals appear prominently in overview and
security routes.
