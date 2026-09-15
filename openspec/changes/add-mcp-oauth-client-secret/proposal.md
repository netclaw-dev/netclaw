## Why

PRD-006 MCP-001 and MCP-010 require secure MCP configuration and an SDK-owned OAuth lifecycle.
Netclaw cannot use pre-registered confidential clients because it cannot configure an OAuth client secret.

## What Changes

- Add an optional sensitive OAuth client secret to each MCP server profile.
- Add `--client-secret` to `netclaw mcp add`.
- Reject a client secret without a client ID before the CLI writes configuration.
- Store the configured secret only in encrypted `secrets.json` data.
- Supply the configured identity to the MCP SDK for token exchange and refresh.
- Keep a configured secret authoritative across token refresh and daemon restart.
- Add a programmable OAuth server case for confidential-client authentication.

The change does not alter dynamic client registration.
The change does not add a second OAuth protocol implementation.
The change does not solve explicit authorization for anonymous MCP discovery from #2123.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `mcp-oauth`: Support a securely configured confidential client through the existing SDK OAuth lifecycle.

## Impact

The change affects MCP configuration, CLI persistence, OAuth cache identity, daemon runtime options, tests, schema, and operator guidance.

### Security impact

The configured secret stays encrypted at rest.
The CLI and diagnostics never print the secret.
The MCP server profile remains the authority for a configured secret.
Dynamic registration credentials remain in the OAuth token record.

### Operational impact

Operators can connect Netclaw to GitHub and Google OAuth Apps that require a client secret.
Existing public clients and dynamic registrations continue to work without configuration changes.
