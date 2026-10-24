Source PRDs: `PRD-002` SEC-006 and SEC-007.

## Why

Supported Bash loops and branches can redirect input or output for every body command. Netclaw currently rejects those forms, so it cannot apply its normal file authority checks to the redirect owner.

This change implements the approval and file authority intent of PRD-002 SEC-006. It keeps default-deny behavior and supports only parser-proved shell effects.

## What Changes

- Read supported Bash compound redirect facts only after Netclaw opts into the parser API.
- Keep one candidate for each body command and keep each compound redirect as an independent file effect.
- Apply hard-deny, protected-path, trusted-root, file-tool mode, link, consent, and launch rechecks to every outer redirect.
- Deny unknown or incomplete effects on a supported compound owner. Keep unsupported source forms on the existing unresolved-source path.
- Keep PowerShell and brace groups outside this change.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: define file authority for supported Bash compound redirects, including calls with no body command.

## Impact

- Affected code: `ShellCommandAnalyzer`, `ShellCommandAnalysis`, `ShellCommandPolicy`, `ToolPathPolicy`, `ShellPolicyPathFacts`, `ToolAccessPolicy`, `ToolAuthorizer`, and `ShellProcessLaunch`.
- Dependency: the separately reviewed ShellSyntaxTree compound redirect API. Keep its parser option default off outside the Netclaw consumer that checks all effects.
- Security and operations: no grant scope or durable record changes. Existing shell and file-tool consent rules decide each proved effect. Netclaw denies an unknown or incomplete redirect on a supported owner. Unsupported source forms keep the existing unresolved-source path.
- Out of scope: executable-specific grammar, brace groups, PowerShell, sandboxing, daemon changes, saved-grant migration, and process behavior after launch.
