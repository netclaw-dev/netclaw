## ADDED Requirements

### Requirement: TA-18 Bash compound redirects retain independent file authority

Netclaw SHALL check each supported Bash compound redirect as an independent file effect. An input redirect SHALL use file-read path rules. An output redirect SHALL use file-write path rules. The compound command's entry directory SHALL resolve relative targets. A command grant SHALL NOT grant access to a redirect target. A file-tool grant SHALL NOT grant a body command.

Netclaw SHALL check hard-deny rules, protected paths, trusted roots, link targets, and the file-tool mode before it requests consent. A call with no body command SHALL still pass each redirect through these checks. Consent SHALL bind the complete shell source and every file operation that needs consent. An unknown target, incomplete redirect, or unknown relative-path base on a supported compound owner SHALL be denied with `shell_redirect_unproved`. One-time consent SHALL NOT override this denial.

Netclaw SHALL check each redirect again before process start. A changed path or link target SHALL stop the launch.

#### Scenario: An input redirect uses read authority

- **GIVEN** an interactive Personal session with grants for `cat` and `file_read` within the declared roots
- **WHEN** the model calls `shell_execute` with `for item in one two; do cat "$item"; done < input.txt`
- **THEN** Netclaw resolves `input.txt` against the loop entry directory
- **AND** the file-read grant covers the redirect
- **AND** the command grant covers only the body command

#### Scenario: An output redirect outside roots denies a granted command

- **GIVEN** an interactive Personal session with a grant for `printf` and bounded write roots
- **WHEN** the model calls `shell_execute` with `while read value; do printf '%s' "$value"; done > ../outside/result.txt`
- **THEN** authorization returns `Denied` with reason `shell_path_outside_trusted_roots`
- **AND** Netclaw does not query a shell grant for the command

#### Scenario: A protected redirect denies before grant lookup

- **GIVEN** an interactive Personal session with grants for every body command
- **WHEN** a compound redirect names a protected path
- **THEN** authorization returns `Denied` with reason `shell_path_protected` or `shell_references_protected_path`
- **AND** no grant or consent answer overrides the denial

#### Scenario: A denied file-tool mode denies a compound redirect

- **GIVEN** an interactive Personal session where `file_read` or `file_write` has mode `Deny`
- **WHEN** a compound command uses the matching input or output redirect
- **THEN** authorization returns `Denied` with reason `shell_redirect_file_tool_denied`

#### Scenario: A file-tool grant does not cover a body command

- **GIVEN** an interactive Personal session with a stored grant for the file tool but no grant for the body command
- **WHEN** the file tool has mode `Approval` and the compound command redirects a file
- **THEN** the file-tool grant covers only the redirect operation
- **AND** Netclaw requests consent for the body command

#### Scenario: A legacy exact shell grant does not cover a file operation

- **GIVEN** `file_write` has mode `Approval` and the store has a legacy exact shell grant for the full compound source
- **AND** the store has no grant for the file operation
- **WHEN** the model calls `shell_execute` with a proved compound output redirect
- **THEN** Netclaw requests consent for the file operation
- **AND** the shell grant does not cover the redirect

#### Scenario: Every redirect gets an independent file check

- **GIVEN** an interactive Personal session with a bounded write root
- **WHEN** a compound command has two output redirects and the later target is outside that root
- **THEN** authorization denies the call before consent
- **AND** the earlier target does not grant authority to the later target

#### Scenario: Nested compound redirects keep their owners

- **GIVEN** an interactive Personal session with bounded read and write roots
- **WHEN** a nested compound command has one inner redirect and one outer redirect
- **THEN** Netclaw checks each redirect once against its own compound entry directory
- **AND** an inner command grant does not widen either path scope

#### Scenario: A compound redirect with no body command still needs file authority

- **GIVEN** an interactive Personal session with a bounded read root
- **WHEN** the model calls `shell_execute` with `for item in one; do value=1; done < input.txt`
- **THEN** Netclaw checks `input.txt` with file-read path rules and the file-tool mode
- **AND** Netclaw does not allow the call only because it found no body command

#### Scenario: Shell Auto does not skip file-operation consent for a compound

- **GIVEN** the shell tool has mode `Auto` and `file_write` has mode `Approval`
- **AND** no stored `file_write` grant covers the compound output target
- **WHEN** the model calls `shell_execute` with a supported compound output redirect
- **THEN** Netclaw requests one-time consent for the full source and the write operation
- **AND** shell mode `Auto` does not skip the file-operation candidate

#### Scenario: Shell Auto does not skip file consent for a compound with no body command

- **GIVEN** the shell tool has mode `Auto` and `file_write` has mode `Approval`
- **AND** no stored `file_write` grant covers the compound output target
- **WHEN** the model calls `shell_execute` with a compound that has no body command and an output redirect
- **THEN** Netclaw requests one-time consent for the full source and the write operation
- **AND** the no-command rule does not skip the file-operation candidate

#### Scenario: A command substitution in an outer target stays unresolved

- **GIVEN** an interactive Personal session
- **WHEN** a compound redirect target contains a command substitution
- **THEN** the parser keeps the whole source unparseable
- **AND** Netclaw uses the existing unresolved-source policy
- **AND** Netclaw does not create reusable candidates from partial command facts

#### Scenario: A changed redirect path stops the launch

- **GIVEN** Netclaw approved a compound redirect target
- **WHEN** the path or its resolved link target changes before process start
- **THEN** Netclaw denies launch with reason `shell_launch_paths_changed`

#### Scenario: An unknown target denies before consent

- **GIVEN** an interactive Personal session
- **WHEN** a supported compound redirect has an unknown target or an unknown relative-path base
- **THEN** authorization returns `Denied` with reason `shell_redirect_unproved`
- **AND** no one-time answer or stored grant overrides the denial

#### Scenario: A one-time answer binds the source and file operations

- **GIVEN** an interactive Personal session with no file-tool grant for a compound redirect
- **WHEN** the operator grants one-time consent for a supported, proved redirect and the full call
- **THEN** the answer does not cover a call with different body commands
- **AND** the answer does not cover a call with a different redirect target
