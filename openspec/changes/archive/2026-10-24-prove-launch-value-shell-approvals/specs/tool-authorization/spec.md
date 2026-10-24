## ADDED Requirements

### Requirement: Legacy grant diagnostics preserve authority

A valid legacy shell grant SHALL participate in bounded phrase near-miss diagnostics.
The diagnostic SHALL use the same command identity and comparison rules that the grant matcher uses.
An explanation change SHALL NOT change candidate coverage, consent options, grant scope, or a final authorization outcome.
Invalid or unrelated entries SHALL NOT become a matching grant or a same-program explanation.
Existing reviewed single-token program exceptions SHALL keep their current meaning.

#### Scenario: A legacy assignment qualifier differs

- **GIVEN** a valid Bash legacy grant for `dotnet test` without an assignment qualifier
- **AND** a resolved candidate for those words with a non-null assignment digest
- **WHEN** the evaluator compares the candidate with that grant
- **THEN** the grant does not cover the candidate
- **AND** the bounded explanation names the assignment mismatch

#### Scenario: A legacy command phrase differs

- **GIVEN** a valid legacy Bash grant for `gh pr view`
- **WHEN** the evaluator compares a candidate for `gh pr merge` with that grant
- **THEN** the grant does not cover the candidate
- **AND** the bounded explanation names the command-word mismatch

#### Scenario: An existing reviewed program grant still covers its operands

- **GIVEN** a valid global legacy Bash grant for `grep`
- **WHEN** an otherwise permitted ordinary `grep` call uses literal operands and no assignment qualifier
- **THEN** the diagnostic repair preserves the existing grant match
- **AND** it creates no new authority for another program

#### Scenario: A scope mismatch remains separate

- **GIVEN** a phrase-compatible grant whose folder does not contain the candidate path
- **WHEN** the evaluator compares the candidate with that grant
- **THEN** the result retains the scope mismatch
- **AND** the phrase diagnostic does not authorize the path

### Requirement: A proved launch-value assignment retains its authority qualifier

A launch-derived assignment SHALL become reusable only when its exact source, effective value, and effects have complete proof.
The proof SHALL use the same supported fresh-host facts and launch snapshot as execution.
Unknown or incomplete facts SHALL retain exact consent.
The assignment identity SHALL retain the shell, name, scope, environment-impact flag, and exact effective value.
An unqualified grant SHALL NOT cover a qualified assignment.
Changed effective values SHALL need separate authority.
The proof SHALL NOT bypass hard deny, protected paths, link checks, audience rules, scope checks, or launch checks.
The implementation SHALL use generic shell facts and SHALL NOT special-case an executable or environment-variable name.

#### Scenario: A launch-derived value matches its literal identity

- **GIVEN** a supported fresh Bash host with a proved launch `HOME` of `/home/test`
- **AND** complete source and effect proof for `export DOTNET_ROOT=$HOME/.dotnet; dotnet test ./sample.csproj`
- **WHEN** Netclaw creates the assignment-qualified candidate
- **THEN** its assignment identity equals the literal-value form with `/home/test/.dotnet` under the same shell and export state
- **AND** a grant without that assignment identity does not cover it
- **AND** all normal file and grant-scope checks still apply

#### Scenario: A changed value does not inherit consent

- **GIVEN** a grant qualified by an exact effective assignment value
- **WHEN** the same command receives a different proved effective assignment value
- **THEN** that grant does not cover the candidate

#### Scenario: Unknown launch or source facts keep exact consent

- **GIVEN** an unsupported host, absent launch proof, or incomplete source effects
- **WHEN** a command assigns a value through an expansion
- **THEN** the launch-value rule adds no reusable authority
- **AND** the existing exact-consent or denial result remains

#### Scenario: A substitution cannot hide a denied command

- **WHEN** an assignment contains a command substitution that invokes a hard-denied command
- **THEN** the hard denial wins
- **AND** an exact effective value or a saved grant cannot bypass it

#### Scenario: A launch-derived protected path remains protected

- **GIVEN** complete launch-value proof for an operand that names a protected path
- **WHEN** a command uses that operand
- **THEN** the existing protected-path policy still decides the operation
- **AND** assignment qualification supplies no additional file authority

#### Scenario: Other environment variables retain their qualifiers

- **WHEN** an assignment to `PATH`, `LD_PRELOAD`, or `GIT_DIR` can reach a program
- **THEN** the grant identity retains that assignment's environment effect
- **AND** an unqualified grant does not cover the changed environment
