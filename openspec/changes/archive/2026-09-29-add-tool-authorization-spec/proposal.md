## Why

Five OpenSpec capabilities and parts of nine others state the tool
authorization rules today. They repeat one another, and some describe
mechanisms that the code no longer has (grant categories, a tool audit store,
a 60-second shell timeout, a `DirectoryRoots` prompt field). A reviewer cannot
find one place that says who decides whether a tool call runs. The tool
authorization consolidation program (decision D6) needs one set of testable
rules and one architecture document before the code slices start.

Source PRDs: PRD-002 (SEC-003, SEC-009), PRD-006 (MCP-003), PRD-001.

## What Changes

- Add the capability `tool-authorization`. It states 16 testable rules
  (TA-1 to TA-16) that describe the current, verified behavior of every tool
  call from the model to process launch. It has an authority flow, a decision
  owner table, and a verification map.
- Add `docs/architecture/tool-authorization.md`, the canonical architecture
  document for people, and `docs/architecture/README.md`. The spec purpose
  links to the document.
- State the correct current facts where older specs are wrong: the 90-second
  default tool timeout, the real `ToolInteractionRequest` fields, the six
  prompt options, the absence of grant categories and of an audit store, the
  headless result text, and the Personal-only shared session root.
- Add the authorization language to `docs/spec/GLOSSARY.md`.
- Replace `docs/spec/SPEC-003` with a pointer. Cut the approval runbook to
  operator procedures.
- Add the Architecture Document Rule and `docs/architecture/*.md` to
  `AGENTS.md` (`CLAUDE.md`).
- This change does not retire the older capabilities. A stacked change
  (`retire-approval-specs`) removes them and trims the other specs, with a
  reason for each removed requirement.

No behavior changes. No code changes.

In scope for MVP: the normative text and the documents above.
Out of scope: code changes, settings changes, and new authorization behavior.

## Capabilities

### New Capabilities

- `tool-authorization`: the single owner of the testable rules for tool
  authorization: trust context, admission, consent modes, hard deny, path
  access decisions, shell facts, coverage, agent correction, prompts, durable
  pause and resume, subagent consent, the grant store, launch
  re-verification, observability, and operator surfaces.

### Modified Capabilities

None in this change. The stacked change `retire-approval-specs` modifies and
retires the older capabilities.

## Impact

- Docs and specs only: `openspec/specs/tool-authorization/`,
  `docs/architecture/`, `docs/spec/GLOSSARY.md`, `docs/spec/SPEC-003-*`,
  `docs/spec/README.md`, `docs/spec/configuration.md`,
  `docs/runbooks/tool-approval-gates.md`, `AGENTS.md`, `CONTRIBUTING.md`,
  `IMPLEMENTATION_PLAN.md`, `docs/prd/README.md`.
- Until the stacked change merges, `tool-approval-gates` and the other older
  capabilities still exist. Where they disagree with `tool-authorization`,
  `tool-authorization` states the verified behavior.

### Security and operational impact

- Security: none at runtime. The spec states the current authority boundaries
  so that a later code PR cannot weaken one without a visible spec change.
- Operations: the runbook keeps every operator procedure (configuration, CLI,
  channels, diagnostics, FAQ). It corrects two wrong statements: the
  `channel_does_not_support_approval` reason code and the tool audit entries do
  not exist.
