## 1. Named Model Configuration

- [ ] 1.1 Add `Models.Proxies.Image` and update the configuration schema.
- [ ] 1.2 Retain named definitions and assignments in model configuration resolution.
- [ ] 1.3 Add fail-closed tests for unknown proxy references and legacy compatibility.

## 2. Runtime Registry

- [ ] 2.1 Add a named runtime registry that caches composed clients and effective capabilities.
- [ ] 2.2 Adapt role-based client resolution to use named registry entries.
- [ ] 2.3 Validate image input and text output for the configured proxy at startup.
- [ ] 2.4 Add registry and daemon runtime contract tests.

## 3. Durable Image Analysis

- [ ] 3.1 Add a fixed versioned image prompt and an image proxy analyzer with no tools or session history.
- [ ] 3.2 Add a serialization-safe proxy-result event and snapshot state.
- [ ] 3.3 Add actor continuations that persist one result before each main model call.
- [ ] 3.4 Add recovery and lazy historical analysis tests.
- [ ] 3.5 Add proxy failure, empty result, and zero-main-call tests.

## 4. Main Model Assembly

- [ ] 4.1 Select original image content for an image-capable main model.
- [ ] 4.2 Select durable untrusted text for a text-only main model.
- [ ] 4.3 Neutralize proxy wrapper delimiters and include the session-relative path.
- [ ] 4.4 Add assembly tests for both model capability paths.

## 5. Attachment Routes

- [ ] 5.1 Retain policy-approved images when the main model or image proxy can consume them.
- [ ] 5.2 Mark proxy attachment lines with `inlined="true" via="image-proxy"`.
- [ ] 5.3 Add Slack, Discord, and Mattermost route tests.

## 6. CLI and TUI

- [ ] 6.1 Add CLI set, clear, and list support for `image-proxy`.
- [ ] 6.2 Reuse named definitions and preserve model metadata across writes.
- [ ] 6.3 Add image proxy controls to the interactive model manager.
- [ ] 6.4 Add negative save tests and legacy migration round-trip tests.
- [ ] 6.5 Add a native smoke tape and a semantic assertion for the TUI path.

## 7. Documentation and Gates

- [ ] 7.1 Update model documentation and the `netclaw-operations` system skill.
- [ ] 7.2 Update behavioral eval cases for the model and provider change.
- [ ] 7.3 Run tests, the eval suite, native smoke, repository quality gates, and OpenSpec validation.
- [ ] 7.4 Update this checklist with final verification evidence.
