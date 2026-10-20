## Context

`LoopbackAuthenticationHandler` grants `Operator` and `LocalProcess` to each request whose source address is loopback in `local` mode. A browser on the same host can send such a request. DNS rebinding gives the attacker page a foreign `Host` name. A cross-site form or fetch gives a foreign `Origin`. A probe at `dev` confirmed both: `/api/health/status` returned 200, and a foreign-origin WebSocket upgrade to `/hub/session` returned 101.

Terms: [Operator, Operator principal, loopback request, and device token](../../../docs/spec/GLOSSARY.md) have the glossary meaning.

## Goals / Non-Goals

**Goals:**

- Refuse loopback-source requests that a foreign web page can cause.
- Keep the CLI, the TUI, and a browser on `localhost` working with no new prompt.

**Non-Goals:**

- Bearer requests, anonymous routes, and tunnel or `reverse-proxy` modes. A page cannot hold a device token, and a reverse proxy sends the public `Host` name.
- A per-session CSRF token. Two headers already show the threat.

## Decisions

- **Put the check in `LoopbackAuthenticationHandler.HandleAuthenticateAsync`.** It runs after the exposure-mode guard and the loopback address test, and before the claims. No route can skip it there. Alternative: the ASP.NET `AllowedHosts` filter or a middleware. Both cover bearer and anonymous routes, and the host filter does not test `Origin`.
- **Share one strict name test below the validator and the policy.** `LoopbackHostName.IsStrictLoopback` in `Netclaw.Configuration` accepts `localhost` (no case, no trailing dot, no trimming) and the canonical text of an IP literal of `127.0.0.0/8` or `::1`. It never resolves DNS. `LoopbackHostOriginPolicy` calls it directly and does not call the bind validator. `DaemonExposureValidator.IsLoopbackHost` calls it first and adds bind-only leniency (legacy spellings such as `127.1`). Request admission therefore depends on the strict test only, and a widening of the validator, for example a DNS alias, cannot admit a request. A widening of the strict test is a security change, and the mutation gate and `LoopbackHostCouplingGuardTests` cover it.
- **Admit a short `Daemon.Host` after URI canonicalization.** The CLI connects with a `Uri`, which rewrites `127.1` to `127.0.0.1`. The `Host` header then carries canonical text. A client that sends the short form as `Host` text gets 401. An IP literal is safe: a rebinding page controls a DNS name and not an IP literal.
- **Keep the rule in one static policy type.** `LoopbackHostOriginPolicy` takes the `HostString` and the `Origin` values and returns a refusal reason or none. The unit tests and the mutation gate call it directly.
- **Fixed rule, no knob.** A knob would copy data that the request already holds.
- **Return `AuthenticateResult.Fail`.** A failed result leaves the user anonymous. A protected route then challenges and answers 401. An anonymous route still answers.
- **Log in `HandleChallengeAsync`.** The default scheme runs on every request, also on anonymous routes. The handler writes the warning only when a protected route challenges, so a request that the daemon does not deny writes no log line. The challenge reads the cached authentication result and does not run the policy again.
- **Compare the Origin to `http://` plus the Host.** The daemon serves plain HTTP in `local` mode, so an `https://` Origin never matches.

## Data and decision owner

`LoopbackAuthenticationHandler` owns the decision. The policy data is call-local: the `Host` and `Origin` values come from the request, live for one call, and no actor or store keeps them. The loopback name rule is code in `LoopbackHostName`. The only durable input is `Daemon.Host`, and the policy does not read it.

Schematic order of checks. The sketch omits the bearer selector and the logger plumbing.

```text
AuthSelector: Authorization starts with "Bearer "  -> DeviceBearer scheme (this flow does not run)

HandleAuthenticateAsync (runs on every request):
  1. ExposureMode needs remote authentication       -> NoResult
  2. source address is null or not loopback         -> NoResult
  3. refusal = FindRefusal(Host, Origin)
       3a. Host is not "localhost" or a canonical loopback IP literal  -> "Host is not a loopback name"
       3b. Origin present and not "http://" + Host                     -> "Origin does not match Host"
  4. refusal is set                                 -> Fail(refusal)
  5. otherwise                                      -> Success(Operator, LocalProcess)

HandleChallengeAsync (runs only when a protected route denies the request):
  6. cached result is Fail                          -> log warning (Host, Origin); answer 401
```

Positive example: `Daemon.Host` is `127.0.0.2`, the CLI sends `Host: 127.0.0.2:5199`, and step 5 grants the Operator identity. Negative example: a page sends `Host: rebind.example:5199` from a loopback source, step 3a refuses it, and a protected route answers 401 with one warning. The same request to `/api/health/ready` answers 200 and writes no warning.

## Risks / Trade-offs

- A client that uses a custom host name for the loopback daemon gets 401 → the release note and the skill entry say so.
- A page can send many requests with a foreign `Host` → the daemon logs a warning only when a protected route denies a request, and the line shows the two header values only.
- A local web app on another port, for example `http://localhost:3000`, sends an `Origin` that differs from the `Host`, so it gets 401 → the release note tells the operator to pair the app and send a device token, or to call the daemon from a server process.
