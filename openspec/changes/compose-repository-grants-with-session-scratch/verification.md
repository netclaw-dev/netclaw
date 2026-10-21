# Verification report

## Scope

The implementation follows the three approved policy changes.
The original production source commit is `837b1ab91`.
The primary checkout retains its existing untracked files.
PR #2563 contains the implementation and the original verification evidence.
The post-review section below describes the consent correction.
This change includes no deployment.

## Requirement evidence

| Requirement | Implementation | Evidence |
| --- | --- | --- |
| TA-8 | `WithManagedTemporaryRedirects` composes command scopes with independent file authority. | `ManagedScratchApprovalTests` covers owned scratch, external operands, other runs, links, and file consent. |
| TA-8 | The matcher retains the occurrence directory and ordinary operand scopes. | `NoProgramMutationTests` covers command authority and parent, child, and peer isolation. |
| TA-9 | `TemporaryPathCorrectionPolicy` advises a new call for proved temporary project outputs. | Advice and retry tests preserve the project directory and refuse input redirects. |
| TA-10 | `ShellPolicyCoordinator` explains reusable grants and validated near misses. | Consent tests preserve candidate words, quoted program identity, and the full command at the display limit. |

The specification deltas also appear in the main tool-authorization specification.
The architecture document names the decision owners and their call-local data.
The system skill version changes from `2.119.0` to `2.120.0`.

## Automated checks

| Check | Result |
| --- | --- |
| Full `Netclaw.Security.Tests` project | 1,681 passed; one skipped. |
| Focused scratch, repository, advice, launch, and redirect classes | 123 passed; three skipped. |
| Strengthened scratch class, including inline option paths and assignment qualifiers | Nine passed. |
| Final consent, command-length, quoted-program, and hard-deny classes | 291 passed; four skipped. |
| Full disposition matrix | 778 passed; one failure that also occurs on the unmodified baseline. |
| Final mutation-support class | 25 passed. |
| Focused Stryker gate | All five plain-target mutants and all 18 redirect-check mutants were killed. |
| Eval-harness unit tests | 362 passed. |
| Native approval tape with the final binary | Passed; process exit status zero. |
| Slopwatch | Zero issues. |
| Copyright headers | Passed. |
| OpenSpec strict validation | Passed. |
| Whitespace and Bash syntax checks | Passed. |

These test selections overlap. Their counts do not form one combined test total.
The mutation extension adds three mutants to the existing invocation and adds no Stryker build.

The disposition failure is `glob-in-directory-segment-uses-global-grant`.
Both source revisions return `RequiresApproval`; the existing case expects `Allowed`.
The isolated baseline check uses commit `2ce7d6660412bb3e821c95e9cfec203bdcfdb751`.
This report records the baseline failure and does not treat the matrix as a passed gate.

## Focused model evidence

Spark2 served `Qwen3.8-Flash-Next` through its OpenAI-compatible endpoint.
The focused `approval_scratch_scope_guidance` case passed all five trials.
The case requires the loaded skill, the correct grant reach, managed scratch, and the preserved project directory.
The case also refuses shell execution.
Run ID: `d5fba3c7-8f03-411e-86dd-7b157b1c6fce`.

The operator stopped the broader suite.
The broader run does not establish full-suite acceptance.
Its directory-argument case passed three of five trials.
An original-skill control passed two of five trials with the same binary, model, prompt seed, and deadline.
Control run ID: `1b559463-475d-4bd9-ab56-9917c9296d73`.
The failed calls used inline `cd` before any changed skill text reached the model.
The unchanged tool description limits typed directory advice to named child directories, while this case asks for `/tmp`.
This existing guidance ambiguity needs a separate review.

The soft-scheduling case remained inconclusive after two 180-second timeouts.
Its three completed trials passed.
The report retains that result.
The operator did not authorize a restart of the full model suite.

## Data boundaries

The model evals use repository templates, synthetic fixtures, and the name `Eval User`.
They do not import the operator's production identity, memory, or incident transcript.
The new approval explanations use fixed text and do not echo stored grant payloads.
The local archive backtest uses no LLM and executes no archived shell command.
Raw archive inputs and result files remain in a private local directory.

## Archive differential

The Akka.Streams archive differential is complete.
It compares baseline `2ce7d6660412bb3e821c95e9cfec203bdcfdb751` with source commit `837b1ab91`.
Both revisions use the same 55,144 ordered inputs and seven isolated lanes.
Archive SHA-256: `d2041144c1e7bb7e51c2b1e3c8adc21dac01c7726fee903082798491aba661ad`.
The probe uses all 21 shell states and all 24 tool states.
The fixture contexts do not reconstruct the original sessions' filesystem state or grant history.
The archive has no repository-grant state. The focused repository tests supply that proof.

Each revision has 1,160,808 primary comparisons.
The baseline has 1,638,030 records with retries. The candidate has 1,638,026.
The full run takes 72 minutes and 19 seconds on this host.

