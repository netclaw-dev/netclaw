# Tool Discovery


## Tool Discovery


MCP tools are not loaded by default. Use `search_tools` to discover them:

```
search_tools(query: "servers")                  # list all MCP servers
search_tools(query: "all", server: "notion")    # browse a server's tools
search_tools(query: "email")                    # keyword search
```

After discovery, matched tools become callable for the session.

### MCP server state and concurrent callers

One configured MCP server is one daemon-owned client connection. Local STDIO
servers therefore run as one process shared by every session authorized to use
that server; a Slack thread or subagent does not receive a private MCP process.
State held by the server is shared too.

Netclaw listens for tool and prompt catalog changes when the server supports
them. Modern servers use `subscriptions/listen`. Older servers can send direct
list-change notifications when they declare `listChanged` support.

Netclaw still polls each catalog. The poll repairs missed events and supports
servers without notifications. A failed notification refresh keeps the last
good catalog. Check the daemon logs for the selected compatibility mode,
acknowledgement timeouts, unsupported methods, or an ended notification stream.

Resource discovery and resource subscriptions are not part of this behavior.

For Playwright, inspect the existing tabs before acting, create a new tab for
your work, and close only tabs you created. Tabs help callers coordinate, but
they are not security boundaries: cookies, local storage, permissions, and
other browser-context state may be shared. Do not assume another authorized
session's browser activity is private from yours.

Sessions receive granted tool categories. `builtin` is always granted.
Other categories (`web`, `file`, `shell`, `scheduling`) depend on ACL
config. If a tool is missing, it may not be granted for this session.

Built-in tool grants follow the audience and are monotonic (Public ⊆ Team ⊆
Personal). **Public** sessions get read-only file tools only — `file_read`,
`file_list`, `attach_file` — and no outbound web access. **Team** adds
`file_write`, `file_edit`, `web_search`, `web_fetch`, the scheduling tools,
`skill_manage`, and `set_working_directory`. **Personal** gets everything.
`shell_execute` is Personal-only — in a Team or Public session, use `file_list`
to enumerate a directory instead of `ls`.

Tools belonging to disabled subsystems (see [Feature Kill Switches](#feature-kill-switches))
are hidden from `search_tools` results for all audiences. Public sessions
additionally cannot discover or load skills, subagents, memory tools,
scheduling tools, or the `web_search` / `web_fetch` tools regardless of
feature flags.

### Adding MCP servers (fail-closed by default)

`netclaw mcp add` writes new MCP servers with **zero granted tools** and
per-audience approval defaults so freshly added servers are never silently
exposed:

| Audience | Grants | Approval default |
|----------|--------|------------------|
| Personal | `[]` (empty list — all tools denied until the operator opts in) | `Approval` |
| Team     | `[]` | `Approval` |
| Public   | `[]` | `Deny` |

After `mcp add`, the operator uses `netclaw mcp permissions` — an
interactive TUI — to grant specific tools and adjust the per-tool or
per-server approval mode. Bare `netclaw mcp tools` is a read-only CLI view
of the same state; both commands surface a discoverability hint toward the
TUI.

Escape hatch: `netclaw mcp add --grant-all` keeps the legacy "null grants
= all tools pass" behavior for CI. Even with `--grant-all`, the per-audience
approval defaults (Personal/Team=Approval, Public=Deny) are still written —
you cannot turn off the approval prompts at `mcp add` time.

Inside the TUI (`netclaw mcp permissions`):

- `Enter` toggles the highlighted tool. In open (`All`) posture the toggle sets
  `Deny` (disabled) or clears it (inherit the server default). In `Allowlist`
  posture it adds or removes the tool from the grant list.
- `A` toggles all tools on/off for the current audience
- `E` enables/disables the whole server for the current audience
- `M` cycles the **server default** approval mode (`Auto → Approval → Deny → Auto`)
- `P` cycles the **highlighted tool's explicit override** (`inherit → Auto → Approval → Deny → inherit`) — `inherit` removes any explicit override so the tool inherits the server default
- `S` saves pending changes to `netclaw.json`
- `←/→` cycles the selected audience

Approval-mode resolution precedence (for MCP tools):

1. Exact `ToolOverrides["{server}/{tool}"]` override
2. `McpServerDefaults[{server}]` default
3. Fail-closed fallback (Personal audience, shell/file-edit matcher family)
4. Audience `DefaultMode`

A tool whose effective mode is `Deny` is **removed from the tool list the model
sees** — the model never receives a tool it cannot call.

Per-tool grants are posture-aware:

- **Open (`All`) posture** (Personal by default): the grant list is *additive*.
  A newly discovered tool is exposed and inherits the server default. Disable a
  specific tool by setting it to `Deny`. From the CLI: `netclaw mcp tools
  <server> --revoke <tool> --audience personal` writes the `Deny` override, and
  `--grant <tool>` clears it.
- **`Allowlist` posture** (Team/Public by default): the grant list is *closed*.
  A tool stays hidden until it is granted, so a newly discovered tool is not
  exposed until the operator adds it.

You do not need to re-run `permissions` after a server learns a new tool; in
open posture it appears automatically under the server default.

### Migrating existing MCP servers

Servers added to `netclaw.json` before this behavior shipped stay untouched —
their tool grants, `ApprovalPolicy.McpServerDefaults`, and `ToolOverrides`
entries are not rewritten during an upgrade.

A grant snapshot stored for an `All`-posture audience (for example a Personal
snapshot written by an older build) is now *inert*: the additive grant list no
longer restricts tools, so a newly discovered tool still appears. To disable a
specific tool, set a `Deny` override in `netclaw mcp permissions` (or
`netclaw mcp tools <server> --revoke <tool> --audience <name>`).

`netclaw doctor` will emit a warning for each enabled MCP server that
Personal can reach (`McpServersMode = All`) but has no
`ApprovalPolicy.McpServerDefaults[server]` entry and no `notion/*`-style
`ToolOverrides` entry. The warning points at `netclaw mcp permissions`.

To resolve: run `netclaw mcp permissions`, pick the server, switch to the
Personal audience, press `M` to set a server default (`Approval` is the
safe choice), `S` to save, then restart the daemon. Repeat for each
audience you want to tighten. `doctor` stops warning once the default is
set.

### When an MCP tool fails

A failed MCP tool call returns a plain, attributed error string — not a raw
JSON blob — so you can act on it directly:

- `Error: MCP tool 'server/tool' reported a failure: <detail>` — the **server**
  rejected the call (e.g. bad arguments, `old_string not found`). The detail is
  the server's own message; fix the arguments and retry, or pick a different
  tool. This is a tool-level error, not a netclaw or "tool not loaded" problem.
- `Error: MCP tool 'server/tool' failed: <detail>` — the call could not reach
  the server (transport/connection failure). Check the server's health with
  `netclaw mcp status`; a reconnect is attempted automatically once.

Both name the server explicitly (`server/tool`), so when two servers expose a
same-named tool you can tell which one failed.
