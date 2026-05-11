## 1. Session and adapter metadata contract

- [x] 1.1 Extend the threaded-adapter to session handoff model so it carries both `HasAdoptedContext` and `HasThirdPartyAdoptedContext`, with deterministic derivation from the adopted sender-id set and current authorized sender.
- [x] 1.2 Update adopted-context persistence models and serializers so persisted records keep the full adopted sender-id provenance plus the separate third-party-adopted policy field without reinterpreting self-only adopted history as empty.
- [x] 1.3 Audit recovery and retry paths that reuse adopted-context records to ensure they preserve the clarified semantics instead of recomputing an inconsistent policy view.

## 2. Memory policy alignment

- [x] 2.1 Update automatic memory-formation policy inputs so suppression/caution logic keys off `HasThirdPartyAdoptedContext` rather than `HasAdoptedContext`.
- [x] 2.2 Keep explicit-elevation behavior unchanged: the current authorized message may still elevate adopted facts under existing memory policy rules.
- [x] 2.3 Add focused tests covering no adopted window, self-only adopted window, and third-party adopted window for automatic memory formation.

## 3. Approval and audit provenance alignment

- [x] 3.1 Update approval prompt and stored approval-context builders so any non-empty adopted window sets `HasAdoptedContext` and includes all adopted sender ids.
- [x] 3.2 Add or update approval-path tests covering self-only adopted provenance versus third-party adopted provenance, ensuring the full adopted sender-id set remains visible in both cases.
- [x] 3.3 Verify the current authorized message remains the only executable source for approval requests after the metadata split.

## 4. Spec and implementation verification

- [x] 4.1 Add unit/integration coverage in threaded adapter and session tests for the clarified `HasAdoptedContext` and `HasThirdPartyAdoptedContext` semantics.
- [x] 4.2 Run `openspec validate clarify-adopted-context-third-party-policy` after implementation artifacts and any follow-on spec sync are complete.
- [x] 4.3 Run the normal quality gates for the implementation PR (`dotnet build`, relevant tests, `dotnet slopwatch analyze`, `./scripts/Add-FileHeaders.ps1 -Verify`).

## 5. OpenSpec completion

- [x] 5.1 `/opsx-verify clarify-adopted-context-third-party-policy` once implementation lands.
- [x] 5.2 `/opsx-sync clarify-adopted-context-third-party-policy` to propagate the deltas into the main capability specs.
- [ ] 5.3 `/opsx-archive clarify-adopted-context-third-party-policy` after merge.
