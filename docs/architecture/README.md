# Architecture Documents

This folder holds the canonical architecture documents for people. A document
here explains how one area of Netclaw works, who owns each decision, and how to
change the area safely.

## Documents

| Document | Area |
| --- | --- |
| [tool-authorization.md](tool-authorization.md) | Every tool call from the model to process launch: admission, shell facts, filesystem authority, prohibition, consent, and consent delivery. |

## What a document here contains

- Diagrams. Use Mermaid blocks. GitHub renders them without a build step.
- The decision owners in the current code, with file paths.
- Architecture guidelines. Each guideline has one example that follows it and
  one example that breaks it.
- Recipes for common extensions. Each recipe names the tests to add.
- Future scenarios and their level of support.
- Links to the testable rules. A document does not copy those rules.

## Rules that keep a document true

- The document describes the current code. A planned shape appears only in the
  "Future scenarios" section or in a note that has the label "Planned".
- A PR that moves a decision owner updates the document in the same diff. See
  the Architecture Document Rule in [`AGENTS.md`](../../AGENTS.md).
- Use the terms in [the engineering glossary](../spec/GLOSSARY.md). Link a
  term. Do not copy its definition.

## How this folder differs from the other docs folders

| Folder | Job | Audience |
| --- | --- | --- |
| `docs/architecture/` | How an area works now, and how to extend it. | Maintainers, reviewers, contributors |
| `openspec/specs/` | Testable rules (requirements and scenarios) for each capability. | Agents, reviewers, OpenSpec tooling |
| `openspec/changes/` | Proposals, designs, and tasks for one change. | The author and reviewers of that change |
| `docs/prd/` | Product requirements and acceptance criteria. | Product owner, planners |
| `docs/spec/` | Older implementation specifications and the glossary. | Engineers |
| `docs/adr/` | Past architecture decisions and their reasons. | Anyone who asks "why" |
| `docs/runbooks/` | Operator procedures: configuration, CLI, diagnostics. | Operators |
| `docs/research/` | Studies and explorations. They are not normative. | Planners |

When two sources disagree, the code is the fact. Fix the architecture document
and the OpenSpec rule in the same PR.
