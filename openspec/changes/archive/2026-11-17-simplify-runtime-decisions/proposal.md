## Why

Netclaw needs fewer decision models, stronger type guarantees, and less dependence on expensive actor fixtures.
Upgrade to .NET 11, then consolidate selected decisions without changing the actor runtime or durable contracts.

## What Changes

- Upgrade the supported build and execution baseline to .NET 11 and C# 15.
- Use closed internal authorization outcomes while preserving released public contracts.
- Extract pure delivery retry and gateway reconnect decisions.
- Move policy permutations into direct tests while retaining real boundary proof.
- Deliver the changes through the installed `github/gh-stack` extension.
- Keep one canonical execution plan at `.systematize/plans/net11-typed-decisions/plan.md`.

## Capabilities

### New Capabilities

None. This change adds no product capability.

### Modified Capabilities

None. Existing authorization, retry, recovery, and transport requirements stay in force.
The change metadata sets `skip_specs: true` because this work changes implementation structure and the runtime baseline.
If discovery requires a behavior change, revise this proposal through OpenSpec before implementation.

## Impact

Source PRDs are PRD-001 FR-002, FR-003, FR-005, FR-008, and FR-011.
PRD-002 SEC-001, SEC-003, SEC-006, SEC-007, and SEC-009 define preserved security guarantees.
Use the [engineering glossary](../../../../docs/spec/GLOSSARY.md) for shared terms.

In scope are the SDK, target frameworks, release checks, internal decisions, affected tests, and current architecture documents.
Out of scope are Akka.Durable adoption, a generic decider framework, new journal records, approval-store migration, reminder redesign, and passivation redesign.

### Security impact

Keep one authorization owner, exact consent scope, launch reauthorization, and default deny.
Preserve public exception contracts until an explicit compatibility decision permits a change.

### Operational impact

Verify self-contained binaries, native dependencies, the Ubuntu image, the Aspire sample, and old journal replay on .NET 11.
Preserve existing configuration, journal, snapshot, and wire formats.
The .NET 11 x64 binaries require `x86-64-v2` hardware. The Linux Arm64 hardware minimum stays unchanged.
Do not publish a release, merge a PR, or deploy as part of this plan.