| Difference | Records | Review |
| --- | --- | --- |
| Identical complete records | 1,284,054 | Every emitted field matches after path normalization. |
| Approval text only | 353,959 | Removal of the exact new notes restores the full baseline record. All other fields match. |
| Consent request to temporary advice | 4 | The call stops and requires a new call. |
| Automatic allow to temporary advice | 6 | The current rule puts corrections before an automatic allow. |
| Unattended-consent denial to temporary advice | 3 | The old reason is `approval_required_unattended`. No hard denial changes. |
| Removed Once retries | 4 | Each belongs to a consent request that becomes temporary advice. No primary case disappears. |

The 13 primary outcome changes come from one archived input across different states.
No decision becomes `Allowed`, and no hard denial weakens.
The review accounts for every changed record.

### Supplementary target proof

The original probe uses record text for the temporary target.
That text omits its internal path fields.
The probe now emits both the managed directory and the platform root explicitly.
It also rejects any temporary correction whose target differs from the current run's managed directory.

A supplementary run uses only the affected input and retains all 45 states.
Both revisions pass the ownership assertion.
The run has 2,905 baseline records and 2,901 candidate records.
Its 17 differences match the full archive's 13 primary changes and four removed Once retries.
The supplementary run takes two minutes and 30 seconds.

The archive comparison uses the production implementation at `837b1ab91`.
The final validation changes strengthen the test and the probe, and clarify the documented eligibility conditions.
The archive review finds no unexpected authority change.

### Local evidence

Aggregate evidence is in `artifacts/authorization-corpus-scratch-archive/semantic-summary.json` and `authority-review-summary.json`.
Raw corpus data and decision records remain private and stay outside Git.
The target-specific evidence is in `artifacts/authorization-corpus-scratch-targets/`.

## Post-review consent correction

The adversarial self-review of `76c06f09d` found a Once-answer collision.
File consent replaced the command identity with the file-operation identity.
A Once answer for `uv export` also permitted `uv sync` with the same scratch target.
The original probe uses the production authorization path and executes no shell command.

`WithFileToolConsent` now retains an exact candidate for each file operation.
A composed redirect also retains an exact command candidate.
This removes a second collision between one filename with `, write ` and two separate output targets.
The existing Once-key code binds both candidates, their scopes, and their assignment digests.
The correction adds no candidate property, grant format, configuration value, or decision owner.
The architecture document describes the exact-consent representation.
The approved TA-8 consent contract requires this correction and needs no new policy decision.

The new regression fails twice before the correction, under shell Approval and shell Auto.
After the correction, a changed command, target, or assignment still needs approval.
The unchanged retry succeeds with `OneTimeApproval`.
The original adversarial probe also confirms those four results after the correction.

### Platform fixture corrections

Hosted run `39513493570` passes Linux tests and all ten mutation jobs.
Its Windows and macOS test jobs fail in the new fixtures.
The Windows redirect test uses Bash with native Windows paths.
The macOS fixtures pass temporary aliases to checks that require physical paths.

The redirect test now selects the native shell through the existing host selector.
Its inside-repository and external-file checks still run on Windows.
The POSIX null-device assertion remains specific to POSIX hosts.
The scratch fixtures resolve their temporary roots before they construct repository and session paths.
The production filesystem policy retains its link restrictions.

Both fixture sets pass through a linked temporary root on Linux.
The scratch class passes all 13 cases. The mutation-support class passes all 31 cases.
These host simulations do not establish native Windows or macOS acceptance.
The next hosted run must supply that evidence.

The full model eval suite remains stopped.
The archive comparison remains prior evidence for `837b1ab91`; this correction does not repeat it.
Independent review remains separate from the author agent's self-review.

### Post-review local checks

| Check | Result |
| --- | --- |
| Scratch, repository, no-program consent, approval boundary, literal-twin, and causal-directory classes | 148 passed; four skipped. |
| Mutation-support class | 31 passed. |
| Focused Stryker gate | All five plain-target and all 19 redirect/consent mutants were killed. |
| Linked temporary root: scratch class | 13 passed. |
| Linked temporary root: mutation-support class | 31 passed. |
| Original command and file-list probes | Changed calls require approval; unchanged retries are allowed. |
| Native approval smoke with the updated binary | Passed; process exit status zero. |
| Slopwatch | Zero issues. |
| Copyright headers | Passed. |
| OpenSpec strict validation, whitespace, and Bash syntax | Passed. |

The mutation fixture includes the final prompt conversion before it creates the Once keys.
The new mutation target negates the condition that preserves the command candidate.
The gate requires that mutant to run and die, alongside the existing redirect mutants.
The final run meets that requirement and adds no Stryker build.
A narrow initial span missed the target. A later run exposed the incomplete prompt fixture.
The final span and fixture correct both gaps without a threshold change.
These checks do not repeat the full model suite or the archive comparison.
