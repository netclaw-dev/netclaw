# Findings: Preserve the catalog after an invalid refresh

## Scope

- Accepted objective: Reject duplicate identifiers before publication. Preserve valid record order.
- Source revision: sha256:461ed4a9e341951f1e05ff03ea2d87ace614a9facd625333960c53fd3db8e81b
- Permitted workspace and actions: Read the fixture source. Write this report. Do not change the source.
- Inputs: source/catalog.py
- Constraints: Preserve exact identifier equality until the user decides otherwise.

## Evidence

| ID | Source and revision | Observation | Limits |
|---|---|---|---|
| E1 | source/catalog.py:7-8@sha256:461ed4a9e341951f1e05ff03ea2d87ace614a9facd625333960c53fd3db8e81b | `self.records = candidate` precedes `self._validate_unique(candidate)`. | This fact applies to the fixture source. |
| E2 | source/catalog.py:16-17@sha256:461ed4a9e341951f1e05ff03ea2d87ace614a9facd625333960c53fd3db8e81b | `def read(self):` returns `return self.records`. | This fact does not prove concurrent access behavior. |

## Findings

| ID | Finding | Component owner | Evidence IDs | Status |
|---|---|---|---|---|
| F1 | Duplicate validation can fail after the catalog publishes the invalid candidate. | Catalog.refresh | E1 | confirmed |
| F2 | The catalog reader exposes the stored record order. Preserve that order for a valid refresh. | Catalog.read | E2 | confirmed |

## Risks And Preserved Behavior

- Risk: A rejected candidate replaces the prior records. Evidence: E1. Check: C1.
- Preserved behavior: A valid candidate retains record order. Evidence: E2. Check: C2.

## Unknowns And Open Decisions

- Q1: Should identifier comparison ignore case? Decision owner: User. Needed evidence: The user's accepted identifier rules.

## Artifact And Check Receipts

- Complete artifact paths: findings.md
- Checks: Source inspection at sha256:461ed4a9e341951f1e05ff03ea2d87ace614a9facd625333960c53fd3db8e81b; no candidate test ran.
- Confirmed effects: This report exists. The source remains unchanged.
- Unknown effects or incomplete work: Implementation and runtime proof remain open.
