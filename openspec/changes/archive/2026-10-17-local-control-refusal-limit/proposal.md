## Why

Source PRDs: `PRD-002`, `PRD-004`.

The daemon limited all local-control requests to 10 per second for each source address.
A tunnel agent forwards remote requests from loopback, which is also the address of the host CLI.
Thus a remote caller without a proof could fill the loopback partition with junk requests.
Then the host CLI got `429` for its one provider OAuth result read, and the login failed.
The same flood also blocked pairing-code creation.

## What Changes

- The daemon counts only refused local-control requests against the limit.
- A request with a valid proof never counts and the limit never refuses it.
- A refused request above 10 in one window gets `429` instead of its normal refusal.
- The rule covers both local-control endpoints through one shared request gate.
- The ASP.NET Core `local-control` rate-limit policy is removed.

The MVP scope includes the refusal limit for both local-control endpoints and deterministic flood tests.
The MVP scope excludes a CLI retry loop and a lockout that lasts longer than one window.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `local-control-proof`: The strict-bounds requirement changes its rate limit. Only refused requests count, and a valid proof is never refused by the limit.

## Impact

- `LocalControlRequests` in `Netclaw.Daemon` becomes a singleton service. It reads the body, validates the proof, and counts refusals.
- Both endpoint handlers call `LocalControlRequests.AuthorizeAsync`.
- A refused request now reads its body (at most 4 KiB) and runs proof validation before the limit applies.
- Security impact: none for authority. The proof stays the only authority. The limit only sheds load from refused requests.
- Operational impact: a flood of junk requests through a tunnel no longer blocks provider OAuth login or `netclaw daemon pair` on the host.
