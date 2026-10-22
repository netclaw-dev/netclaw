## Context

Two anonymous endpoints accept a local-control proof:

- `POST /api/local-control/v1/pairing-code`
- `POST /api/local-control/v1/provider-oauth-result`

The ASP.NET Core rate limiter ran before each handler. It allowed 10 requests per second for each `RemoteIpAddress`.
In a tunnel mode, the tunnel agent connects from loopback. Remote requests and host CLI requests share one partition.
The host CLI reads the provider OAuth result one time. One `429` ends the login.

## Goals / Non-Goals

**Goals:**

- A valid proof is never refused by the limit.
- Refused requests stay limited for each source address.
- One code path applies the rule to both endpoints.

**Non-Goals:**

- A CLI retry loop. A retry hides the failure and only moves the race.
- A long lockout like `PairingExchangeGuard`. A lockout on loopback would block the host CLI.

## Decisions

### Count only refusals, after validation

The limit cannot know a request outcome before the handler runs. Thus the middleware limiter is removed.
`LocalControlRequests.AuthorizeAsync` reads the body and validates the proof first.
It counts a request only when that request is refused.

```text
AuthorizeAsync(request)                       schematic
  (body, refusal) = read body (413 / 400)
  if body: refusal = validate proof for body.Operation (401 / 400 / 503)
  if refusal is null: return body             valid proof, never counted
  count[source] += 1 in the current window
  return count[source] <= 10 ? refusal : 429
```

Alternative: a CLI retry with backoff until the flow deadline. Rejected, because the attacker can keep the partition full.
Alternative: a separate partition for valid proofs. Rejected, because the partition key is chosen before the proof is read.

### A fixed one-second window from the daemon TimeProvider

`LocalControlRequests` keeps one window start and one count for each source.
When the window ends, the next refusal clears all counts. Memory is bounded by the sources seen in one second.
The window uses the injected `TimeProvider`, so tests control it.

### Ownership and state

- Owner: `LocalControlRequests` in `Netclaw.Daemon.Security`.
- State: process-local, in memory, in one DI singleton. It is not durable. A restart clears it.
- The proof validator and its replay cache do not change.

## Risks / Trade-offs

- [A refused request now costs a body read and a proof decrypt before the limit] → The body is at most 4 KiB, and the decrypt of a 26-byte payload is cheap.
- [An attacker can still get 10 refusals per second from each source] → Refusals grant nothing. The proof stays the only authority.
- [Failure mode: the count is lost at restart] → This is acceptable, because the count only sheds load.

## Migration Plan

No data migration. The `local-control` rate-limit policy name is removed from the daemon. No configuration names it.
