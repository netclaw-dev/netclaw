# Design: Define Netclaw MVP Foundation

## Context

The repo currently contains a .NET build skeleton. Product behavior and
architecture contracts need to be documented before implementation starts.

## Goals / Non-Goals

Goals:

- capture session, ACL, security, and Slack transport behavior in OpenSpec
- ensure PRD and OpenSpec traceability
- keep MVP constraints explicit and enforceable

Non-goals:

- implementing runtime code in this change
- introducing north-star architecture features

## Decisions

### Decision 1: Slack Socket Mode is the baseline transport

Use Slack Socket Mode for MVP to avoid requiring inbound public HTTP for core
messaging flow.

### Decision 2: Security defaults are enforced at planning layer

Define default-deny and fail-closed requirements now to prevent permissive
implementation drift.

### Decision 3: Actor transport boundary remains pub/sub oriented

Adapters dispatch commands and consume broadcasts; session actors stay transport
agnostic.
