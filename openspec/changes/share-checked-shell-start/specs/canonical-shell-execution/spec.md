## ADDED Requirements

### Requirement: Every routed shell launch checks current authority

Normal, streamed, and background shell calls SHALL use the same checked process start boundary.
The boundary SHALL retain the exact command, absolute cwd, shell identity, and child environment.
It SHALL check current policy before launch and reject a denied, corrected, or cancelled request without a process.
One accepted launch request SHALL start at most one process.
See the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for shared terms.

#### Scenario: Queued grant remains valid
- **GIVEN** a background command has a valid stored grant
- **WHEN** a queue slot becomes available and that grant still covers the exact call
- **THEN** the command starts once without another approval prompt

#### Scenario: Grant is revoked in the queue
- **GIVEN** a background command needs a stored grant
- **WHEN** the grant is revoked before launch
- **THEN** no process starts
- **AND** the job reports the policy failure

#### Scenario: A path changes before launch
- **GIVEN** a queued command refers to an allowed path
- **WHEN** that path becomes a link to protected storage
- **THEN** the launch fails without a process

#### Scenario: Relative cwd cannot bind the launch
- **WHEN** a shell request supplies a relative working directory
- **THEN** the request fails before process creation
- **AND** it does not inherit the daemon directory

#### Scenario: Cancellation precedes launch
- **WHEN** the process owner cancels the request before launch
- **THEN** no process starts

### Requirement: Shared startup preserves process lifetime ownership

A foreground caller SHALL retain cancellation control over its process tree.
A background actor SHALL own its process after submission.
The caller's return or cancellation after accepted submission SHALL NOT terminate that background process.
The owner SHALL retain output capture, timeout, completion, and process disposal.

#### Scenario: Background submission returns
- **WHEN** the submission call returns while its background process remains active
- **THEN** the process continues
- **AND** the owner can query and cancel the job

#### Scenario: Foreground caller cancels
- **GIVEN** a foreground command has a child process
- **WHEN** the caller cancels the command
- **THEN** the process tree exits
