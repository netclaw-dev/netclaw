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

If the daemon rejects a duplicate device name, select a unique name.
The same valid code remains available after this conflict.

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

### Config locations

- `~/.netclaw/config/devices.json` — paired device registry (daemon side)
- `~/.netclaw/config/secrets.json` — `DeviceToken` field (client side, added
  by `netclaw pair`)
- `~/.netclaw/config/netclaw.json` — `Daemon` section (`Host`, `Port`,
  `ExposureMode`)
