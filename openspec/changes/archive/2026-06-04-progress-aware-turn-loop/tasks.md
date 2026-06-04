## 1. Configuration and schema

- [x] 1.1 Replace `MaxToolCallsPerTurn` with `MaxToolIterationsPerTurn` in
  `SessionConfig`, `RawSessionConfig`, and configuration binding.
- [x] 1.2 Update `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json`
  to remove `MaxToolCallsPerTurn` and add `MaxToolIterationsPerTurn` with the
  chosen default.
- [x] 1.3 Update config docs/comments that still describe per-turn governance in
  terms of raw tool-call count.

## 2. Turn-loop accounting

- [x] 2.1 Change turn-loop enforcement to use iteration count instead of raw
  tool-call count.
- [x] 2.2 Define one LLM response that issues one or more parallel tool calls as
  a single iteration.
- [x] 2.3 Preserve the existing force-no-tools completion behavior when the
  iteration cap is reached.

## 3. Tests and verification

- [x] 3.1 Add or update tests proving one parallel tool batch counts as one
  iteration.
- [x] 3.2 Add or update tests proving multiple separate tool rounds count as
  multiple iterations.
- [x] 3.3 Add or update tests proving the turn stops tool-enabled looping when
  `MaxToolIterationsPerTurn` is reached.
- [x] 3.4 Add or update config/schema tests for `MaxToolIterationsPerTurn` and
  rejection of stale `MaxToolCallsPerTurn`.
- [x] 3.5 Run the relevant build and test suite for the turn loop and
  configuration changes.
- [x] 3.6 Run `/opsx-verify` to confirm the implementation matches the change
  artifacts before sync/archive.
