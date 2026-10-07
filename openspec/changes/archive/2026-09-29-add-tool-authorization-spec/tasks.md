## 1. Architecture document

- [x] 1.1 Add `docs/architecture/tool-authorization.md` with the lifecycle, contexts, end-to-end sequence, language, guidelines, extension recipes, future scenarios, proof map, and decision history.
- [x] 1.2 Add `docs/architecture/README.md` and explain how the folder differs from the other docs folders.
- [x] 1.3 Check each owner and fact in the document against source at `0f1991ed0`.

## 2. Testable rules

- [x] 2.1 Add the `tool-authorization` delta with Purpose, Authority Flow, Decision Owners, Verification Map, and requirements TA-1 to TA-16.
- [x] 2.2 Give each new rule at least one positive and one negative scenario.
- [x] 2.3 Run `openspec validate add-tool-authorization-spec --strict`.

## 3. Language and pointers

- [x] 3.1 Add the Authorization Language section to `docs/spec/GLOSSARY.md` and move the trusted-root and path-access-decision owner to `tool-authorization`.
- [x] 3.2 Replace `docs/spec/SPEC-003-acl-policy-and-security-controls.md` with a pointer and update `docs/spec/README.md`.
- [x] 3.3 Correct the Public and Team defaults and the shared sessions root in `docs/spec/configuration.md`.
- [x] 3.4 Cut `docs/runbooks/tool-approval-gates.md` to operator procedures and link the architecture document.

## 4. Constitution and links

- [x] 4.1 Add `docs/architecture/*.md` to "Read first" and add the Architecture Document Rule to `AGENTS.md`.
- [x] 4.2 List `docs/architecture/` in `CONTRIBUTING.md`.
- [x] 4.3 Point `IMPLEMENTATION_PLAN.md`, `docs/prd/README.md`, and `docs/spec/SPEC-011-daemon-architecture.md` at the new artifacts.

## 5. Verification

- [x] 5.1 Check every relative link in the changed Markdown files.
- [x] 5.2 Review the Mermaid blocks.
- [x] 5.3 Confirm that the diff changes no production or test code.
