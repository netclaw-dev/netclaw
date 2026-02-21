# Design: Guided Onboarding and Provider Strategy

## Context

Netclaw MVP depends on fast, safe first-run setup. OpenRouter is preferred as
default provider, but provider selection should remain extensible.

## Goals / Non-Goals

Goals:

- define a stepwise onboarding contract with resume support
- define provider abstraction requirements independent of actor internals
- preserve secure defaults during setup

Non-goals:

- implementing provider failover algorithms
- implementing all provider integrations in this planning change

## Decisions

### Decision 1: Guided onboarding is the default initialization path

`netclaw init` uses an interactive wizard by default and supports non-interactive
flags for automation.

### Decision 2: OpenRouter is default, not exclusive

Onboarding defaults to OpenRouter while requiring provider-agnostic runtime
contracts.

### Decision 3: Onboarding persists progress

Partial setup is resumable to reduce friction and avoid repeated credential
entry.
