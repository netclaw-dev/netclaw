## Context

See `proposal.md` for the problem and scope.
The MCP SDK owns token exchange, refresh, and bearer injection.
Netclaw supplies the client identity through the SDK OAuth options.
The current profile can supply only a client ID.
The current token cache suppresses its stored secret when a profile pins that ID.

## Goals / Non-Goals

**Goals:**

- Keep the configured client secret encrypted at rest.
- Preserve one authority for each configured secret.
- Supply the same configured identity during exchange, refresh, and restart.
- Prove the full path with a programmable OAuth server.

**Non-Goals:**

- Replace the SDK OAuth protocol implementation.
- Change dynamic client registration.
- Add an interactive secret prompt.
- Fix the anonymous-discovery flow from #2123.

## Decisions

### Store the secret in the MCP profile secrets section

The configuration model will use a sensitive value for `OAuthClientSecret`.
The CLI will write it only to `McpServers.<name>.OAuthClientSecret` in `secrets.json`.
The normal configuration overlay will decrypt the value before daemon configuration binds.

This location keeps the client ID and secret in one logical profile.
It also reuses the existing MCP secret path for headers and environment variables.

An OAuth token record will not store a configured secret.
That record will continue to store secrets from dynamic registration.
This distinction prevents a stale token record from overriding a rotated profile secret.

### Reject an unpaired secret before persistence

`netclaw mcp add` will accept `--client-secret` only with `--client-id`.
The command will validate this relation before it writes either configuration file.
A client ID without a secret will remain valid for public clients.

An interactive prompt would reduce process-list exposure.
This change keeps the existing argument convention that MCP headers and environment secrets use.

### Carry the configured identity through the existing token cache

The token cache will receive both configured identity values.
It will identify the configured secret as profile-owned data.
SDK options and cached token containers will receive that in-memory identity.

The following flow is schematic.
It omits HTTP policy checks and candidate lifecycle gates.

```text
CLI --client-id + --client-secret
    -> netclaw.json: OAuthClientId
    -> secrets.json: encrypted OAuthClientSecret
    -> configuration overlay
    -> MCP profile
    -> token cache identity
    -> SDK OAuth options
    -> SDK token exchange or refresh
```

The CLI owns validation and durable writes.
Its parsed secret is call-local before persistence.
The MCP profile owns the durable configured secret.
The token cache holds a daemon-local decrypted value.
The OAuth token record owns durable tokens and dynamic registration credentials.

### Prove behavior at the SDK boundary

The existing programmable OAuth server will require confidential-client token authentication.
It will reject a missing or incorrect secret.
The test will use the production manager, SDK transport, and credential store.

The CLI tests will verify encryption, merge behavior, redaction, and pre-write rejection.
The credential tests will verify identity selection and token-record ownership.

## Risks / Trade-offs

- [Risk] A command-line secret can appear in process inspection. → The help text will identify the value as sensitive.
- [Risk] A stale token record can contain an older client secret. → The configured profile identity always wins.
- [Risk] A secret can bind as ciphertext. → A configuration test will verify decryption through the production overlay.
- [Risk] A token refresh can lose the configured identity. → Restart coverage will inspect the SDK token request.

## Migration Plan

The schema change is additive.
Existing public clients and dynamic registrations require no migration.
Operators can add a secret by re-running `netclaw mcp add` with both client options.
Rollback removes confidential-client support but leaves the encrypted secret data intact.
