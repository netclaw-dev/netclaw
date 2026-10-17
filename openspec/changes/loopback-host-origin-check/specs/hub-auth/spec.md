## MODIFIED Requirements

### Requirement: Loopback authentication scheme

The daemon SHALL register a loopback authentication scheme that automatically authenticates connections from `127.0.0.1` and `::1` as `LocalProcess` / `Operator` without requiring credentials only when the selected exposure mode allows loopback trust.

When `Daemon.ExposureMode` is `reverse-proxy`, the loopback scheme SHALL return no result for loopback requests so that only explicit credentialed schemes can authorize the connection.

In `local` exposure mode, the loopback scheme SHALL refuse a loopback-source request unless the request passes both of these tests:

- The `Host` header names `localhost`, or an IP literal that the strict loopback name test (`LoopbackHostName.IsStrictLoopback`) accepts, with any port, compared without regard to case. An IP literal is an IPv4 address of `127.0.0.0/8` in canonical dotted-quad text, or `[::1]`. The scheme SHALL refuse `localhost.`, a short, decimal, or hexadecimal IPv4 spelling, an IPv4-mapped IPv6 literal, `0.0.0.0`, and any other name, including a DNS name that resolves to loopback. The strict test SHALL NOT resolve DNS, and the bind validator SHALL NOT widen it. The `Host` text itself SHALL be canonical. A `Daemon.Host` value admits after URI canonicalization, because the CLI rewrites a short form to canonical text before it sends the request.
- The request has no `Origin` header, or the `Origin` header equals `http://` plus the request `Host`, compared without regard to case.

The scheme SHALL run the Host and Origin test on every loopback-source request in `local` mode and SHALL fail a refused request with a reason. A refusal denies only a protected route: the route SHALL answer HTTP 401. A route that allows anonymous access SHALL answer as before. The loopback scheme SHALL log a warning with the `Host` and `Origin` values and no other request data only when it denies a request. A refused request to an anonymous route SHALL NOT write a warning. The rule SHALL be fixed and SHALL have no configuration option. The Host and Origin test SHALL NOT apply to requests that carry a bearer token.

The terms Operator, Operator principal, loopback request, and device token have the meaning in [the engineering glossary](../../../../../docs/spec/GLOSSARY.md). The Host and Origin values are call-local data.

#### Scenario: Loopback connection auto-authenticated in local mode

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** a connection originates from `127.0.0.1` or `::1`
- **AND** the `Host` header is `localhost:5199`
- **WHEN** the client connects to `/hub/session`
- **THEN** the connection is authenticated with principal classification `Operator` and transport authenticity `LocalProcess`

#### Scenario: Matching Origin is accepted

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** a connection originates from `127.0.0.1`
- **AND** the `Host` header is `localhost:5199` and the `Origin` header is `http://localhost:5199`
- **WHEN** the loopback scheme evaluates the connection
- **THEN** the connection is authenticated as `Operator` with `LocalProcess` authenticity

#### Scenario: Local bind other than 127.0.0.1 is admitted

- **GIVEN** `Daemon.ExposureMode` is `local` and `Daemon.Host` is `127.0.0.2`
- **AND** the CLI connects to `http://127.0.0.2:5199`, so the `Host` header is `127.0.0.2:5199`
- **WHEN** the loopback scheme evaluates the connection
- **THEN** the connection is authenticated as `Operator` with `LocalProcess` authenticity

#### Scenario: DNS name that resolves to loopback is refused

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** a connection originates from `127.0.0.1`
- **AND** the `Host` header is `localtest.me:5199`, which resolves to loopback
- **WHEN** the loopback scheme evaluates the connection
- **THEN** authentication fails

#### Scenario: Legacy IP spelling is refused

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** a connection originates from `127.0.0.1`
- **AND** the `Host` header is `2130706433:5199`, `0x7f.1:5199`, `0.0.0.0:5199`, or `localhost.:5199`
- **WHEN** the loopback scheme evaluates the connection
- **THEN** authentication fails

#### Scenario: Foreign Host is refused

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** a connection originates from `127.0.0.1`
- **AND** the `Host` header is `rebind.example:5199`
- **WHEN** the loopback scheme evaluates the connection
- **THEN** authentication fails
- **AND** the route answers HTTP 401

#### Scenario: Foreign Origin is refused

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** a connection originates from `127.0.0.1`
- **AND** the `Host` header is `127.0.0.1:5199` and the `Origin` header is `http://evil.example`
- **WHEN** the loopback scheme evaluates the connection
- **THEN** authentication fails
- **AND** the route answers HTTP 401

#### Scenario: Denied request is logged once

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** a connection originates from `127.0.0.1`
- **AND** the `Host` header is `rebind.example:5199`
- **WHEN** the request reaches a protected route
- **THEN** the route answers HTTP 401
- **AND** the daemon writes one warning with the `Host` and `Origin` values

#### Scenario: Anonymous route writes no refusal warning

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** a connection originates from `127.0.0.1`
- **AND** the `Host` header is `rebind.example:5199`
- **WHEN** the request reaches `/api/health/ready`
- **THEN** the route answers as it does for any caller
- **AND** the daemon writes no refusal warning

#### Scenario: Null Origin is refused

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** a connection originates from `127.0.0.1`
- **AND** the `Origin` header is `null`
- **WHEN** the loopback scheme evaluates the connection
- **THEN** authentication fails

#### Scenario: Bearer request is not checked

- **GIVEN** a request carries a valid device bearer token
- **AND** the `Host` header is `app.example.com`
- **WHEN** the daemon authenticates the request
- **THEN** the bearer scheme authenticates the request
- **AND** the loopback Host and Origin test does not run

#### Scenario: Non-loopback connection not auto-authenticated

- **GIVEN** a connection originates from a non-loopback address
- **WHEN** the loopback scheme evaluates the connection
- **THEN** the scheme returns no result (defers to other schemes)

#### Scenario: Reverse-proxy mode does not auto-authenticate loopback

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** a connection originates from `127.0.0.1`
- **WHEN** the loopback scheme evaluates the connection
- **THEN** the scheme returns no result
- **AND** the connection must authenticate through a credentialed scheme instead
