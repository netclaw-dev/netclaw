## ADDED Requirements

### Requirement: CLI derives local control-plane endpoint from daemon bind config

When no explicit daemon endpoint override exists, the CLI SHALL derive a usable local control-plane endpoint from `Daemon.Host` and `Daemon.Port` in daemon configuration instead of always falling back to `http://127.0.0.1:5199`.

If the daemon bind host is an unspecified wildcard listen address such as `0.0.0.0`, `::`, or `[::]`, the CLI SHALL normalize it to a connectable loopback host for local control-plane use.

#### Scenario: Explicit environment override still wins

- **GIVEN** `NETCLAW_DAEMON_ENDPOINT` is set
- **WHEN** the CLI resolves the daemon endpoint
- **THEN** it uses the environment override

#### Scenario: Client config override wins over daemon bind fallback

- **GIVEN** no environment override is set
- **AND** the client config file contains a daemon endpoint
- **WHEN** the CLI resolves the daemon endpoint
- **THEN** it uses the client config endpoint

#### Scenario: Daemon bind config provides fallback endpoint

- **GIVEN** no environment override or client endpoint override exists
- **AND** daemon config contains `Host = "10.0.0.20"` and `Port = 6200`
- **WHEN** the CLI resolves the daemon endpoint
- **THEN** it returns `http://10.0.0.20:6200`

#### Scenario: Wildcard bind is normalized for local control-plane use

- **GIVEN** no environment override or client endpoint override exists
- **AND** daemon config contains `Host = "0.0.0.0"` and `Port = 5199`
- **WHEN** the CLI resolves the daemon endpoint
- **THEN** it returns `http://127.0.0.1:5199`

### Requirement: Daemon-host CLI auth decision uses effective exposure requirements

The daemon-host CLI SHALL decide whether to attach a bearer token based on whether the resolved endpoint requires remote authentication, not only on whether the endpoint host is loopback.

#### Scenario: Reverse-proxy loopback control-plane endpoint attaches token

- **GIVEN** the resolved endpoint is `http://127.0.0.1:5199`
- **AND** daemon config exposure mode is `reverse-proxy`
- **AND** a device token exists locally
- **WHEN** the CLI builds its daemon connection
- **THEN** it attaches the bearer token

#### Scenario: Local-mode loopback control-plane endpoint skips token

- **GIVEN** the resolved endpoint is `http://127.0.0.1:5199`
- **AND** daemon config exposure mode is `local`
- **WHEN** the CLI builds its daemon connection
- **THEN** it does not attach a bearer token by default
