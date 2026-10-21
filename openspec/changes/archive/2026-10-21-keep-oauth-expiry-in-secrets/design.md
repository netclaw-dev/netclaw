## Context

See proposal.md for the reason. Before this change, `PersistTokens` wrote the
tokens to `secrets.json` and then rewrote `netclaw.json` with the expiry. The
config watcher watches `netclaw.json` only, so each refresh restarted the daemon.

The daemon binds provider entries from `IConfiguration` sources in this order:
`netclaw.json`, then `secrets.json`, then `NETCLAW_*` variables. The binder reads
`secrets.json` as plain JSON. `SecretsFileWriter` encrypts every string leaf, so
an encrypted expiry cannot bind as a date.

## Goals / Non-Goals

**Goals:**
- A refresh writes `secrets.json` only.
- The tokens and the expiry change in one atomic update.
- An old `netclaw.json` key keeps working and has a safe upgrade path.
- The refresh service never presents a spent refresh token.

**Non-Goals:**
- Parsing of token endpoint rejection responses, retry, and the `auth-failed`
  health status. These move to a follow-up change.
- A cross-process secrets lock for the CLI.
- A new state file or a new configuration source.

## Decisions

1. **Exempt one leaf from encryption instead of adding a state file.** The owner
   of the rule is `SecretsFileWriter.IsPlaintextLeaf`. The predicate matches a
   JSON path of exactly three segments: `Providers`, any name, `OAuthTokenExpiry`.
   It ignores case. The alternative was a separate `oauth-state.json` file with
   its own configuration source and path policy. That adds a file, a source, and
   a deny-list entry. The exception adds one predicate.
2. **Decrypt on read, write plaintext.** `DecryptNode` still decrypts every
   `ENC:` leaf, including the exempt path. A transactional update then heals an
   old encrypted expiry on the next save. The binder still fails loudly on an
   encrypted expiry, because a silent null would hide a bad file.
3. **Write a JSON null, not remove the key.** `secrets.json` layers after
   `netclaw.json`. A null hides a retired `netclaw.json` value.
4. **Strict expiry parse.** `OAuthTokenExpiryStorage.Parse` throws
   `InvalidDataException` for a non-date. This matches the binder. No silent fallback.
5. **Refresh state per provider under a gate.** The service keeps
   `LastStored`, `Live`, and a `Spent` set per provider in process memory. It
   reads `secrets.json` under the gate and refuses a spent token. A restart
   clears the state; the file is then the only source.
6. **The legacy fix is a doctor action.** `RetiredOAuthExpiryDoctorCheck` reports.
   `DoctorFixService` copies the date to `secrets.json` first, then writes
   `netclaw.json`. A crash between the two leaves both values, and the
   `secrets.json` value wins.
7. **Focused mutation gate.** The predicate is a security boundary. A Stryker
   target covers only the span between two marker comments.

## Risks / Trade-offs

- [An encrypted expiry from an old write stops daemon startup] -> `netclaw doctor`
  or the startup error names the provider. A new sign-in saves a plaintext date.
- [A `netclaw.json` with comments gets no automatic fix] -> The doctor report and
  startup warning name the key. The operator removes it by hand. The daemon works
  because the `secrets.json` value wins after the first save.
- [A plaintext leaf in `secrets.json`] -> The value is a date. The file stays
  owner-only and read-denied for agent tools.
- [A save fails after a good exchange] -> The service keeps the live token in
  memory and logs an error. A restart can present a spent token if the file
  stays stale. The error log tells the operator to fix the file.

## Migration Plan

1. Upgrade. A retired `netclaw.json` key keeps binding.
2. The first refresh writes the expiry to `secrets.json`. That value wins.
3. The operator runs `netclaw doctor --fix` to remove the key.

Rollback: an older build reads the expiry from `netclaw.json`. The old build
ignores the extra `secrets.json` key, and the old key in `netclaw.json` still binds.
