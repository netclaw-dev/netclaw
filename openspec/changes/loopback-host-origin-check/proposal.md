## Why

The daemon treats every request that arrives from a loopback source address as the local Operator. A web page in the operator's browser can also reach the loopback daemon, through DNS rebinding or a cross-site request. Such a request carries a foreign `Host` name or a foreign `Origin`. A probe at `dev` showed that both kinds of request received the Operator identity. A foreign-origin WebSocket upgrade to `/hub/session` connected.

## What Changes

- The loopback authentication scheme checks the `Host` header of each loopback-source request in `local` exposure mode. The host name must pass the strict loopback name test `LoopbackHostName.IsStrictLoopback`: `localhost`, or the canonical text of an IP literal of `127.0.0.0/8` or `::1`. The port can be any value. The test never resolves DNS, and the bind validator `DaemonExposureValidator.IsLoopbackHost` builds on it, so a wider bind value cannot widen request admission. The `Host` text itself must be canonical. A `Daemon.Host` value admits after URI canonicalization: the CLI `Uri` rewrites a short form such as `127.1` to `127.0.0.1` before it sends the request. A rebinding page controls a DNS name and not an IP literal, so an IP literal is safe.
- When the request has an `Origin` header, the value must equal `http://` plus the request `Host`. `Origin: null` fails.
- A refused request fails authentication with a reason. A protected route answers HTTP 401 and the scheme logs a warning with the `Host` and `Origin` values. An anonymous route answers as before and writes no warning.
- The rule is fixed. No configuration option changes it.
- **BREAKING**: A client that reaches the loopback daemon through another host name, for example an `/etc/hosts` alias, gets 401. A local web app on another port, for example `http://localhost:3000`, also gets 401 when it calls the daemon from the browser.

Source PRD: PRD-002 SEC-005 (exposure policy).

Terms: this change uses [Operator, Operator principal, loopback request, and device token](../../../docs/spec/GLOSSARY.md) as the glossary defines them.

In scope: the loopback scheme. Out of scope: bearer requests, the answer of anonymous routes such as `/api/health/ready`, and the tunnel and `reverse-proxy` exposure modes. A per-session CSRF token is out of scope.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `hub-auth`: The "Loopback authentication scheme" requirement adds the Host and Origin check and its scenarios.

## Impact

- Code: `LoopbackAuthenticationHandler` in `src/Netclaw.Daemon/Security`.
- Security: this is HTTP admission and not tool authorization. No authorization context changes. The change adds one focused mutation gate. It covers the policy and the strict name test.
- Operations: the system skill `netclaw-operations` gets an entry for a request that the daemon refuses by `Host` or `Origin`. The release notes tell operators about the custom host name case.
