## 1. Decouple MCP Teardown From Actor-System Shutdown

- [x] 1.1 Add an `IHostApplicationLifetime.ApplicationStopping` hook that triggers MCP client/child-process teardown independent of `IHostedService` stop ordering.
- [x] 1.2 Refactor `McpClientManager` teardown into a single memoized `TeardownAsync()` operation shared by the `ApplicationStopping` callback, `StopAsync`, and `Dispose`, so a second caller awaits the same completed work instead of re-disposing or racing it.
- [x] 1.3 Add a `_stopping` flag set synchronously when teardown begins, consulted by `ConnectAsync` and `TryReconnectAsync` so neither the tool-call retry-after-failure path nor `McpReconnectionService`'s periodic poll can create a new client or child process once teardown has started.
- [x] 1.4 Change per-server disposal in `TeardownAsync()` from sequential `foreach` + `await` to a parallel `Task.WhenAll` projection, preserving today's per-server try/catch so one server's disposal failure doesn't block or delay others.

## 2. Automated Proof

- [x] 2.1 Extend `McpProcessBoundStdioTests.cs` (using the real-process `McpSmokeHarness`) with a test proving double-teardown (`ApplicationStopping` hook followed by `StopAsync`) disposes the underlying child process exactly once, without a spurious warning on the second call. (Landed as a new sibling file `McpShutdownTeardownTests.cs` reusing `McpSmokeHarness`, rather than growing the unrelated process-ownership test file — also exercises a third entry point, `Dispose()`, in the same test.)
- [x] 2.2 Add a test proving that once teardown has started, a subsequent `TryReconnectAsync` call does not launch a new child process.
- [x] 2.3 Add a test proving multiple configured servers tear down concurrently: total teardown wall-clock time is bounded by the slowest single server's dispose, not the sum across servers.
- [x] 2.4 Add a test proving an in-flight tool call against a server whose client is disposed mid-call surfaces a clean, attributed tool error (via the existing `McpToolAdapter.ExecuteAsync` catch path) rather than hanging past the client's own dispose timeout.
- [x] 2.5 Run targeted MCP tests and the full `Netclaw.Daemon.Tests` project.

## 3. Guidance and Quality Gates

- [x] 3.1 Update `netclaw-operations` guidance to note that MCP child-process teardown now starts at application-stopping (concurrent with session drain) rather than after actor-system shutdown, and that in-flight MCP tool calls fail fast during shutdown; bump the skill version.
- [x] 3.2 Confirm the eval suite is not applicable because this change does not alter production tool definitions, skill matching, prompts, or model behavior.
- [x] 3.3 Run OpenSpec validation, Slopwatch, file-header verification, and `git diff --check`.
