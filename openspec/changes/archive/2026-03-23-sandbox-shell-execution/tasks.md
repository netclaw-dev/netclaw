## 1. Configuration and runtime contracts

- [ ] 1.1 Add sandbox shell configuration types, option binding, and schema validation for backend kind, runtime/image settings, scratch workspace root, and default isolation options.
- [ ] 1.2 Introduce a daemon-owned sandbox runner abstraction and structured execution result model that the shell tool can call without embedding backend-specific logic.
- [ ] 1.3 Add startup/runtime validation for sandbox backend prerequisites so `sandbox-only` is treated as unavailable when validation fails.

## 2. Sandboxed shell execution

- [ ] 2.1 Implement the first sandbox backend using the selected container runtime, including isolated launch, timeout enforcement, stdout/stderr capture, and failure categorization.
- [ ] 2.2 Implement per-invocation workspace staging, explicit mounts for project/session paths, dedicated writable scratch output, and deterministic cleanup of artifacts.
- [ ] 2.3 Route `shell_execute` through the sandbox backend when shell mode resolves to `sandbox-only`, preserving `host-allowed` behavior and forbidding automatic fallback to host execution.

## 3. Diagnostics and operator surfaces

- [ ] 3.1 Update `netclaw doctor` to validate sandbox backend health and emit remediation-first failures when `sandbox-only` is configured but unusable.
- [ ] 3.2 Update `netclaw status` and related diagnostics output to show active shell mode plus sandbox backend health/details when applicable.
- [ ] 3.3 Update onboarding/config reference and operator docs to explain sandbox shell prerequisites, default network isolation, and no-fallback behavior.

## 4. Validation

- [ ] 4.1 Add unit tests for shell mode routing, sandbox validation failures, and no-fallback authorization/execution behavior.
- [ ] 4.2 Add integration tests for successful sandbox execution, timeout handling, isolated mount behavior, and cleanup of ephemeral artifacts.
- [ ] 4.3 Run `dotnet slopwatch analyze` and any affected smoke/integration checks to verify the new sandbox shell path does not introduce quality regressions.
