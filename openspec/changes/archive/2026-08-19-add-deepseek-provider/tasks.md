## 1. Provider Contract

- [x] 1.1 Add the DeepSeek descriptor, API-key probe, and known model metadata
- [x] 1.2 Add the DeepSeek plugin and register it in all provider catalogs

## 2. Wire Behavior

- [x] 2.1 Add required generic and DeepSeek wire profiles to the shared chat client
- [x] 2.2 Map MEAI reasoning options and Netclaw reasoning suppression to DeepSeek fields
- [x] 2.3 Preserve DeepSeek reasoning content during assistant tool-call replay

## 3. Operator Surfaces

- [x] 3.1 Add DeepSeek to CLI and TUI provider coverage
- [x] 3.2 Update the model-provider specification and operations skill guidance

## 4. Automated Proof

- [x] 4.1 Add fake-HTTP tests for authentication, discovery, metadata, and errors
- [x] 4.2 Add payload tests for reasoning, tool-loop replay, and generic-profile isolation
- [ ] 4.3 Run focused tests, evals, the TUI smoke path, Slopwatch, and the header check
