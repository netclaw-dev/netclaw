## Modified Requirement: Shell command pattern matching

The system SHALL extract verb-chain prefix patterns from shell commands
using tokenization. The verb chain SHALL consist of non-flag tokens from
the start of the command until the first flag (`-`), path, URL, or bare
integer argument. Extraction is greedy: bare-word operands that are
neither flags, paths, URLs, nor integers (subcommands, remote names,
branch names, refs) SHALL remain in the verb chain — the extractor SHALL
NOT attempt to distinguish subcommands from positional operands.

> **Why integers are excluded:** Bare integers (pure digit sequences like
> `123`, `8080`, `30`) are never CLI subcommands. They represent
> call-specific values — ticket IDs, port numbers, timeouts — that vary
> between invocations of the same verb chain. Baking them into the pattern
> produces overly-specific approval entries that do not generalize:
> `freshdesk ticket get 123` vs `freshdesk ticket get 456` would create
> two unrelated entries, forcing separate approval for each unique value.

#### Scenario: Verb chain strips bare integer positional argument

- **GIVEN** the command `freshdesk ticket get 123`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `freshdesk ticket get`
- **AND** the bare integer `123` is excluded because it is call-specific

#### Scenario: Verb chain generalizes across different integer values

- **GIVEN** commands `nc host 8080` and `nc host 9090`
- **WHEN** patterns are extracted for both
- **THEN** both produce the same pattern `nc host`
- **AND** approval granted for one integer value covers all values of the same verb chain

#### Scenario: Verb chain terminates at integer (not just skips it)

- **GIVEN** the command `timeout 30 curl http://example.com`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `timeout`
- **AND** everything after the integer (including wrapped subcommands like `curl`) is dropped from the pattern

#### Scenario: Non-bare numeric tokens are preserved

- **GIVEN** the command `docker run --name test123 --port=8080 -e VAR=1e5`
- **WHEN** the pattern is extracted
- **THEN** the pattern includes `test123`, `--port=8080`, and `VAR=1e5`
- **AND** only pure digit-only tokens are treated as integers

#### Scenario: Flag values containing digits are preserved

- **GIVEN** the command `timeout -k 5 kill -SIGTERM 1234`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `timeout -k`
- **AND** the flag `-k` is preserved (starts with `-`, not a bare integer) while the integer `5` terminates the chain
