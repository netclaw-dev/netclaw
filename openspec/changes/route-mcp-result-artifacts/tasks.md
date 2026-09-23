## 1. Result Projection

- [x] 1.1 Add one immutable MCP result projection and verify formatter tests cover text, artifacts, errors, and invalid Base64.
- [x] 1.2 Keep the existing text wrappers compatible and verify current exact-text formatter tests pass.

## 2. Artifact Admission

- [x] 2.1 Add the daemon artifact materializer and verify scan-before-write, verified MIME, count, byte, path, and cancellation cases.
- [x] 2.2 Reuse the existing modality decision and tool outputs; verify image-capable and text-only contexts produce the required outputs.

## 3. MCP Integration

- [x] 3.1 Wire the materializer into `McpClientManager` and dependency injection; verify constructor and lifecycle tests compile and pass.
- [x] 3.2 Return a valid PNG from the deterministic MCP server and verify both SDK result shapes preserve bytes without Base64 text.
- [x] 3.3 Add a real STDIO rejection case and verify invalid image bytes produce no file or model output.

## 4. Product Guidance

- [x] 4.1 Add the artifact contract to `PRD-006` and `SPEC-009`; verify the requirements name security, output, and modality behavior.
- [x] 4.2 Update `netclaw-operations`, increase its version, and verify the skill explains MCP artifact delivery and rejection.

## 5. Verification

- [x] 5.1 Run focused formatter, materializer, STDIO, and session pipeline tests; verify all selected tests pass.
- [ ] 5.2 Run `openspec validate route-mcp-result-artifacts --strict`, the eval suite, Slopwatch, header checks, and `git diff --check`.
- [x] 5.3 Run the full solution tests and review focused mutation scope; verify no new security target is necessary or add one narrow target.
