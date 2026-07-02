## ADDED Requirements

### Requirement: Fixed approval option labels

The system SHALL render approval option labels using fixed verb-only strings sourced from `ApprovalOptionKeys` (`Approve once`, `Approve for this chat`, `Approve always`, `Deny`), independent of the tool, command, or directory root being approved. Labels SHALL NOT interpolate runtime values such as paths, command text, or tool names.

This requirement applies uniformly across all interactive channel adapters (Slack, Discord). Channel adapters MAY surface tool, command, and directory-scope context in the message body (section blocks, summary text), but SHALL NOT embed that context inside button-text labels.

The fixed-label requirement exists to keep button text within platform hard limits (Slack `PlainText` button text: 76 characters; Discord button label: 80 characters). Compile-time constants in `ApprovalOptionKeys` are ≤21 characters and are structurally incapable of breaching either limit.

#### Scenario: Directory-scoped shell approval uses fixed labels

- **GIVEN** a shell command `grep "error" /home/.netclaw/logs/app.log` requires approval
- **AND** directory-root extraction yields a reusable root `/home/.netclaw/logs/`
- **WHEN** the approval prompt is generated
- **THEN** option B reads the default `"Approve for this chat"`
- **AND** option C reads the default `"Approve always"`
- **AND** the directory root `/home/.netclaw/logs/` is conveyed to the user via the message body, not the button text

#### Scenario: Long directory path does not produce oversized button text

- **GIVEN** a shell command requires approval inside a deeply-nested directory whose absolute path exceeds 76 characters
- **WHEN** the approval prompt is generated
- **THEN** every option label SHALL be ≤21 characters
- **AND** the channel adapter SHALL post the prompt successfully without `invalid_blocks` or any platform rejection caused by button-text length

#### Scenario: Multi-root shell approval uses fixed labels

- **GIVEN** a shell command requires approval that yields more than one reusable directory root
- **WHEN** the approval prompt is generated
- **THEN** option B reads the default `"Approve for this chat"`
- **AND** option C reads the default `"Approve always"`
- **AND** the full root list is conveyed to the user via the message body

#### Scenario: Non-shell tool approval uses fixed labels

- **GIVEN** any tool other than `shell_execute` requires approval
- **WHEN** the approval prompt is generated
- **THEN** all four option labels are the `ApprovalOptionKeys` defaults

## REMOVED Requirements

### Requirement: Dynamic approval option labels

**Reason**: The previous requirement mandated interpolating the directory root scope into button labels (`"Approve in {directory-root} for this chat"`, `"Approve in {directory-root} always"`). For realistic project paths the resulting label exceeded Slack's 76-character button-text limit, causing Slack to reject the message with `invalid_blocks`. The channel post failed and Netclaw's safety-net auto-deny path ran, presenting the user with a silent self-deny for a tool call they never had a chance to approve. Discord had the same exposure with its 80-character button cap.

**Migration**: Replaced by **Fixed approval option labels** above. The directory-scope context that was embedded in the button label is already conveyed in the message body (Slack `*Directory Roots*` section block, Discord `**Directory Root:**` summary line) so no user-visible information is lost — only the platform-rejection failure mode. No data migration is required: persisted approvals key off `ApprovalEntries` (the directory roots), not button labels.
