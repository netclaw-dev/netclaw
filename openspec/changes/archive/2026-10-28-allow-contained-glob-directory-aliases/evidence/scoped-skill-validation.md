# Scoped operations-skill validation

The user canceled further model evals on 2026-10-28 and questioned the broad suite's relevance.
The repository requires behavioral validation for a skill edit. It does not specify all 113 cases or 565 trials.
Task 3.2 therefore covers the changed alias guidance through its retained targeted case.
This decision does not change the repository's general eval policy.

## Retained proof

The case is `approval_contained_alias_guidance`.
Run `53d00fe7-6008-422d-adb0-5c3a9e4ff1cf` passed all five trials.
It used seed `20261027`, threshold `0.8`, and a 600-second prompt deadline.
The retained harness source is `8033fa3c03b5368608bb0eef2b9aa00b82b9e3d1`.
The retained runtime source is `1301d07d9224e7986d39073db0ba49f7f2ab1cde`.

The independent audit confirms these facts:

- All five stored verdicts pass. Every CLI and assertion exit is zero.
- Each trial has valid session and actor evidence.
- All 75 recorded raw-file hashes match the retained files.
- Every trial loads `netclaw-operations` through `skill_load` and receives the updated clause.
- No trial calls `shell_execute`.
- Each response contains exactly one correct value for each of the five policy fields.
- The final N1 and N2 skill, prompt, strict oracle, and direct assertion helpers match the retained proof.

The skill version is `2.142.0`.
Its SHA256 is `297d92a79a4c42ca6a3795782a1a464c865afd0696b1129486ebe4e68df9f8a8`.
The strict oracle SHA256 is `56c64b9c0a78305bfd986791e5e43e391004e4b1247e2c7ff85ba9fec797642f`.
The case and prompt SHA256 is `e26397d9c5fca099d89583aef818dd65d618fe8f5fdcc16b5974418c96a35896`.

The raw receipts remain in the coordinator's delivery archive.

## Limits and separate attempts

The prompt names Personal Approval mode and supplies answer choices.
This proof covers that guidance response. It does not prove unaided behavior or runtime authority.
Deterministic authorizer tests, native checks, focused mutations, and corpus replay supply the security evidence.

The broad 600-second attempt `8ffe5fe2-ac4e-46d2-ac7a-1611874f8027` remains incomplete.
It retains 55 passes and one inconclusive trial.
The later aggregate `b5e896d2-f0f9-432d-a1c2-7b78a3dd1422` stopped at the user's request.
It retains eight passes and one incomplete ungraded slot.
Neither attempt supplies a full-suite pass. No samples are pooled across attempts.
