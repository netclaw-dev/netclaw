## Context

Use the [Netclaw engineering glossary](../../../docs/spec/GLOSSARY.md) for tool call, dispatcher, tool result, tool receipt, outcome category, authority, transport or session failure, application error, tool-declared error, and OAuth-capable server. This change adds the last four terms to the glossary. See proposal.md for motivation.

The real examples below come from a link-shortener MCP server (`shortio`) observed on 2026-08-26. The server never returned HTTP 5xx. It returned tool-declared errors with the text `Internal Server Error`, HTTP 429 under a 30-requests-per-minute budget, and HTTP 401 for a bad key.

Current flow at `dev` (`94d2bfce`) for an MCP tool call that ends in an exception. This pseudocode is schematic. It omits authorization, redaction, and the output bound.

```text
MCP server or transport throws
  -> McpClientManager.InvokeSharedAsync
       McpException       -> return "Error: MCP tool '<server>/<tool>' failed: <message>"  (no log)
       transport failure  -> log at Debug, reconnect, rethrow
  -> McpToolAdapter.ExecuteAsync
       catch-all          -> return "Error: MCP tool '<name>' failed: <message>"           (no log, no receipt)
  -> DispatchingToolExecutor
       string result      -> receipt category = success
                          -> log "Tool executed: ..." at Information
  -> SessionToolExecutionPipeline
       receipt            -> log "Tool outcome category=Success" at Information
```

Constraints that shape the approach:

