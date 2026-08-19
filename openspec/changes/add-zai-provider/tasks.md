## 1. Provider Contract

- [x] 1.1 Add the Z.ai descriptor, API-key probe, and known model metadata
- [x] 1.2 Add the Z.ai plugin and register it in all provider catalogs
- [x] 1.3 Default to the GLM Coding Plan endpoint; document the platform override
- [x] 1.4 Treat trailing `v<digits>` base segments as already versioned in endpoint resolution

## 2. Wire Behavior

- [x] 2.1 Add the Zai wire profile to the shared chat client
- [x] 2.2 Map MEAI reasoning options and Netclaw reasoning suppression to Z.ai fields
- [x] 2.3 Preserve Z.ai reasoning content during assistant tool-call replay

## 3. Operator Surfaces

- [x] 3.1 Add Z.ai to CLI and TUI provider coverage
- [x] 3.2 Update the model-provider specification and operations skill guidance

## 4. Automated Proof

- [x] 4.1 Add fake-HTTP tests for authentication, discovery, metadata, and errors
- [x] 4.2 Add payload tests for reasoning, tool-loop replay, and generic-profile isolation
- [ ] 4.3 Run focused tests, evals, the TUI smoke path, Slopwatch, and the header check
