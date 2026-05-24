## Context

This change defines the reusable leaf-editor contract that both
bootstrap-time init flows and the later `netclaw config` command will use.
The locked product shape matters:

- `netclaw init` is bootstrap, not the main editor.
- `netclaw config` is the main post-install surface.
- Identity stays init-owned.

So the abstraction should model reusable leaf editors and semantic writes,
not a specific top-level dashboard layout.

## Goals / Non-Goals

**Goals:**

- Define `ISectionEditor` as the reusable leaf-editor contract.
- Support init-owned editors and config-owned editors without forcing them
  all into one menu.
- Preserve existing config semantically on save, including inactive
  exposure-mode values and unrelated sections.
- Keep secrets masked and non-rehydratable.
- Refactor the bootstrap leaves that matter to the locked split:
  Provider, Identity, Security Posture, Enabled Features.

**Non-Goals:**

- Defining the `netclaw config` IA.
- Making Identity editable from `netclaw config`.
- Forcing all schema sections into TUI editors.
- Byte-identical JSON preservation.

## Decisions

### D1. The abstraction is for leaf editors, not dashboard IA

`ISectionEditor` describes the smallest reusable editable surface. The next
change may compose those leaves under domain pages such as `Channels` or
`Security & Access`, or route specific nodes to existing commands such as
`netclaw provider` and `netclaw model`.

Alternative considered: make the registry shape equal the config dashboard
shape. Rejected because the locked IA is domain-oriented and heavier on
sub-pages, while the reusable abstraction is leaf-oriented.

### D2. Merge-on-save is semantic, not byte-identical

The merge layer preserves the meaning of unrelated sections and inactive
values, but ordering, whitespace, and exact serialized shape are not part of
the contract. Tests compare semantics, not raw file bytes.

Alternative considered: keep byte-identical guarantees. Rejected because the
locked product decisions explicitly require semantic round-tripping and
inactive-value preservation without turning formatting into a compatibility
surface.

### D3. Existing config loading supports init-owned re-entry, not init-as-editor

`WizardContext.ExistingConfig` is populated when an init-owned flow needs to
re-enter existing state. This supports things like identity re-entry and
shared bootstrap leaves, but does not commit the product to "re-run init to
edit everything".

Alternative considered: frame this change as full init reentrancy. Rejected
because the locked split moves ongoing editing to `netclaw config`.

### D4. Identity is synthetic and permanently init-owned in this branch

Identity spans config plus generated identity files, so it keeps a synthetic
`SectionId` and `ShowInMenu = false`. The config dashboard must not surface
Identity as just another settings page.

### D5. Enabled Features is a separate reusable leaf from Security Posture

Security Posture, Enabled Features, and Audience Profiles are distinct
concepts. This change therefore refactors posture and enabled-features as
separate leaves rather than encoding runtime feature enablement inside the
posture editor.

### D6. Audit scope is registered leaf editors only

Registered leaf editors require round-trip tests and validation contracts.
Future routed handoff entries are not leaf editors and only need shallow
routing coverage in the config command change.

## Risks / Trade-offs

- Refactoring four existing bootstrap leaves can regress init behavior.
  Mitigation: keep init smoke coverage and add leaf-level round-trip tests.
- Semantic merge assertions are less strict than byte equality.
  Mitigation: test meaningful preservation of unrelated values,
  hidden/inactive values, and secrets behavior.
- A synthetic Identity editor can be confusing to reviewers.
  Mitigation: keep the exemption entry explicit and document that Identity
  remains init-owned.

## Migration Plan

1. Land the abstraction and the four leaf refactors.
2. The next change composes those leaves into the domain-oriented
   `netclaw config` dashboard.
3. The third change constrains `netclaw init` to bootstrap-only behavior.

## Open Questions

None. The abstraction is intentionally narrower after the locked product
decisions.
