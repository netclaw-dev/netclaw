## ADDED Requirements

### Requirement: NETCLAW_HOME environment variable overrides base path

`NetclawPaths` SHALL honour the `NETCLAW_HOME` environment variable as a
fallback source for its `BasePath` when no explicit `basePath` constructor
argument is provided. Precedence, from highest to lowest, SHALL be:

1. Explicit `basePath` constructor argument (used by tests and embedded
   hosting scenarios).
2. `NETCLAW_HOME` environment variable, if set and non-empty.
3. The default `{UserProfile}/.netclaw` path.

This matches the precedent established by `DaemonApi.ResolveEndpoint`,
which already reads `NETCLAW_DAEMON_ENDPOINT` to override the CLI's
daemon URL.

#### Scenario: Env var redirects path resolution

- **GIVEN** `NETCLAW_HOME=/tmp/nc-sandbox` is exported in the environment
- **AND** no explicit `basePath` argument is passed to the constructor
- **WHEN** `new NetclawPaths()` is invoked
- **THEN** the resulting `BasePath` equals `/tmp/nc-sandbox`
- **AND** derived paths (`SqliteDbPath`, `LogsDirectory`, etc.) are rooted
  at `/tmp/nc-sandbox`

#### Scenario: Explicit basePath wins over env var

- **GIVEN** `NETCLAW_HOME=/tmp/nc-sandbox` is exported
- **WHEN** `new NetclawPaths("/tmp/nc-explicit")` is invoked
- **THEN** the resulting `BasePath` equals `/tmp/nc-explicit`

#### Scenario: Unset env var falls back to default

- **GIVEN** `NETCLAW_HOME` is not set in the environment
- **WHEN** `new NetclawPaths()` is invoked
- **THEN** the resulting `BasePath` equals
  `{UserProfile}/.netclaw` — identical to pre-change behavior

#### Scenario: Eval script isolates CLI-side state via env var

- **GIVEN** `evals/run-evals.sh` exports `NETCLAW_HOME=$EVAL_HOME` before
  invoking `netclaw -p`
- **WHEN** the CLI resolves paths during an eval prompt
- **THEN** it reads and writes only inside `$EVAL_HOME`
- **AND** the operator's real `~/.netclaw` directory is not touched by
  the CLI during the eval run
