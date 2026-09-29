# Test evidence fixtures

This folder is the stable home of the JSON evidence that the approval and
tool-friction tests read. Two test projects use it:

- `Netclaw.Security.Tests` includes the files in `Netclaw.Security.Tests.csproj`.
- `Netclaw.Actors.Tests` links the files in `Netclaw.Actors.Tests.csproj`.
  This project already links evidence model files from `Netclaw.Security.Tests`.

The build copies each file to the same output path as before the move. Test
code reads the files from the output directory, so the test code did not change:

| Output path | Source in this folder |
| --- | --- |
| `ApprovalEvidence/*.json` | `ApprovalEvidence/*.json` |
| `ToolFrictionEvidence/tool-friction-fixtures.json` | `ToolFrictionEvidence/tool-friction-fixtures.json` |
| `ToolFootprintEvidence/tool-schema-footprint-baseline.json` | `ToolFootprintEvidence/tool-schema-footprint-baseline.json` |

## Files and readers

| File | Content | Tests that read it |
| --- | --- | --- |
| `ApprovalEvidence/approval-matrix.json` | Sanitized approval cases observed in Netclaw 0.26.0-beta.3. | `ShellApprovalEvidenceContractTests` |
| `ApprovalEvidence/netclaw-policy-fixtures.json` | Shell policy fixtures: the command, its facts, the expected trace, and the expected final result for each case. | `ShellApprovalEvidenceContractTests`, `ShellPolicyEvidenceFixtureTests` |
| `ApprovalEvidence/fresh-session-policy-fixtures.json` | Shell policy fixtures for fresh sessions. | `ShellApprovalEvidenceContractTests`, `ShellPolicyEvidenceFixtureTests` |
| `ApprovalEvidence/post-1890-approval-harvest.json` | Sanitized live approval harvest after #1890. | `ShellApprovalEvidenceContractTests` |
| `ApprovalEvidence/post-1925-binary-swap-approval-harvest.json` | Sanitized live approval harvest after the #1925 binary swap. | `ShellApprovalEvidenceContractTests` |
| `ApprovalEvidence/post-1925-extended-approval-harvest.json` | Extended live approval harvest after #1925. | `ShellApprovalEvidenceContractTests` |
| `ApprovalEvidence/post-1952-live-approval-harvest.json` | Sanitized live approval harvest after #1952. | `ShellApprovalEvidenceContractTests` |
| `ApprovalEvidence/post-265d606f-fresh-session-approval-harvest.json` | Fresh-session approval harvest at `265d606f`. | `ShellApprovalEvidenceContractTests` |
| `ApprovalEvidence/post-334cb4c-live-batching-harvest.json` | Live batch harvest at `334cb4c`. | `ShellApprovalEvidenceContractTests` |
| `ApprovalEvidence/pre-guidance-fresh-session-eval-baseline.json` | Fresh-session eval baseline before the guidance change. | `ShellApprovalEvidenceContractTests` |
| `ApprovalEvidence/post-guidance-fresh-session-eval-results.json` | Fresh-session eval results after the guidance change. | `ShellApprovalEvidenceContractTests` |
| `ApprovalEvidence/post-9d02d19-binary-swap-eval-results.json` | Eval results after the `9d02d19` binary swap. | `ShellApprovalEvidenceContractTests` |
| `ApprovalEvidence/post-7efa7fd-followup-live-eval-results.json` | Follow-up live eval results at `7efa7fd`. | `ShellApprovalEvidenceContractTests` |
| `ApprovalEvidence/post-terminal-denial-guidance-eval-results.json` | Eval results after the terminal-denial guidance. | `ShellApprovalEvidenceContractTests` |
| `ApprovalEvidence/post-independent-operation-guidance-eval-results.json` | Eval results after the independent-operation guidance. | `ShellApprovalEvidenceContractTests` |
| `ToolFrictionEvidence/tool-friction-fixtures.json` | Sanitized tool-friction cases. | `ToolFrictionEvidenceContractTests`, `ToolFrictionReplayTests` |
| `ToolFootprintEvidence/tool-schema-footprint-baseline.json` | Tool schema size baseline for main and sub-agent sessions. | `SubAgentSpawnIntegrationTests` |

The tests hash the exact bytes of some files. The root `.gitattributes` keeps
every `*.json` file in this folder as LF on every platform. Do not remove that
rule.

`ShellApprovalEvidenceContractTests.Approval_evidence_contains_no_source_identity`
reads every `*.json` file in the `ApprovalEvidence` output folder. Put only
sanitized evidence in `ApprovalEvidence/`.

## History

These files are byte-identical copies of evidence from these OpenSpec change
folders:

- `openspec/changes/archive/2026-08-15-structure-shell-approval-policy/evidence/`
- `openspec/changes/reduce-fresh-session-approval-spam/evidence/`
- `openspec/changes/make-agent-tools-pit-of-success/evidence/`

The OpenSpec copies are historical records. The tests do not read them. Change
the files in this folder, not the OpenSpec copies. Tests must not load files
from `openspec/`, because an archive step moves or deletes change folders.
