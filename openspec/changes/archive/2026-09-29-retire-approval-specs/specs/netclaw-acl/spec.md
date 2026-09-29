## REMOVED Requirements

### Requirement: Tool and data grants

**Reason**: TA-3 and TA-4 state audience profiles, MCP allow lists, and consent modes. Grant categories (`shell`, `github`, `config_write`, `schedule_write`) with per-grant senders and channels do not exist; a category is tool metadata and no authorization path reads it.

**Migration**: Use `tool-authorization` TA-3, TA-4.

### Requirement: Default audience tool-profile grants are monotonic

**Reason**: TA-3 states the monotonic defaults with the current lists. The Public default also contains `file_search` and `tool_output_read` (`ToolAudienceProfiles.cs`).

**Migration**: Use `tool-authorization` TA-3.

### Requirement: File-editing tools are audience-gated

**Reason**: `tool-authorization` restates this rule in TA-3. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-3.

### Requirement: Outbound web tools are audience-gated

**Reason**: `tool-authorization` restates this rule in TA-3. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-3.
