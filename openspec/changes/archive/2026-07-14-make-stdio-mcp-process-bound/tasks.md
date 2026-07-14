## 1. Collapse MCP Client Ownership

- [x] 1.1 Delete Playwright detection, implicit argument rewriting, scoped-client collections, cleanup, and disposal paths from `McpClientManager`.
- [x] 1.2 Route every configured MCP server invocation through the existing daemon-owned shared client and reconnect path.

## 2. Automated Proof

- [x] 2.1 Add focused tests proving different `ToolExecutionContext` session identities use one configured client/process path.
- [x] 2.2 Add focused tests proving STDIO arguments pass through unchanged, including explicit `--isolated` preservation.
- [x] 2.3 Run targeted MCP tests and the full relevant test project.

## 3. Guidance and Quality Gates

- [x] 3.1 Update `netclaw-operations` guidance to state that configured MCP servers and their state are daemon-scoped; bump the skill version.
- [x] 3.2 Confirm the eval suite is not applicable because the change does not alter production tool definitions, skill matching, prompts, or model behavior.
- [x] 3.3 Run OpenSpec validation, Slopwatch, file-header verification, and `git diff --check`.
