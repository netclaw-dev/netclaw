## Why

A refresh of an OAuth access token writes the new token expiry into `netclaw.json`.
The config watcher sees a `netclaw.json` write and restarts the daemon. Each
refresh can then stop work in progress. The expiry is refresh timing, not a
setting that an operator edits. It belongs beside the OAuth tokens in `secrets.json`.

PRD reference: none. This change fixes a defect in the provider OAuth refresh
path. It adds no product feature.

## What Changes

- Netclaw writes `Providers.<name>.OAuthTokenExpiry` to `secrets.json` in the
  same save as the OAuth tokens. A refresh no longer writes `netclaw.json`.
- `secrets.json` keeps that one leaf unencrypted. The configuration binder reads
  the file as plain JSON and cannot read encrypted text as a date. Every other
  leaf stays encrypted.
- The reader rejects an expiry that is not a date. It no longer ignores the value.
- A key in `netclaw.json` is retired. It still binds until `secrets.json` holds a
  value. `netclaw doctor` reports the key. `netclaw doctor --fix` moves the date
  to `secrets.json` and removes the key after a backup.
- The daemon logs a startup warning for each retired key.
- The refresh service reads `secrets.json` under its provider gate before it
  spends a refresh token. It never presents a refresh token that it already
  spent. It keeps the live token in memory when a save fails, and it logs the
  failed save as an error.
- `netclaw secrets set` states that the token expiry is not encrypted and rejects
  a value that is not a date.
- A focused mutation gate protects the encryption exception.

In scope: the items above.

Out of scope (follow-up change): parsing of the token endpoint rejection
response, retry of a rejected refresh, an `auth-failed` provider health status,
and a cross-process lock for the `netclaw` CLI.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `transactional-secrets`: one provider leaf stays readable in `secrets.json`.
- `netclaw-model-providers`: OAuth refresh state lives in `secrets.json`.

## Impact

- Code: `Netclaw.Configuration` (secrets writer, expiry storage), `Netclaw.Providers`
  (token persistence, refresh service), `Netclaw.Cli` (provider writer, provider
  command, secrets command, doctor), `Netclaw.Daemon` (startup warning).
- Config: the JSON schema keeps `OAuthTokenExpiry` and marks a `netclaw.json` key as retired.
- Docs and skill: `docs/spec/configuration.md`, `netclaw-operations` skill.

Security impact: the one unencrypted leaf is a date. It is not a credential. The
`secrets.json` file stays owner-only and read-denied for agent tools. The
predicate matches an exact three-segment path, and a focused mutation gate checks it.

Operational impact: a token refresh restarts nothing. An operator with a
retired key runs `netclaw doctor --fix`. An operator with an encrypted expiry in
`secrets.json` signs in again with `netclaw provider`.
