## Why

Netclaw requests exact consent for a bounded glob that crosses a contained directory alias.
The current link refusal prevents ordinary repository-list commands from using the existing diagnostic policy.

## What Changes

- Resolve bounded alias reach with the existing filesystem authority rules.
- Check the original pattern and each known target's remaining pattern against protected paths.
- Preserve known protected reach when another branch makes the walk incomplete.
- Require existing folder and repository grants to cover the authored scope and every target scope.
- Keep incomplete, external, cyclic, broken, and uninspectable reach unresolved.
- Retain the 4096-unit walk bound and the existing link-hop bound.
- Apply the same facts to arguments, redirects, reviewed policy, and the resolved path view.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: TA-6 target protection, TA-7 bounded glob reach, and TA-8 target scope coverage.

## Impact

Source requirements are PRD-002 SEC-003, SEC-006, SEC-007, and SEC-009, plus PRD-006 tool policy controls.
The change affects `ShellGlobScope`, `FileSystemAuthority`, `ToolPathPolicy`, approval scope extraction, and the Actors path view.
The public API, package dependency, durable grant format, and operator configuration stay unchanged.

### Security and operational impact

The existing policy can permit a contained alias only after all applicable authority checks pass.
Target protection can refuse a call when the previous lexical glob check hid a protected target.
The change adds bounded filesystem inspection to the path screen.
The authorization-to-execution filesystem race remains.

### Scope

In scope are bounded parser-proved Bash glob arguments and redirects under the existing shell-state contract.
Out of scope are executable grammar, arbitrary glob expansion, new grant kinds, and kernel containment.
