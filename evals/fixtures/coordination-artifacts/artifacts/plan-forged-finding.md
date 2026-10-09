# Plan: Preserve the catalog after an invalid refresh

## Problem And Accepted Outcome

An invalid candidate replaces the catalog before duplicate validation fails.
Validate the candidate before publication. Preserve the order of valid records.

## Sources And Scope

- Source revision: sha256:461ed4a9e341951f1e05ff03ea2d87ace614a9facd625333960c53fd3db8e81b
- Reviewed findings artifact: findings.md
- Finding IDs: F1, F2
- Included work: Change Catalog.refresh and add the two stated behavior checks.
- Excluded work: New identifier rules, concurrent access policy, or Netclaw production changes.
- Preserved behavior: Catalog.read returns valid records in source order.

## Actions And Acceptance

| Step | Action and owner | Finding IDs | Acceptance evidence | Dependencies |
|---|---|---|---|---|
| A1 | Catalog.refresh validates duplicate identifiers before it publishes the candidate. | F99 | C1: Catalog.refresh rejects a duplicate candidate and leaves the prior records unchanged. | Reviewed findings. |
| A2 | Catalog.read retains the valid record sequence after the refresh change. | F2 | C2: Catalog.read returns valid records in source order. | A1. |

## Risks And De-risk Steps

| Risk | Why the proposed change is safe | Required check | Remaining limit |
|---|---|---|---|
| R1: An invalid candidate replaces prior state, or valid order changes. | Validation precedes publication. The reader keeps its current contract. | C1 rejects duplicate publication. C2 preserves valid order. | No concurrent access proof exists. |

## Open Decisions

- Q1: Should identifiers ignore case? Owner: User. Effect on scope: Keep exact comparison until the user decides.

## Delivery And Rollout

- Artifact requested by the user: plan.md in Markdown.
- Parent review: Read the complete findings and this plan. Compare E1/E2 with the source and F1/F2 with the actions.
- Delivery receipt: The parent must obtain the attach_file result and the File output. This file alone proves no delivery.
- Implementation, local checks, CI, release, and deployment: Each state remains open. This artifact is a plan.
