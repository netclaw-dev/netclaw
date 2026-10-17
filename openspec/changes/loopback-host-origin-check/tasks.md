## 1. Probe and tests

- [x] 1.1 Run the probe at `dev` and record the result
- [x] 1.2 Add handler tests for the Host rule, the Origin rule, and the non-loopback and reverse-proxy controls
- [x] 1.3 Add hub upgrade and lifecycle shutdown tests with a foreign Origin

## 2. Implementation

- [x] 2.1 Add `LoopbackHostOriginPolicy` and call it from `LoopbackAuthenticationHandler`
- [x] 2.2 Log each refusal at warning level with the Host and Origin values

## 3. Gates and docs

- [x] 3.1 Add the focused mutation gate script, its tests, the CI matrix entry, and the `TOOLING.md` entry
- [x] 3.2 Update the `netclaw-operations` system skill and bump its version

## 4. Review fixes

- [x] 4.1 Reuse `DaemonExposureValidator.IsLoopbackHost` in the policy and keep the canonical-text rule, with a red test for a `127.0.0.2` bind
- [x] 4.2 Write the refusal warning in `HandleChallengeAsync` only, with a red test for an anonymous route
- [x] 4.3 Extend the focused mutation gate to the enforcing call and `IsEmptyOrPortSuffix`, and update the mutant count in `TOOLING.md`
- [x] 4.4 Link the glossary, state the call-local data, and add the check order to the design

## 5. Coupling fix

- [x] 5.1 Add `LoopbackHostName.IsStrictLoopback` below the validator and the policy, and call it from both
- [x] 5.2 Add `LoopbackHostCouplingGuardTests` to the mutation project, and add the strict name test to the focused mutation gate
- [x] 5.3 State in the policy doc, the proposal, and the spec that a `Daemon.Host` value admits after URI canonicalization
