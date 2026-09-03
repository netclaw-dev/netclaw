## Context

See `proposal.md` for the security problem.
The daemon and host CLI already share `NetclawPaths.KeysDirectory` and the `Netclaw` Data Protection application name.
The tunnel can forward remote requests through the same loopback listener that the host CLI uses.
The current pairing exchange consumes a code before the device registry accepts the new device.
Pairing has a host code-creation path and a remote code-exchange path.
An exposure mode must keep both paths available without granting remote callers host authority.

## Goals / Non-Goals

**Goals:**

- Prove host key-ring access without source-address trust.
- Keep pairing available in every exposure mode.
- Preserve durable device state during the upgrade.
- Keep a code valid after a recoverable exchange failure.
- Add deterministic proof for success and denial paths.

**Non-Goals:**

- Do not create a general local administration protocol.
- Do not grant device tokens local-control authority.
- Do not retain the legacy hub method.
- Do not change exposure configuration or device record formats.

## Decisions

### Use a purpose-isolated Data Protection proof

The proof uses the current key ring and application name.
It uses the purpose `Netclaw.LocalControl.Pairing.v1` instead of `Netclaw.Secrets.v1`.
This choice avoids a second host secret and reuses the current deployment boundary.

The protected plaintext has a fixed binary layout:

1. One byte contains protocol version `1`.
2. One byte contains operation `1`, which means `generate-pairing-code`.
3. Eight big-endian bytes contain the issue time in Unix milliseconds.
4. Sixteen bytes contain a cryptographic random nonce.

The outer HTTP request uses `{ "proof": "<base64url>" }`.
The endpoint returns the current `PairingCodeResultDto` JSON shape.

Alternatives included local sockets and a new HMAC key.
Local sockets need platform-specific lifecycle code.
A new HMAC key duplicates the current key-ring authority.

### Validate time and replay before code generation

One singleton validator owns the replay cache.
The cache data is process-local and expires after each proof window.
The validator uses the injected `TimeProvider`.
It removes expired entries before its 1,024-entry capacity check.
It records a valid nonce atomically before it permits code generation.

Invalid proofs return `401` with one generic body.
A valid unsupported version returns `400` with `unsupported_protocol_version`.
Replay-cache exhaustion returns `503` with a generic recovery message.
The endpoint rejects bodies larger than 4 KiB.

### Keep the HTTP endpoint thin

The endpoint owns HTTP status mapping only.
The proof validator owns authentication, time, version, operation, and replay decisions.
The pairing coordinator owns code generation and exchange state transitions.
The device registry remains the durable owner of `devices.json`.

### Serialize each pairing transaction

One singleton pairing coordinator serializes code generation and code exchange.
It owns the call-local token material and the transaction order.
The pairing code service owns the process-local active code.
It validates the code before the registry checks the device name.
It writes the device before it consumes the code.

If the registry write fails, the code stays active.
If the write succeeds, code consumption occurs synchronously under the same coordinator lock.
A process failure after the write clears the in-memory code during restart.
This order prevents a second device from using the old code.

### Remove hub authority without a fallback

The CLI calls only `/api/local-control/v1/pairing-code`.
The daemon removes `SessionHub.GeneratePairingCode` and its address predicate.
The hub keeps its chat contract.

A new CLI against an old daemon receives `404` and prints joint-update guidance.
An old CLI against a new daemon receives a missing hub-method error.
The new daemon does not add a compatibility route.

### Use a direct host transport for the proof

The host command derives its endpoint from the daemon configuration in the same Netclaw home.
It does not use paired-client endpoint state.
A dedicated client sends no device token and bypasses HTTP proxies.
The client also rejects redirects.

Direct means that the CLI uses the daemon bind endpoint instead of the advertised remote endpoint.
Direct does not require `local` exposure mode or a loopback-only daemon bind.
The host command must work after an operator enables any exposure mode.

The command keeps two endpoint values.
The direct local-control endpoint receives the proof.
The advertised endpoint appears only in the remote pairing instruction.

This rule keeps the proof on the host authority boundary.
A remote client endpoint could otherwise receive a valid proof and a device token.

The exposure path can make a remote request appear to come from loopback.
That path does not grant authority because the endpoint requires a valid key-ring proof.

