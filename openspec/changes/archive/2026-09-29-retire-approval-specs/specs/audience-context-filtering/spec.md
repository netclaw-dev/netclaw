## REMOVED Requirements

### Requirement: Public audience has no implicit internal file roots

**Reason**: `tool-authorization` restates this rule in TA-6. That capability is the single owner of tool authorization rules. Global read roots never apply to `Public`.

**Migration**: Use `tool-authorization` TA-6.
