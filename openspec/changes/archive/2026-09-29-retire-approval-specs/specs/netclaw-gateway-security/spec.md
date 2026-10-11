## REMOVED Requirements

### Requirement: Default-deny policy

**Reason**: TA-3 states tool admission. The inbound sender and channel rule moved to `netclaw-input-adapters` Requirement "Inbound channel ACL denies by default", which states the current allow-list behavior.

**Migration**: Use TA-3; netclaw-input-adapters "Inbound channel ACL denies by default".

### Requirement: Fail-closed startup

**Reason**: The "invalid ACL schema" rule describes a removed ACL schema. TA-3 states the current startup check for audience profiles. A shell probe failure stops startup under `canonical-shell-execution`.

**Migration**: Use `tool-authorization` TA-3.

### Requirement: Controlled exposure modes

**Reason**: Already stated in `daemon-exposure`: Requirements "Exposure mode declaration", "Configurable daemon bind address", and "Startup prerequisite validation for tunnel modes".

**Migration**: None. No runtime behavior changes.

### Requirement: Privileged action approval

**Reason**: `tool-authorization` restates this rule in TA-4. That capability is the single owner of tool authorization rules.

**Migration**: Use `tool-authorization` TA-4.

### Requirement: Security audit visibility

**Reason**: Netclaw has no security audit store. TA-15 states the log lines, the attempt identifier, and the journal events that exist.

**Migration**: None. No runtime behavior changes.

### Requirement: Self-configuration safety (SEC-008)

**Reason**: TA-6 states the write-deny list (config directory, secrets, keys, database, system skills, server feeds). The `config_write` grant and the audit record do not exist.

**Migration**: Use `tool-authorization` TA-6.

### Requirement: Shell execution boundaries (SEC-009)

**Reason**: `netclaw-tools` Requirement "Shell execution tool" states the timeout (default 90 seconds, not 60), closed stdin, and output bounds. TA-6 and TA-14 state working-directory authority. Netclaw does not reject interactive commands before launch; it closes stdin.

**Migration**: Use netclaw-tools "Shell execution tool"; TA-6; TA-14.

### Requirement: Tool invocation audit

**Reason**: No queryable tool audit store exists (the audit logger was removed in #1643 and #1646). TA-15 states the log lines and the attempt identifier.

**Migration**: None. No runtime behavior changes.

### Requirement: Fail-closed reminder write validation

**Reason**: Moved unchanged in substance to `netclaw-scheduling` Requirement "Fail-closed reminder write validation". It is not a tool authorization rule.

**Migration**: Use `netclaw-scheduling` Requirement "Fail-closed reminder write validation".

### Requirement: Inbound webhook ingress safeguards

**Reason**: Moved unchanged in substance to `inbound-webhooks` Requirement "Inbound webhook ingress safeguards". It is not a tool authorization rule.

**Migration**: Use `inbound-webhooks` Requirement "Inbound webhook ingress safeguards".

### Requirement: Channel-owned interactive callback endpoint safeguards

**Reason**: Moved unchanged in substance to `netclaw-mattermost-socket` Requirement "Channel-owned interactive callback endpoint safeguards". It is not a tool authorization rule.

**Migration**: Use `netclaw-mattermost-socket` Requirement "Channel-owned interactive callback endpoint safeguards".
