# CLI execution context: local verification

This is a structural refactor for `pair`, `update`, and `approvals`.
The context contains paths, clock, input, output, and error streams.
The caller owns streams. Inner services retain their specific dependencies.
No public CLI option, exit code, configuration shape, or stored-data format changes.

## Local evidence

The initial implementation used `upstream/dev` at `aa5fc525a`.
These results apply to implementation commit `6ad420a3d`:

- All 172 focused command, context, and host tests passed, with no skips.
- All 2,034 CLI tests passed with an isolated `NETCLAW_HOME`, with no skips.
- The native light smoke harness passed 27 tapes and ten daemon scenarios.
- Slopwatch reported zero new issues. Copyright headers and the diff check passed.

The operator environment exposed one existing wizard test failure.
The unchanged base fails `RunWithOrchestrator_SupervisorMarkerSetButNoSupervisor_SurfacesActionableReason` too.
The test's default systemd probe sees the host's active unit instead of its fake supervisor.
The isolated home removes that host dependency. This refactor does not change the wizard test.

The update tests verify HTTP and checksum failures before daemon access.
They also verify the supplied home and clock, foreign-unit refusal, and binary rollback.
The approval tests verify canonical grants, diagnostics, and the supplied timestamp.
The pair tests retain endpoint rejection, bounded responses, virtual deadlines, and caller cancellation.
No authorization gate or owner rule changes. Existing focused Stryker targets remain unchanged.

Behavioral evals are not required for this CLI refactor, per the operator's instruction.
The coverage attempt passed 61 update tests, but no coverage collector was available.
No OpenCover report or CRAP score exists.

Other command migrations and shared path construction remain outside this slice.

## Validation after the rebase

The branch now uses `upstream/dev` at `b8f5cfaa4`.
The range comparison shows no code change from the original implementation.
Only the operations skill version differs. The version preserves the newer upstream release.

- All 2,105 CLI tests passed with an isolated `NETCLAW_HOME`, with no skips.
- The native refresh passed the `help`, `init-wizard`, and `approvals` tapes and the `pairing` scenario.
- Slopwatch reported zero issues. Copyright headers and the diff check passed.

The full native suite result above predates the rebase.
The refresh uses the rebased code. The rebase retains the upstream retention feature.

## Test review after feedback

The source guard test was removed. No production code changed.
The review found no other self-confirming assertions among the tests changed by this PR.
The factory assertions check the supplied dependencies, not production daemon construction.
The other assertions inspect command results, files, timestamps, and daemon calls.

- All 2,090 CLI tests passed with an isolated `NETCLAW_HOME`, with no skips.
- Slopwatch reported zero issues. Copyright headers and the diff check passed.
