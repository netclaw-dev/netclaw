## 1. Bounded chat-title directory search

- [x] 1.1 Add the SDK-neutral title-search contract and verify it contains no Graph SDK types or raw continuation URLs.
- [x] 1.2 Implement tenant user/chat metadata batches and verify title matches, deduplication, type exclusion, and later-page results with fake Graph responses.
- [x] 1.3 Bind opaque cursors to tenant and query; verify expiry, request bounds, cancellation, partial coverage, and explicit Graph failures.

## 2. Direct Group Chat name entry

- [x] 2.1 Open the Group Chat title input directly; verify partial and pasted full titles use chat search without user search.
- [x] 2.2 Support continued search and safe selection; verify empty batches, stale-result rejection, canonical-ID persistence, and unchanged principal grants.
- [ ] 2.3 Update the native Teams tape; verify the name field and advanced-ID path through the real terminal input route.

## 3. Guidance and validation

- [x] 3.1 Update the operator guide and versioned operations skill; verify they explain title search, optional consent, continuation, and partial coverage.
- [x] 3.2 Validate this corrective OpenSpec change with strict validation; verify it explicitly supersedes the earlier participant-first decision.
- [ ] 3.3 Run focused Teams tests, native smoke, formatting, Slopwatch, header checks, and behavioral evals; record results and any unavailable prerequisites.

## Validation status

- The full CLI suite passes: 1,534 passed, zero failed, and two existing root/POSIX file-lock tests skipped.
- This run includes all 163 focused Teams tests and the final narrow-viewport regression.
- Strict OpenSpec validation passes. The native tape syntax check passes.
- Slopwatch reports zero issues. The PowerShell header check reports that all files have headers.
- Both native smoke helper projects publish successfully. The mock LLM passes its loopback health check.
- Native tape execution remains pending. Chromium cannot create its required Unix socket in this environment, so no tape action executes.
- The project formatter cannot start its named-pipe service because this environment denies socket creation.
- Whitespace verification passes for the new C# files.
- Behavioral evals remain pending because Docker is unavailable.
