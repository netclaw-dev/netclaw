## 1. Teams TUI state and principal management

- [ ] 1.1 Consolidate the Teams menu into destination and principal add/manage flows, then verify native keyboard navigation and saved-row rendering.
- [ ] 1.2 Add scoped configured-principal lists, filters, confirmations, and exact removal, then verify global and channel rules remain independent.
- [ ] 1.3 Add canonical manual principal validation before draft mutation, then verify a mixed batch leaves persisted configuration unchanged.
- [ ] 1.4 Make advanced entry focusable and make each search request loop-owned and generation-safe, then verify typed and pasted `m`, Unicode, navigation, and cancellation cases.
- [ ] 1.5 Resolve exact Team/channel pairs from structured mappings, then verify a multi-Team channel edit changes only its exact override.

## 2. Bounded Group Chat directory capability

- [ ] 2.1 Add SDK-neutral Group Chat metadata and page contracts, then verify contracts expose no Graph SDK types.
- [ ] 2.2 Implement selected-user Graph chat pages, local filtering, bounded cache, and opaque continuation validation, then verify pagination and invalid continuation tests.
- [ ] 2.3 Add Group Chat picker, review, manual entry, and display-only label enrichment, then verify participant selection does not change principal configuration.
- [ ] 2.4 Keep optional chat metadata consent distinct in directory status and doctor output, then verify missing consent preserves local chat management.

## 3. Destination management and runtime safety

- [ ] 3.1 Add configured destination rows, details, and exact removal, then verify unresolved channels and Group Chats remain visible and removable.
- [ ] 3.2 Preserve Group Chat ingress as an explicit separate switch, then verify a saved ID alone remains denied after configuration activation.
- [ ] 3.3 Add configuration-to-runtime authorization tests for add and removal, then verify canonical IDs reach the real Teams ACL consumer.
- [ ] 3.4 Add the approval authorization regression, then verify global group and exact-channel user admission match callback authorization.

## 4. Documentation and full verification

- [ ] 4.1 Update the Teams operator guide, doctor guidance, and system operations skill with optional `Chat.ReadBasic.All` consent and owner tests.
- [ ] 4.2 Update `config-teams` native smoke and semantic assertions, then run its normal and narrow-terminal cases.
- [ ] 4.3 Run focused tests, solution build and tests, Slopwatch, file-header check, vulnerability scan, smoke suite, strict OpenSpec validation, and whitespace checks.
- [ ] 4.4 Review the final diff and owner test guide, then create a draft fork PR and monitor all applicable CI checks.
