# SPEC-003: ACL Policy and Security Controls

Source PRDs: `PRD-001`, `PRD-002`

## Status

This specification is a pointer. Its earlier policy model (grant categories,
ambient channel mode, and a tool audit store) no longer describes Netclaw.

Use these two sources:

- [Tool authorization architecture](../architecture/tool-authorization.md)
  explains how Netclaw authorizes every tool call. It has diagrams, decision
  owners, guidelines, and future scenarios.
- [`tool-authorization` OpenSpec capability](../../openspec/specs/tool-authorization/spec.md)
  states the testable rules and maps each rule to its tests.

Inbound sender and channel rules belong to the channel and input-adapter
specifications, for example
[`netclaw-input-adapters`](../../openspec/specs/netclaw-input-adapters/spec.md).
