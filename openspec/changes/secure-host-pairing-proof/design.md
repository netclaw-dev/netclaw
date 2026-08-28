## Context

See `proposal.md` for the security problem.
The daemon and host CLI already share `NetclawPaths.KeysDirectory` and the `Netclaw` Data Protection application name.
The tunnel can forward remote requests through the same loopback listener that the host CLI uses.
The current pairing exchange consumes a code before the device registry accepts the new device.

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

This rule keeps the proof on the host authority boundary.
A remote client endpoint could otherwise receive a valid proof and a device token.

Examples:

- `Daemon.Host=0.0.0.0` maps the host request to `http://127.0.0.1:<port>`.
- A saved `https://remote.example` client endpoint does not receive the proof.
- A `307` redirect does not receive a second request.

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

## Migration Plan

1. Implement and verify the change in the temporary private advisory fork.
2. Update the daemon and CLI in the same `0.27` beta.
3. Restart the daemon after the update.
4. Preserve all device records, valid tokens, and exposure settings.
5. Run `netclaw daemon pair` for any required host re-authentication.
6. Run the CLI inside the container for a container daemon.
7. Deploy the website procedure update after the beta is available.
8. Publish the advisory after the fixed release and procedure are available.

Rollback uses the prior binary and unchanged device registry.
Operators must not change the exposure mode as a rollback or recovery step.
