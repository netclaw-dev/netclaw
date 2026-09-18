## 1. Closed next-action contract

- [x] 1.1 Add the closed internal remediation code.
- [x] 1.2 Require exactly one remediation for each recoverable correction receipt and reject it for other outcomes.
- [x] 1.3 Convert project declaration, managed temporary, and file edit producers from free-form strings to the closed remediation code.
- [x] 1.4 Add constructor and producer regressions for valid, missing, undefined, and wrong-category remediations.

## 2. Shared model presentation

- [x] 2.1 Add one pure presenter for all supported remediation values.
- [x] 2.2 Route parent batch, parent streaming, and child tool-role messages through the presenter without revealing hidden tools.
- [x] 2.3 Remove duplicate next-action wording from remediation producers while preserving bounded failure detail.
- [x] 2.4 Add parent-child parity tests that assert one next action and no result-text inference.

## 3. Compatibility

- [x] 3.1 Prove approval authority, managed-temporary retry state, public APIs, and durable messages are unchanged.

## 4. Validation

- [x] 4.1 Update `IMPLEMENTATION_PLAN.md` so it records the completed behavior and the separate follow-up affordance change.
- [x] 4.2 Run focused parent, child, receipt, file-tool, and approval correction tests.
- [x] 4.3 Run the full Release build and test suite, header verification, strict OpenSpec validation, diff checks, and Slopwatch.
