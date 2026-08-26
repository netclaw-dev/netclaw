## 1. Specification

- [x] 1.1 Land this change's artifacts on `spec/mcp-tool-outcome-receipts`; verify `openspec validate mcp-tool-outcome-receipts --strict` passes.

## 2. Receipts and one Warning log (#2055)

- [x] 2.1 Make `McpToolAdapter.ExecuteAsync` return its error string through `ToolOutcomeResults` with the category from design D1 (401/403 → `AccessDenied`, 404 → `NotFound`, else `TransientFailure`); leave `ExecuteViaBoundToolAsync` unchanged; verify adapter tests that use the `RecordingMcpToolInvoker` fake for HTTP 500, HTTP 403, and a plain `McpException`; each test also asserts that the result text names the tool.
- [x] 2.2 Make `McpClientManager.InvokeSharedAsync` follow design D2: tool lookup outside the `try`, one catch that logs a redacted Warning with server, tool, and HTTP status, rethrow of non-transport exceptions, and no log on caller cancellation; verify lifecycle tests that a thrown `McpException` reaches the caller with no reconnect, that `harness.Logger.Entries` holds one Warning that names the server and the tool, and that a cancelled call adds no Warning.
- [x] 2.3 Update the `application MCP failure` assertion in `McpClientManagerLifecycleTests` to expect the exception; run `McpToolResultFormatterTests` and `McpClientManagerLifecycleTests`; verify the tool-declared `isError` path does not change.

## 3. Reconnect classification (#2056)

- [x] 3.1 Make `IsTransportOrSessionFailure` return true for `HttpRequestException` only when `StatusCode` is null or 404; verify lifecycle tests: HTTP 500 and 429 produce no reconnect and an unchanged generation; HTTP 404 and a status-less failure each produce one reconnect.
- [x] 3.2 Widen the first catch clause in `McpClientManager.LoadAsync` to `McpException` or `HttpRequestException` when not a transport failure; verify a `McpPromptSkillTests` test that an HTTP 500 on `GetPromptAsync` returns a failed load result that names the prompt, with no reconnect.

## 4. Auth guard for servers that cannot use OAuth (#2057)

- [ ] 4.1 Make `ReportToolFailure` call `MarkToolAuthFailure` only when `HasOAuthRuntimeHints(serverName, entry)` is true; add a lifecycle harness overload that accepts an `McpServerEntry`; convert `ToolLevelAuthFailure_MovesServerOutOfConnected` to an HTTP entry without an `Authorization` header; verify new lifecycle tests that a static-header HTTP server and a stdio server each stay `Connected` after an `isError` result with auth words, with the Warning present in `harness.Logger.Entries`.

## 5. Validation and documentation

- [ ] 5.1 Run `dotnet test` for `Netclaw.Actors.Tests` (Tools) and `Netclaw.Daemon.Tests` (Mcp), `dotnet slopwatch analyze`, and `./scripts/Add-FileHeaders.ps1 -Verify`; verify no new violations.
- [x] 5.2 Add the new Warning line to the diagnostics table in `feeds/skills/.system/files/netclaw-operations/references/diagnostics.md` and bump the `netclaw-operations` skill version; verify the row and the version change.
- [ ] 5.3 Run an adversarial review on each stacked pull request before it opens; verify every finding has a NOW, PARK, or FIX INLINE disposition recorded in the pull request.
- [x] 5.4 Edit issue #2055 so its Expected section matches design D2 (the `Tool executed:` line stays); verify the issue body no longer requires that line to be absent.
- [x] 5.5 Edit issue #2056 to remove the `Retry-After` expectation and to add the prompt-load path; verify the body matches design D3.
- [x] 5.6 Edit issue #2057 to state the `HasOAuthRuntimeHints` gate, the retained heuristic for OAuth-capable servers, and the absence of 401/403 remedy text; verify the body matches design D4.
