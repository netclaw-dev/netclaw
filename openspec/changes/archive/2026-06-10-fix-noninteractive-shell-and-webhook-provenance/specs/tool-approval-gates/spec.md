## ADDED Requirements

### Requirement: Non-interactive shell path validation uses audience-scoped file access

For non-interactive channels (`SupportsInteractiveApproval == false`), the system SHALL authorize a shell command's path arguments and working directory through the same audience-scoped file-access resolution used by write-capable file tools (`file_write`, `file_edit`). A single interpretation of the audience's write filesystem mode SHALL govern both surfaces:

- `Mode == All` ⇒ paths are unrestricted (authorized).
- `Mode == Roots` ⇒ each path MUST resolve within the audience's configured roots,
  otherwise the command is denied.
- `Mode == None` ⇒ path-bearing shell commands are denied.

The system SHALL NOT maintain a separate roots-listing interpretation that treats
an unrestricted (`Mode == All`) audience as having zero roots and denies on that
basis. Shell authorization remains subject to the hard-deny list, the
protected-path policy, and the approval gate, which are evaluated independently of
this check.

#### Scenario: Unrestricted audience non-interactive shell proceeds to approval

- **GIVEN** a non-interactive channel resolving to an audience whose write filesystem mode is `All`
- **WHEN** a shell command with a path argument is authorized
- **THEN** trust-zone validation does not deny the command
- **AND** authorization proceeds to the approval gate, which allows it only if the verb chain is pre-approved or safe-listed

#### Scenario: Roots-scoped audience confines shell paths

- **GIVEN** a non-interactive context whose write filesystem mode is `Roots` with a configured root
- **WHEN** a shell command references a path outside the configured roots
- **THEN** the command is denied with `shell_path_outside_trust_zone`

#### Scenario: Roots-scoped audience allows in-root shell paths

- **GIVEN** a non-interactive context whose write filesystem mode is `Roots` with a configured root
- **WHEN** a shell command references a path inside a configured root
- **THEN** trust-zone validation does not deny the command
- **AND** authorization proceeds to the approval gate

#### Scenario: Working directory outside roots is denied

- **GIVEN** a non-interactive context whose write filesystem mode is `Roots`
- **WHEN** a shell command specifies a working directory outside the configured roots
- **THEN** the command is denied with `shell_working_directory_outside_trust_zone`