- `INetclawTool.ExecuteAsync` returns a string. The public contract stays.
- The tool receipt is first-writer-wins (`ToolExecutionOutputs.TryComplete` uses `CompareExchange`). A tool that completes the receipt before it returns wins over the dispatcher's `success` completion.
- `ToolOutcomeResults` (#2033) already attaches a category to a result string. The workspace tools use it. Its one-argument helpers need no file activity and no remediation code.
- `McpClientManager.IsTransportOrSessionFailure` is the single predicate that decides a reconnect. It has two consumers: the tool path (`InvokeSharedAsync`) and the prompt-skill path (`LoadAsync`).
- `McpClientManager.ReportToolFailure` owns the tool-declared error path and its Warning log.
- `McpClientManager.HasOAuthRuntimeHints(serverName, entry)` already decides whether a server is OAuth-capable.
- In the SDK (`ModelContextProtocol.Core` 2.2.0), `McpException` carries no error code. Only the derived `McpProtocolException.ErrorCode` does, and the SDK reserves `-32602` for a malformed request or an unknown primitive name. Tool input validation arrives as a tool-declared error, not as this exception.
- The existing test `ToolLevelAuthFailure_MovesServerOutOfConnected` documents the expired-token case: the token failure reaches the agent as a tool-declared error, not as an HTTP 401. Its harness uses a stdio entry today.
- Production always passes the manager as the adapter's invoker (`PrepareMcpTools`). The adapter's bound-tool path is unreachable in the daemon.

## Goals / Non-Goals

**Goals:**

- One converter from exception to tool result. The adapter owns it.
- One receipt per failed call with a category the actor can trust.
- One Warning line per failed call in the daemon log.
- Reconnect only for a transport or session failure.
- No false `AuthFailed` for a server that is not OAuth-capable.
- The fewest moving parts. Reuse existing helpers. Add no new type, interface, or configuration.

**Non-Goals:**

- Change the model-facing text shape of an MCP failure.
- Retry, replay, or add a per-server request budget.
- Propagate `Retry-After` from the SDK exception. The SDK exception carries no headers.
- Map JSON-RPC error codes to categories.
- Change the receipt category of a tool-declared error.
- Change the connect-path auth classification (#1908 covers it).
- Change the adapter's bound-tool path.

## Proposed flow

This pseudocode is schematic. It omits authorization, redaction, and the output bound.

```text
MCP server or transport throws
  -> McpClientManager.InvokeSharedAsync
       caller cancellation            -> rethrow, no log
       any other exception            -> log one Warning (server, tool, HTTP status when present, redacted exception)
         transport or session failure -> reconnect for later calls, rethrow
         application error            -> rethrow, no reconnect
  -> McpToolAdapter.ExecuteAsync
       caller cancellation            -> rethrow
       any other exception            -> receipt category per D1, return "Error: MCP tool '<name>' failed: <message>"
  -> DispatchingToolExecutor
       string result                  -> receipt already complete; "Tool executed: ..." unchanged
  -> SessionToolExecutionPipeline
       receipt                        -> log "Tool outcome category=<category>"
```

## Decisions

### D1. The adapter completes the receipt from the caught exception

`McpToolAdapter.ExecuteAsync` keeps its catch-all. Instead of a bare `return`, it returns the same error string through `ToolOutcomeResults`:

```text
HttpRequestException 401 or 403  -> context.AccessDenied(text)
HttpRequestException 404         -> context.NotFound(text)
anything else                    -> context.TransientFailure(text)
```

The receipt is call-local. Nothing durable changes. Every receipt consumer today gates on `success`, so a non-success receipt has no state effect. The category becomes visible in the pipeline's `Tool outcome category=` Information line, which today prints `Success` for a failed MCP call.

The bound-tool path (`ExecuteViaBoundToolAsync`) stays as it is. The daemon never uses it.

Positive example (observed HTTP 429 after the 30-per-minute budget):

```text
tools/call -> HTTP 429
              {"statusCode":429,"error":"Too Many Requests","message":"Rate limit exceeded, retry in 52 seconds"}
  result:  "Error: MCP tool 'shortio/get-domains' failed: Response status code does not indicate success: 429 (Too Many Requests). Response body: {"statusCode":429,...}"
  receipt: category = transient_failure
```

Positive example (observed HTTP 401 for a key with a `Bearer ` prefix the server rejects):

```text
tools/call -> HTTP 401
              {"jsonrpc":"2.0","error":{"code":-32000,"message":"Unauthorized: No API key provided"},"id":null}
  result:  "Error: MCP tool 'shortio/get-domains' failed: ... 401 (Unauthorized) ..."
  receipt: category = access_denied
```

Negative example (observed tool-declared error; `search-links` with only its required `domainId`):

```text
tools/call -> HTTP 200
              {"result":{"content":[{"type":"text","text":"Internal Server Error"}],"isError":true},"jsonrpc":"2.0","id":70}
  result:  "Error: MCP tool 'shortio/search-links' reported a failure: Internal Server Error"
  receipt: unchanged by this change
```

Alternatives:

- Let the exception reach the dispatcher's classification branch. Rejected. It changes the model-facing text, drops the tool name prefix, and reopens the `HttpClient` timeout versus caller cancellation distinction that the adapter's filter exists for.
- Return a typed result object from `IMcpToolInvoker`. Rejected. It adds a type and an interface change for one consumer.
- Map `McpProtocolException.ErrorCode == InvalidParams` to `invalid_input`. Rejected. The SDK uses that code for a malformed request or an unknown tool name, not for tool input. One more branch for an ambiguous meaning.

### D2. The manager rethrows non-transport exceptions and logs once

`McpClientManager.InvokeSharedAsync` no longer converts `McpException` to a string. The adapter is then the only converter. The tool lookup that throws `CreateUnavailableException` moves out of the `try` block, so the manager's own exception does not log as an invocation failure.

```text
try
  return InvokeFunctionAsync(...)
catch OperationCanceledException when ct.IsCancellationRequested
  rethrow                                   (no log, no reconnect)
catch Exception ex
  log Warning: server, tool, HTTP status when ex is HttpRequestException with a status, RedactForLogging(ex)
  if not IsTransportOrSessionFailure(ex): rethrow
  transportFailure = ex
reconnect for later calls; rethrow transportFailure
```

Both branches log Warning. This matters for delivery order: the #2055 pull request lands before D3, and an HTTP 500 is still a transport failure at that point. The Debug line in `ReconnectAfterTransportFailureAsync` stays.

Real log line for the 429 example above:

```text
[WRN] Netclaw.Daemon.Mcp.McpClientManager: MCP tool 'shortio/get-domains' invocation failed (HTTP 429)
```

An `HttpClient` timeout surfaces as `TaskCanceledException` with the caller token not cancelled. It logs Warning, is not a transport failure, and reaches the adapter as `transient_failure`.

The dispatcher's `Tool executed:` line stays unchanged. It reports duration and size. The Warning line is the failure signal.

Alternative: log in the adapter. Rejected. The adapter has no logger and no server context.

### D3. The classifier reads the HTTP status, on both consumers

`IsTransportOrSessionFailure` returns true for `HttpRequestException` only when `StatusCode` is null or `404`. Every other status is an application error, as the glossary defines it. The other branches of the predicate stay as they are.

Tool path: the manager rethrows an application error without a reconnect, the generation does not change, and the adapter maps it per D1.

Prompt-skill path (`LoadAsync`): today its first catch clause accepts only `McpException`. After D3 an `HttpRequestException` with an application status would match neither clause and escape to the dispatcher. The first clause widens to `McpException` or `HttpRequestException` when not a transport failure, and returns the existing `McpPromptSkillLoadResult.Failed(...)` text. No new clause, no new type.

Positive example (application error, no reconnect; observed):

```text
tools/call -> HTTP 429, body {"statusCode":429,"error":"Too Many Requests",...}
  manager:    Warning "... invocation failed (HTTP 429)"; rethrow; generation stays 1
  adapter:    transient_failure; "Error: MCP tool 'shortio/get-domains' failed: ... 429 ..."
```

Negative example (session failure, reconnect; the Streamable HTTP contract):

```text
tools/call -> HTTP 404 (session expired)
  manager:    Warning "... invocation failed (HTTP 404)"; one coalesced reconnect for later calls; rethrow
  adapter:    not_found; "Error: MCP tool 'shortio/get-domains' failed: ... 404 ..."
```

Alternative: a second predicate for application statuses. Rejected. One predicate is enough.

### D4. Result-text auth detection applies only to OAuth-capable servers

`ReportToolFailure` calls `MarkToolAuthFailure` only when `HasOAuthRuntimeHints(serverName, entry)` is true, so only an OAuth-capable server (glossary) can be demoted from a tool-declared error. A stdio server or a static-header server keeps `Connected`. The Warning line from `ReportToolFailure` still records the failure. The manager reads the entry from `_serverEntries`, as `ReconnectAfterTransportFailureAsync` does.

Positive example (OAuth-capable server, reclassified; the fixture in `ToolLevelAuthFailure_MovesServerOutOfConnected`):

```text
http server, no Authorization header
  tool-declared error text: "Unauthorized: token expired"
  status: AuthFailed; remedy names "netclaw mcp auth <name>"
```

Negative example (static-header server, not reclassified). The observed server relays its REST layer's failures as tool-declared errors with the shape `{"error":"Request failed: 404 Not Found"}` and `{"error":"Request failed: 400 Bad Request"}`. A REST 403 follows the same shape:

```text
http server, Authorization header configured (the observed shortio profile)
  tool-declared error text: {"error":"Request failed: 403 Forbidden"}
  before: IsAuthFailureMessage matches "forbidden" -> AuthFailed; false authentication_failed alert;
          "netclaw mcp auth shortio" named as the remedy, which a static-header server cannot use
  after:  status Connected; Warning logged; no remedy names "netclaw mcp auth"
```

Alternative: delete the result-text heuristic. Rejected. The expired-token case is real and has a test. A false positive on an OAuth-capable server is a separate issue.

Alternative: gate on `HasConfiguredAuthorizationHeader` only. Rejected. A stdio server has no header and would stay demotable, with the same wrong remedy.

### D5. Owners and data lifetime

| Decision | Owner | Data |
|---|---|---|
| Outcome category for an MCP exception | `McpToolAdapter` | call-local |
| Tool receipt storage | `DispatchingToolExecutor` via `ToolExecutionOutputs` | call-local |
| Reconnect or not | `McpClientManager.IsTransportOrSessionFailure` | call-local |
| Warning log line | `McpClientManager.InvokeSharedAsync` | call-local |
| Prompt load failure result | `McpClientManager.LoadAsync` | call-local |
| Server status change | `McpClientManager.ReportToolFailure` | actor-local snapshot |

No durable record, event, snapshot, protobuf, configuration, or public API changes.

## Failure modes and recovery

This pseudocode is schematic. It omits the ACL gate and redaction.

```text
transport or session failure (no status, 404, socket fault)
  -> Warning log -> at most one coalesced reconnect for later calls -> rethrow
  -> adapter: transient_failure (404: not_found) -> result string

reconnect also fails
  -> Warning log -> existing Error log -> AggregateException rethrown
  -> adapter: transient_failure -> result string

application error, HTTP status (5xx, 429, 401, 403, other 4xx)
  -> Warning log -> rethrow, no reconnect, generation unchanged
  -> adapter: category per D1 -> result string

application error, JSON-RPC (McpException, not session-related)
  -> Warning log -> rethrow, no reconnect
  -> adapter: transient_failure -> result string

tool-declared error (isError: true)
  -> ReportToolFailure Warning -> result string; D4 gate decides AuthFailed (unchanged otherwise)

prompt load, application error
  -> failed load result that names the prompt; no reconnect

caller cancellation
  -> propagates; no log, no receipt, no reconnect (unchanged)

HttpClient timeout (TaskCanceledException, caller token not cancelled)
  -> Warning log -> rethrow, no reconnect -> adapter: transient_failure
```

## Risks / Trade-offs

- [OAuth-capable servers keep substring auth detection on result text] → Documented in D4. A false positive on such a server gets its own issue.
- [Tests that assert the manager returns an error string for `McpException`] → `McpClientManagerLifecycleTests` (the `application MCP failure` assertion) changes to expect the exception. The adapter test asserts the string.
- [The existing expired-token test uses a stdio entry] → It changes to an HTTP entry without an `Authorization` header, so it keeps its meaning under D4.
- [One Warning per failed call on a flapping server] → Same volume as the tool-declared error path today. Acceptable.
- [401 or 403 on an OAuth-capable server maps to `access_denied`] → The SDK refreshes inside the call. A surfaced 401 means refresh failed. `access_denied` is accurate.
- [A static-header server whose key is revoked stays `Connected`] → The Warning line and the `access_denied` result name the status. `netclaw mcp list` cannot show it. Acceptable for MVP.
- [A non-compliant server reports a dead session with HTTP 400, not 404] → The observed server answers a stale or deleted session with `HTTP 400 {"jsonrpc":"2.0","error":{"code":-32000,"message":"Bad Request: No valid session ID provided"},"id":null}`. The SDK turns a 400 with a JSON-RPC body into `McpProtocolException`, and the predicate's message check does not read that text as a session failure. Such a server keeps a dead session until the daemon restarts. This is pre-existing: before this change the same answer was a string result with no reconnect. Out of scope here; a follow-up can add a session-scoped check for that message shape.
- [Sub-issue text narrows] → #2056 drops `Retry-After`; #2057 keeps the heuristic for OAuth-capable servers and adds no 401/403 remedy text. The issues were edited to match, and the pull requests say so.

## Migration Plan

None. No durable data changes. Rollback is a revert.

## Delivery order

Stacked pull requests, one per issue, each on the previous branch:

1. This change's artifacts and the glossary terms.
2. #2055: D1 and D2.
3. #2056: D3, both consumers.
4. #2057: D4.
