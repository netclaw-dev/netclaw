## Device Pairing


Remote devices authenticate with the daemon using a two-sided pairing protocol.

### Pairing flow

**Daemon host** (requires local or SSH access):

```
shell_execute: netclaw daemon pair
```

This command proves access to the daemon host key ring.
It creates a single-use pairing code with a five-minute lifetime.
The command works in all exposure modes.
Host authority follows the shared Netclaw home and key ring.
It does not follow a physical host label, source address, tunnel, or proxy.

Run the CLI inside the daemon container for a container deployment:

```
shell_execute: docker exec <container-name> netclaw daemon pair
```

The container command must use the same Netclaw home and user as the daemon.
Do not copy the key ring to a remote device.

If `netclaw daemon pair` fails immediately after an exposure-mode change, run
`netclaw doctor` and inspect `~/.netclaw/logs/crash-*.log` for the specific
startup validation failure instead of assuming a generic readiness timeout.

**Client side** (remote device):

```
shell_execute: netclaw pair https://my-daemon.tail1234.ts.net:5000
```

The user is prompted for the pairing code. On success, the bearer token is
saved to `secrets.json` (`DeviceToken` field) and the endpoint is saved to
`~/.netclaw/client/config.json`.

### Credential lifecycle

| Credential | Lifetime | Next action after failure |
|---|---|---|
| Host proof | 30 seconds and one use | Run `netclaw daemon pair` again |
| Pairing code | Five minutes and one successful exchange | Create a new code on the daemon host |
| Device token | Until operator revocation | Use the normal pairing flow again |

The device token has no automatic expiration or refresh flow.
Pairing-code expiration does not invalidate a paired device token.

If the daemon rejects a duplicate device name, select a unique name.
Reuse the same code while it remains valid.

Create a new host code after an invalid, expired, used, or missing code response.
Wait before a retry when the daemon reports a request limit.
The CLI does not save a device token or endpoint after a failed exchange.

Use the normal pairing flow when a client loses its token.
Select a unique replacement name if the old device record still exists.
Revoke the old device record after the replacement token works.
Use the same flow after an operator revokes a device and later restores access.

Update the CLI and daemon together when the command reports a protocol mismatch.
The command does not use the old hub method as a fallback.

### Device management

| Action | Command |
|--------|---------|
| List paired devices | `netclaw daemon devices` |
| Revoke a device | `netclaw daemon devices revoke <name>` |

### Security notes

- Codes: single-use, 8 chars from 32-char alphabet (~1.1 trillion
  combinations), 5-minute TTL
- Rate limiting: 5 attempts/min/IP; after 10 failures, the IP is blocked for
  15 minutes
- When no code is pending, the exchange endpoint returns 404 (invisible to
  scanners)
- Tokens are stored as salted SHA-256 hashes on the daemon; the raw token is
  never persisted server-side
- Device tokens remain valid until operator revocation; they have no refresh flow

### Config locations

- `~/.netclaw/config/devices.json` — paired device registry (daemon side)
- `~/.netclaw/config/secrets.json` — `DeviceToken` field (client side, added
  by `netclaw pair`)
- `~/.netclaw/config/netclaw.json` — `Daemon` section (`Host`, `Port`,
  `ExposureMode`)
