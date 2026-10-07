## REMOVED Requirements

### Requirement: set_working_directory adds a project trusted root

**Reason**: `tool-authorization` restates this rule in TA-6. That capability is the single owner of tool authorization rules. `session-cwd` Requirement "set_working_directory tool" keeps the declaration behavior.

**Migration**: Use `tool-authorization` TA-6.