| Exposure mode | Host code-creation path | Remote code-exchange path | Code-creation authority |
|---|---|---|---|
| `local` | Configured daemon endpoint | Direct daemon endpoint | Valid key-ring proof |
| `reverse-proxy` | Configured daemon endpoint | Advertised proxy endpoint | Valid key-ring proof |
| `tailscale-serve` | Configured daemon endpoint | Advertised Tailscale endpoint | Valid key-ring proof |
| `tailscale-funnel` | Configured daemon endpoint | Advertised Tailscale endpoint | Valid key-ring proof |
| `cloudflare-tunnel` | Configured daemon endpoint | Advertised Cloudflare endpoint | Valid key-ring proof |

```text
Daemon host                                      Exposure boundary

Host CLI -- key-ring proof --> local-control endpoint -- creates --> pairing code
                                                               |
                                                               v
Remote CLI -- pairing code --> proxy or tunnel --> exchange endpoint -- returns --> device token

Remote caller -- loopback claim or device token --> local-control endpoint -- rejects
```

The diagram is schematic.
It omits rate limits, proof replay checks, and TLS termination.

| Presented fact | Grants host code-creation authority | Reason |
|---|---|---|
| Loopback source address | No | A proxy or tunnel can produce this address |
| Forwarded loopback header | No | The caller controls or influences routing metadata |
| Valid device or bootstrap token | No | The token grants remote device access only |
| Valid unused key-ring proof | Yes | The proof demonstrates possession of the host key ring |
| Captured valid unused proof | Yes, until first use or expiration | The proof is a short-lived bearer capability |

Examples:

- `Daemon.Host=0.0.0.0` maps the host request to `http://127.0.0.1:<port>`.
- `Daemon.Host=192.168.1.20` keeps that configured bind address for the direct host request.
- A saved `https://remote.example` endpoint appears in the instruction but receives no proof.
- A `307` redirect does not receive a second request.

Counterexamples:

- A remote caller cannot add `X-Forwarded-For: 127.0.0.1` instead of a proof.
- A device token cannot authorize code creation through a reverse proxy.
- A copied unused proof can win a first-use race because the proof does not provide channel confidentiality.

### Treat key-ring access as host authority

The common Data Protection factory restricts the Unix key directory to owner access.
The factory fails visibly when it cannot create, read, or protect with the key ring.
Windows keeps the platform Data Protection protection model.

Container operators run `docker exec ... netclaw daemon pair`.
This command shares the daemon key ring and user identity.

### Ordered flow

```text
Host CLI                 Local-control endpoint       Pairing coordinator       Device registry
   | protect v1 proof              |                         |                         |
   | direct POST, no redirect ---->|                         |                         |
   |                               | validate time/replay    |                         |
   |                               | generate -------------->|                         |
   |<--------- code and expiry ----|                         |                         |
Remote CLI                        Exchange endpoint          |                         |
   | POST code and name ---------->| validate code --------->|                         |
   |                               |                         | add device ------------>|
   |                               |                         | consume code after write |
   |<-------------- token ---------|                         |                         |
```

The diagram is schematic.
It omits rate limits, token hashing, and HTTP error mapping.

## Risks / Trade-offs

- Key-ring copies grant local-control authority. → Documentation treats the key ring as a host credential.
- Clock jumps can reject a proof. → The CLI creates a fresh proof and the daemon allows five seconds of future skew.
- A full replay cache can deny a valid host. → Entries expire quickly and the daemon logs only a reason category.
- Immediate removal breaks mixed versions. → The CLI prints explicit joint-update guidance and never uses an unsafe fallback.
- A registry write can fail after token creation. → The coordinator discards the raw token and preserves the code.
- A general HTTP client can export the proof. → A dedicated direct client disables proxies, redirects, and bearer attachment.
- A non-loopback HTTP path can expose the proof. → The deployment must protect the direct host path from an on-path observer.
- A copied proof can win its first-use race. → The proof expires after 30 seconds and the daemon accepts its nonce once.

## Migration Plan

1. Implement and verify the change in a public draft PR with normal CI.
2. Update the daemon and CLI in the same `0.27` beta.
3. Restart the daemon after the update.
4. Preserve all device records, valid tokens, and exposure settings.
5. Run `netclaw daemon pair` for any required host re-authentication.
6. Run the CLI inside the container for a container daemon.
7. Deploy the website procedure update after the beta is available.
8. Publish the advisory after the fixed release and procedure are available.

Rollback uses the prior binary and unchanged device registry.
Operators must not change the exposure mode as a rollback or recovery step.
