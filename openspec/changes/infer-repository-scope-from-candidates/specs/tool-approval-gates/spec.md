## MODIFIED Requirements

### Requirement: Repository grants cover registered Git worktrees only by explicit choice

Netclaw SHALL offer a distinct repository scope only for a clean reusable shell phrase.
Each grant-bearing candidate's effective directory SHALL be its structured directory or the request working directory when that directory is absent.
Every grant-bearing effective directory SHALL resolve to a registered worktree under one canonical Git common directory.
Different registered sibling worktrees under that identity SHALL be eligible in one request.
The request working directory SHALL not constrain a candidate that has its own directory.
An approval-exempt pure side-effect candidate SHALL NOT establish or suppress repository identity.
Netclaw SHALL use no raw command text to infer repository identity.
The grant SHALL bind to the canonical repository identity and the approved verb phrase.
Netclaw SHALL recheck candidate scope at the prompt, grant response, persistence, and reuse boundaries.
It SHALL store each grant with the candidate-derived worktree root.
It SHALL reject a changed, moved, forged, linked, missing, or mixed repository scope.
Netclaw SHALL recheck directory, path, audience, hard-deny, and protected-path rules for each call.
Existing folder grants SHALL remain path-scoped and SHALL not gain repository authority.
Netclaw SHALL omit this scope for a main checkout that uses `--separate-git-dir`.

#### Scenario: A repository grant covers a sibling worktree

- **GIVEN** a repository grant covers `./scripts/release.sh` in the main checkout
- **AND** Git registers a sibling worktree under the same canonical repository identity
- **WHEN** the agent calls that verb from the sibling worktree
- **THEN** Netclaw can reuse the repository grant after all other policy checks pass

#### Scenario: A repository grant covers a nested worktree directory

- **GIVEN** a repository grant covers a registered sibling worktree
- **WHEN** the agent calls that phrase from a nested directory in the same worktree
- **THEN** Netclaw can reuse the grant after each grant-bearing candidate path passes the scope checks

#### Scenario: Candidate scope supplies repository identity

- **GIVEN** the request working directory is outside every Git repository
- **AND** every grant-bearing candidate has an effective directory in one registered worktree
- **WHEN** Netclaw creates the approval prompt
- **THEN** Netclaw offers `This repository`
- **AND** it derives the repository identity from the candidate scopes

#### Scenario: Candidates can use registered sibling worktrees

- **GIVEN** two candidates have effective directories in separate registered worktrees
- **AND** both worktrees use one canonical Git common directory
- **WHEN** Netclaw creates the approval prompt
- **THEN** Netclaw offers `This repository`

#### Scenario: A candidate without a directory uses the request working directory

- **GIVEN** one candidate has no directory and another targets a registered sibling worktree
- **AND** the request working directory belongs to the same canonical repository
- **WHEN** Netclaw creates the approval prompt
- **THEN** Netclaw can offer `This repository`

#### Scenario: A missing candidate directory prevents repository scope

- **GIVEN** one candidate has no directory
- **AND** the request working directory is outside every Git repository
- **WHEN** another candidate targets a registered worktree
- **THEN** Netclaw omits `This repository`

#### Scenario: A pure side effect does not suppress repository scope

- **GIVEN** the request working directory is outside every Git repository
- **AND** each grant-bearing candidate targets one registered repository
- **WHEN** the command also contains a pure output side effect without a path
- **THEN** Netclaw can offer `This repository`

#### Scenario: An output redirect remains grant-bearing

- **GIVEN** each executable candidate targets one registered repository
- **WHEN** an output side effect redirects to a directory outside that repository
- **THEN** Netclaw omits `This repository`

#### Scenario: Mixed repositories prevent repository scope

- **GIVEN** two candidates resolve to registered worktrees under different Git common directories
- **WHEN** Netclaw creates the approval prompt
- **THEN** Netclaw omits `This repository`

#### Scenario: A folder grant does not cross to a sibling worktree

- **GIVEN** a folder grant covers `./scripts/release.sh` below the main checkout
- **WHEN** the agent calls the same phrase from a sibling worktree outside that directory
- **THEN** Netclaw requests approval or denies the call

#### Scenario: An unregistered lookalike cannot use a repository grant

- **GIVEN** a repository grant belongs to one Git repository
- **WHEN** an unrelated directory presents a `.git` pointer without a matching worktree registration
- **THEN** Netclaw does not use the repository grant

#### Scenario: A linked candidate cannot establish repository scope

- **GIVEN** a candidate directory crosses a symbolic link into a registered worktree
- **WHEN** Netclaw creates the approval prompt or checks a stored grant
- **THEN** Netclaw rejects repository scope for that candidate

#### Scenario: A separate Git directory does not offer a repository grant

- **GIVEN** a main checkout uses `--separate-git-dir`
- **WHEN** the operator reviews a shell approval request in that checkout
- **THEN** Netclaw omits the repository grant choice

#### Scenario: A repository identity changes after the prompt

- **GIVEN** Netclaw offers a repository grant for repository A
- **WHEN** a candidate worktree points to repository B before the operator chooses that grant
- **THEN** Netclaw rejects the grant and stores no repository approval

#### Scenario: An unrelated working directory can reuse a repository grant

- **GIVEN** a stored repository grant covers a reusable phrase
- **AND** the request working directory is outside every Git repository
- **WHEN** the candidate targets a registered worktree with the stored repository identity
- **THEN** Netclaw can reuse the grant after all other policy checks pass

#### Scenario: Other verbs in one shell call keep their own grants

- **GIVEN** a repository grant covers `./scripts/release.sh`
- **WHEN** the same shell call also invokes an ungranted executable
- **THEN** Netclaw requests approval or denies the ungranted occurrence
