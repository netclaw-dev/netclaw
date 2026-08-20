## Context

The existing policy fixture catalog executes D acceptance cases, A adversarial
cases, and eleven earlier live regressions through the real shell policy
coordinator. The post-1952 harvest adds 21 representative prompts, but its
`commandShape` values are display-safe evidence. Several contain angle-bracket
placeholders that would change Bash grammar if executed. Copying those strings
into the coordinator fixture would therefore test the redaction syntax rather
than the observed approval shape.

This change affects only source-controlled evidence and tests. It does not
change an actor boundary, policy stage, grant store, session history, public
API, or persisted representation. The coordinator fixture continues to use an
in-process approval actor stub and the canonical bundled safe catalog.

## Goals / Non-Goals

**Goals:**

- represent every T01-T21 harvested case with an identity-free command that
  preserves the policy-relevant shell structure;
- execute each command through the real coordinator with exact expected
  outcome, approval shape, and actor-contact count;
- bind each regression to its source file, source evidence ID, classification,
  and target outcome;
- make accidental command or expectation drift visible through a locked digest;
  and
- preserve current strict behavior for expected approvals, agent-alignment
  cases, and unresolved ShellSyntaxTree facts.

**Non-Goals:**

- changing production policy or reviewed-safe catalogs;
- parsing `gh` operations, Docker behavior, or Bash arithmetic in Netclaw;
- granting authority from an evidence classification;
- claiming the 21 cases exhaust the source traffic window; or
- changing actor messages, persistence, recovery, or runtime failure handling.

## Decisions

### Curate executable commands instead of executing display redactions

Each new live regression will retain the command's control flow, executable
chain, path boundaries, redirects, and dynamic constructs while replacing
identities with ordinary quoted literals. Angle-bracket placeholders will not
appear in executable commands because Bash treats them as redirects.

The alternative was to execute `commandShape` directly. That would make cases
such as `<known-file>` and `<old-range>` semantically false and could turn a
read into a redirect or parse failure.

### Bind every live row to an explicit evidence file

`PolicyLiveRegressionCase` will add a required internal-only
`SourceEvidenceFile` field. Contract validation will resolve the pair
`(SourceEvidenceFile, SourceEvidenceId)` and compare the source classification
with the fixture classification. This avoids relying on globally unique S/T
identifiers and makes future harvest additions unambiguous.

The field is test-only JSON. It does not affect a public or durable runtime
contract.

### Lock the executable live-regression section as one evidence artifact

The contract test will compute a deterministic digest over the serialized
`liveRegressionCases` section. The digest will also include each linked source
`commandShape` and classification. A command, source shape, evidence link,
classification, outcome, correction, option, or actor count change therefore
requires an explicit evidence review. Semantic tests will still execute every
row through the coordinator. The digest is a drift alarm, not a substitute for
behavior.

The alternative was a large hard-coded command dictionary in C#.
That would duplicate the corpus and make review harder.

### Preserve classifications without translating them into authority

All new T rows currently target `RequiresApproval`. `ExpectedApproval` remains
a product-appropriate prompt. `AgentAlignmentDebt` remains promptable because
guidance, file tools, or session scratch are the preferred remedy.
`ShellSyntaxTreeFactGap` remains promptable until a general parser-owned fact
exists. No classification can directly cover a policy candidate.

### Reuse the existing coordinator harness

The new rows use `PolicyAdversarialCase` and
`ShellPolicyEvidenceFixtureTests.Live_regression_fixtures_pin_current_policy_outcomes`.
This preserves the real syntax analysis, path policy, safe-catalog,
coordinator, approval-context, and actor-check route without adding a second
evaluator or fixture-specific production seam.

## Risks / Trade-offs

- **Curated commands can diverge from live intent** -> Preserve the
  policy-relevant structure, link every row to the source evidence, and review
  the paired source and curated command together.
- **A locked digest can be mechanically refreshed** -> Require the semantic
  coordinator assertions and exact source-classification linkage to pass too.
- **Platform-dependent paths can make fixtures flaky** -> Use the declared
  Bash/Linux environment with canonical POSIX fixture roots and identity-free
  external paths.
- **A future parser release can legitimately change outcomes** -> Treat the
  resulting fixture failure as an explicit corpus review, then update the
  expectation and digest together if the new behavior is intended.

## Migration Plan

1. Extend the test-only fixture schema and existing L rows with explicit source
   evidence files.
2. Add L12-L32 for T01-T21 and record current coordinator results.
3. Add linkage, digest, uniqueness, classification, and mutation coverage.
4. Run focused coordinator and evidence suites plus the repository quality
   gates.

Rollback removes the new test-only rows and schema field. No runtime data or
authority requires migration.

## Open Questions

None. Production changes for any future general parser fact require a separate
ShellSyntaxTree and Netclaw change.
