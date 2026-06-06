## Design: Bare Integer Token Stripping in Approval Matcher

### Problem

The approval matcher's `ReconstructClauseText()` builds display text and retry-exact keys from AST clause structure. It iterates over `clause.Verb.Joined` (correctly excludes integers via `IsVerbLikeToken`) then over `clause.Args` — but includes ALL args, including bare integers. This produces patterns like `freshdesk ticket get 123` instead of `freshdesk ticket get`.

The matching path (`ExtractCandidatesViaBashParser` → `clause.Verb.Joined`) already works correctly — the gap is only in display/retry-key extraction.

### Approach

**Single point of change:** `ReconstructClauseText()` in `IToolApprovalMatcher.cs`.

Add an `IsBareIntegerToken()` predicate that returns true for strings consisting entirely of ASCII digits (length >= 1, no leading `-`). When `ReconstructClauseText` encounters a bare integer arg, it `break`s the loop — dropping all subsequent args. This matches the spec's "termination condition" semantics.

**Why `break` not `continue`:** The spec says integers are a termination condition, not just an exclusion. For `timeout 30 curl http://example.com`, everything after `30` (including the wrapped subcommand `curl`) is outside the approval intent.

**Windows path:** Intentionally NOT fixed on Windows. The legacy `TraverseApprovalUnits` path uses `ShellTokenizer` which lacks the AST's `IsVerbLikeToken`. PwshParser integration for Windows is a separate change.

### Risks

1. **Unicode digits:** `char.IsDigit` accepts non-ASCII digits (e.g., Arabic numerals). A fix to use `token[0] >= '0' && token[0] <= '9'` would be more precise but is low-risk to defer — CLI tokens are almost exclusively ASCII digits.
2. **Breadth of `break`:** Dropping all args after the integer could make patterns unusually broad (e.g., `timeout` instead of `timeout 30`). However, `timeout` is already a very generic verb that would need approval anyway.
3. **Redirect targets:** Redirects are in a separate loop after Args, so they are always preserved regardless of where the break fires. Correct behavior.

### Alternative Approaches Considered

1. **Fix `IsVerbLikeToken` to exclude integers:** Already done. This correctly excludes integers from `VerbChain`. The gap is in display/retry-key extraction, not matching.
2. **Add `--no-integers` flag to `ReconstructClauseText`:** Over-engineered. The predicate is only called in one place.
3. **Use `int.TryParse`:** Simpler but slightly slower per-token. The manual digit check is fine for CLI-length tokens.
