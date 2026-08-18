# NetClaw Release Notes

## 0.26.0-beta.5 (2026-08-18)

### Features
- **MCP tools inherit the server approval default** — A tool that a server adds after per-tool rules were configured now inherits the server's default posture instead of being hidden; disabled tools are hidden from the model entirely, and `netclaw mcp tools` grant/revoke toggles map to Approval/Deny and work correctly against Deny defaults ([#1978](https://github.com/netclaw-dev/netclaw/pull/1978))
- **Fewer shell approval prompts in fresh sessions** — Avoidable approval retries and prompts are reduced, agents stop retrying a scope after an access denial, and project scope declaration misses are cut ([#1982](https://github.com/netclaw-dev/netclaw/pull/1982), [#1985](https://github.com/netclaw-dev/netclaw/pull/1985), [#1983](https://github.com/netclaw-dev/netclaw/pull/1983), [#1990](https://github.com/netclaw-dev/netclaw/pull/1990))
- **One approval for causal Bash diagnostic chains** — Causal diagnostic command chains now collapse to a single approval decision instead of prompting per step ([#1925](https://github.com/netclaw-dev/netclaw/pull/1925))
- **Agents use file tools and stable project scope** — Agents are steered to file tools for known file work, subagents are guided to private session scratch, and subagent project scope corrections are fixed ([#1952](https://github.com/netclaw-dev/netclaw/pull/1952), [#1956](https://github.com/netclaw-dev/netclaw/pull/1956), [#1921](https://github.com/netclaw-dev/netclaw/pull/1921), [#1920](https://github.com/netclaw-dev/netclaw/pull/1920))

### Bug Fixes
- **MCP tool output is no longer corrupted by redaction** — Legitimate payloads that look credential-like, such as presigned upload URLs, pass through unmodified; redaction stays on for shell, file, web, and background-job output ([#1992](https://github.com/netclaw-dev/netclaw/pull/1992))
- **`shell_execute` no longer hangs on background children** — Commands that daemonize return promptly, buffered output flushes in a short grace window, and a note reports when output capture was cut ([#1887](https://github.com/netclaw-dev/netclaw/pull/1887))
- **Mattermost initial connection race fixed** — Startup now waits for the WebSocket `OnConnected` event before reporting ready ([#1986](https://github.com/netclaw-dev/netclaw/pull/1986))
- **MCP OAuth cold-start refresh works again** — The SDK-resolved client identity is persisted so token refresh still matches after a restart instead of falling back to interactive auth ([#1970](https://github.com/netclaw-dev/netclaw/pull/1970))
- **MCP secrets redacted from OAuth failure logs** — Token and client-registration error bodies can no longer leak the client secret into daemon logs ([#1976](https://github.com/netclaw-dev/netclaw/pull/1976))
- **MCP non-OAuth 401/403 no longer reported as awaiting auth** — Plain transport rejections surface as their real HTTP status instead of telling operators to run `netclaw mcp auth` ([#1908](https://github.com/netclaw-dev/netclaw/pull/1908))
- **MCP HTTP status detection hardened** — `netclaw mcp list` and `netclaw doctor` no longer misreport stdio servers whose command path contains "401" or "403" as HTTP auth failures ([#1913](https://github.com/netclaw-dev/netclaw/pull/1913))
- **Duplicate reminder acks are idempotent** — Re-sent acknowledgement messages no longer disturb reminder state ([#1955](https://github.com/netclaw-dev/netclaw/pull/1955))
- **Self-update no longer crashes on Windows after success** — Deleting the running binary's backup is skipped, and a failed binary swap rolls back so installs never brick ([#1924](https://github.com/netclaw-dev/netclaw/pull/1924))
- **PowerShell host probe timeout raised to 15 seconds** — Slow `pwsh` cold starts are less likely to fail the probe ([#1949](https://github.com/netclaw-dev/netclaw/pull/1949))
- **Headless reviewed-safe shell authority fixed** — Reviewed-safe commands in headless sessions now resolve authority correctly ([#1918](https://github.com/netclaw-dev/netclaw/pull/1918))

### Internal Improvements
- **Typed shell approval policy** — ShellSyntaxTree 0.3.3 path facts, a typed policy coordinator with bounded decision trace, extracted and ordered policy stages, and enforced reviewed-safe redirect boundaries ([#1916](https://github.com/netclaw-dev/netclaw/pull/1916), [#1915](https://github.com/netclaw-dev/netclaw/pull/1915), [#1890](https://github.com/netclaw-dev/netclaw/pull/1890), [#1926](https://github.com/netclaw-dev/netclaw/pull/1926), [#1929](https://github.com/netclaw-dev/netclaw/pull/1929))
- **Tool rationale required and preserved across tool loops** — Tool execution now requires a rationale and carries it across loop iterations ([#1933](https://github.com/netclaw-dev/netclaw/pull/1933))
- **OAuth refresh failure diagnostics on auth-loss** — Auth demotion now reports which binding field is missing or mismatched, refresh-token state, and token expiry ([#1969](https://github.com/netclaw-dev/netclaw/pull/1969))
- **Eval harness separates stdout and stderr** — No more false eval failures from diagnostic lines parsed as JSON output ([#1896](https://github.com/netclaw-dev/netclaw/pull/1896))
- **CI test stability** — Deterministic fixes for flaky session-log, reminder, and MCP startup tests ([#1991](https://github.com/netclaw-dev/netclaw/pull/1991), [#1927](https://github.com/netclaw-dev/netclaw/pull/1927), [#1906](https://github.com/netclaw-dev/netclaw/pull/1906), [#1904](https://github.com/netclaw-dev/netclaw/pull/1904))

### Dependency Updates
- **Bump ModelContextProtocol.Core** — 2.1.0 → 2.2.0 ([#1938](https://github.com/netclaw-dev/netclaw/pull/1938))
- **Bump Termina** — 0.16.2 ([#1953](https://github.com/netclaw-dev/netclaw/pull/1953))
- **Bump Microsoft.NET.Test.Sdk** — 18.8.1 → 18.9.0 ([#1971](https://github.com/netclaw-dev/netclaw/pull/1971))
- **Bump Testcontainers** — 4.13.0 → 4.14.0 ([#1972](https://github.com/netclaw-dev/netclaw/pull/1972))
- **Bump Microsoft.SourceLink.GitHub** — 10.0.301 → 10.0.400 ([#1902](https://github.com/netclaw-dev/netclaw/pull/1902))
- **Bump microsoft-platform packages** — Microsoft.AspNetCore.DataProtection and System.Security.Cryptography.Xml 10.0.10 → 10.0.11 ([#1900](https://github.com/netclaw-dev/netclaw/pull/1900))
- **Pin SSH.NET to 2026.0.0** — Resolves CVE-2026-48798 in the Testcontainers transitive dependency (test-only, no runtime exposure) ([#1909](https://github.com/netclaw-dev/netclaw/pull/1909))

## 0.26.0-beta.4 (2026-08-12)

### Features
- **Timezone-aware cron schedules** — Cron reminders now accept the Vixie `CRON_TZ=<IANA-zone>` prefix, so schedules evaluate DST-aware in a chosen time zone; expressions without the prefix still run in UTC, and unknown or Windows-style zones fail with IANA guidance ([#1789](https://github.com/netclaw-dev/netclaw/pull/1789))
- **Netclaw identity shown on MCP OAuth consent screens** — RFC 7591 dynamic client registration now sends `client_uri` and `logo_uri`, so authorization servers render a recognizable Netclaw consent screen instead of a bare client name ([#1877](https://github.com/netclaw-dev/netclaw/pull/1877))
- **`skill list` shows MCP prompt skills** — The CLI now reads the daemon's live skill registry via a new `GET /api/skills` endpoint, so dynamic MCP prompt skills appear in listings; the command now requires the daemon and reports "Daemon unavailable" instead of silently degrading ([#1891](https://github.com/netclaw-dev/netclaw/pull/1891))
- **Reminder scheduling failures surface loudly** — Post-fire rescheduling and startup-reconcile failures now emit a `ReminderScheduleFailed` warning, count toward the auto-disable threshold, and trigger the Critical alert plus channel notice on disable — a reminder can no longer silently stay enabled and never fire ([#1886](https://github.com/netclaw-dev/netclaw/pull/1886))

### Bug Fixes
- **Unknown Bash argument data no longer blocks safe commands** — The approval analyzer treats unresolved parameter expansion as structurally safe when ShellSyntaxTree classifies it as a non-path argument; dynamic command identities, paths, redirects, and substitutions still fail closed ([#1875](https://github.com/netclaw-dev/netclaw/pull/1875))

### Internal Improvements
- **General shell approval analysis required** — Constitution-only change prohibiting executable-specific parsing in the approval layer; unresolved inputs stay strict when general shell facts are unavailable ([#1874](https://github.com/netclaw-dev/netclaw/pull/1874))
- **Structured shell approval policy spec** — OpenSpec proposal, design, and spec with evidence fixtures (D01–D18 approval matrix) documenting the typed, per-candidate coverage pipeline ([#1876](https://github.com/netclaw-dev/netclaw/pull/1876))

## 0.26.0-beta.3 (2026-08-11)

### Features
- **Agents declare shell working scope up front** — Agents are now guided to declare their working directory once before multi-command work in a named project, pass the directory to the shell tool instead of inline `cd`, and retry rejected paths instead of working around them — fewer redundant approval prompts and more reliable scoped approvals ([#1870](https://github.com/netclaw-dev/netclaw/pull/1870))

### Bug Fixes
- **Shell approval scopes fixed for dotted paths** — Dotted and hidden paths (e.g. `.netclaw/`) and git refs now produce correct approval scopes, with normalized-verb matching and `git ls-tree` recognized as safe ([#1868](https://github.com/netclaw-dev/netclaw/pull/1868))
- **MCP: dead OAuth registrations discarded again under SDK 2.1** — The MCP SDK 2.1 discovery probe no longer swallows token-endpoint `invalid_client` rejections during interactive OAuth; rejected client registrations are discarded instead of wedging ([#1865](https://github.com/netclaw-dev/netclaw/pull/1865))

### Dependency Updates
- **Bump ModelContextProtocol** — 2.0.0 → 2.1.0 ([#1865](https://github.com/netclaw-dev/netclaw/pull/1865))
- **Bump Anthropic** — 12.39.0 → 12.40.0 ([#1842](https://github.com/netclaw-dev/netclaw/pull/1842))
- **Bump ShellSyntaxTree** — 0.2.0 → 0.3.0 ([#1867](https://github.com/netclaw-dev/netclaw/pull/1867))

## 0.26.0-beta.2 (2026-08-10)

### Features
- **MCP server prompts as dynamic skills** — Prompts exposed by connected MCP servers are now loadable through the skill index, with argument validation and cross-server conflict rejection ([#1813](https://github.com/netclaw-dev/netclaw/pull/1813))
- **Native PowerShell on Windows** — The daemon now runs Windows shell execution through native PowerShell (7.6 preferred, 5.1 fallback) instead of cross-language emulation, with unified execution, analysis, and approval policy ([#1848](https://github.com/netclaw-dev/netclaw/pull/1848))

### Bug Fixes
- **Reminders no longer skipped on capacity** — The execution-capacity gate that settled reminders as skipped is removed; reminders now defer instead of silently dropping ([#1839](https://github.com/netclaw-dev/netclaw/pull/1839))
- **MCP: dead OAuth connections no longer report Connected forever** — Servers with expired OAuth tokens demote to AwaitingAuth with an operator alert instead of wedging the daemon ([#1841](https://github.com/netclaw-dev/netclaw/pull/1841))
- **MCP: HTTP protocol fallback header race fixed** — The stale MCP protocol-version header is only removed from initialize requests; daemon and CLI now share one HTTP client ([#1861](https://github.com/netclaw-dev/netclaw/pull/1861))
- **Daemon working directory preserved** — The daemon no longer runs from a temp directory that cleanup could delete out from under it ([#1853](https://github.com/netclaw-dev/netclaw/pull/1853))
- **`/dev/null` approval scope fixed** — Safe commands redirecting to `/dev/null` no longer create a reusable `/dev` approval scope ([#1852](https://github.com/netclaw-dev/netclaw/pull/1852))
- **PowerShell host probe hardened** — Slow `pwsh` cold starts no longer brick daemon startup; the probe retries and falls back to Windows PowerShell 5.1 ([#1859](https://github.com/netclaw-dev/netclaw/pull/1859))

### Internal Improvements
- **Shell approval analysis migrated to ShellSyntaxTree** — Bash and PowerShell approval decisions now use the ShellSyntaxTree parser with fail-closed unknown syntax ([#1835](https://github.com/netclaw-dev/netclaw/pull/1835), [#1836](https://github.com/netclaw-dev/netclaw/pull/1836), [#1855](https://github.com/netclaw-dev/netclaw/pull/1855))
- **PowerShell approval repetition reduced** — Safe PowerShell command sequences prompt less often ([#1837](https://github.com/netclaw-dev/netclaw/pull/1837))
- **Bash hidden-execution approval boundaries pinned** — `source` builtins and nameref-deferred execution now require one-shot approvals ([#1838](https://github.com/netclaw-dev/netclaw/pull/1838))
- **PowerShell execution-region approvals improved** — Host and body commands must each match approval policy independently ([#1857](https://github.com/netclaw-dev/netclaw/pull/1857))

### Dependency Updates
- **Bump SlackNet** — 0.17.10 → 0.17.11 ([#1844](https://github.com/netclaw-dev/netclaw/pull/1844))
- **Bump ShellSyntaxTree** — 0.2.0 → 0.3.0-alpha.6

## 0.26.0-beta.1 (2026-08-09)

### Features
- **MCP OAuth surfaced at add time** — `netclaw mcp auth` now runs before permission prompts when adding a server ([#1773](https://github.com/netclaw-dev/netclaw/pull/1773))
- **Mention-required thread replies** — Slack, Discord, and Mattermost channels can gate thread replies on an @-mention, with per-channel control ([#1783](https://github.com/netclaw-dev/netclaw/pull/1783))
- **Mention-triggered thread history backfill** — Thread history is backfilled when a mention arrives on mention-gated channels ([#1798](https://github.com/netclaw-dev/netclaw/pull/1798))
- **File reads reach as far as the shell** — File-read tool permission scope now matches the shell tool's reach ([#1770](https://github.com/netclaw-dev/netclaw/pull/1770))
- **TUI state preserved across navigation** — Provider and model pages keep their state when navigating or refreshing ([#1804](https://github.com/netclaw-dev/netclaw/pull/1804))
- **Background job and one-shot reminder cleanup** — Completed background job definitions and successful one-shot reminders are pruned automatically ([#1821](https://github.com/netclaw-dev/netclaw/pull/1821))

### Bug Fixes
- **Approval prompts: already-granted candidates skipped** — Shell candidates with existing grants no longer re-trigger approval ([#1830](https://github.com/netclaw-dev/netclaw/pull/1830))
- **Approval: safe shell stages compose with grants** — Pipelines of safe stages now compose correctly with one-time grants ([#1828](https://github.com/netclaw-dev/netclaw/pull/1828))
- **Approval: background job control bypasses prompts** — `check_background_job` and cancellation no longer demand approval ([#1817](https://github.com/netclaw-dev/netclaw/pull/1817))
- **Reminders: failed one-shot executions retained** — Failed one-shot reminders retry instead of being dropped, with execution history recorded ([#1812](https://github.com/netclaw-dev/netclaw/pull/1812))
- **Approval: phantom directory scopes removed** — Stored approval patterns no longer fabricate fake directory scopes ([#1799](https://github.com/netclaw-dev/netclaw/pull/1799))
- **Approval: quoted free-text operands dropped from stored patterns** — Quoted free-text arguments are no longer baked into stored approval patterns ([#1815](https://github.com/netclaw-dev/netclaw/pull/1815))
- **Approval: trailing-slash globs scoped to parent** — `dir/`-style globs are scoped to their parent directory, closing a scope-expansion gap ([#1785](https://github.com/netclaw-dev/netclaw/pull/1785))
- **Approval: immediate-retry bypass seeded per approved scope** — Every approved scope now gets the immediate-retry bypass ([#1800](https://github.com/netclaw-dev/netclaw/pull/1800))
- **Sessions: tool-batch wedge fixed** — Unanswered tool calls are closed out before the error reply is sent ([#1796](https://github.com/netclaw-dev/netclaw/pull/1796))
- **TUI: stale provider/model state refreshed** — Provider and model manager pages no longer show stale data ([#1827](https://github.com/netclaw-dev/netclaw/pull/1827))
- **Security: ToolAccessPolicy fail-closed** — Deny-list and protected-path policies are now required; a policy missing them can no longer silently allow blocked commands ([#1787](https://github.com/netclaw-dev/netclaw/pull/1787))

### Dependency Updates
- **Bump Termina** — 0.16.0 → 0.16.1
- **Bump SkiaSharp.NativeAssets.Linux.NoDependencies** — 4.151.0 → 4.151.1
- **Bump Verify.XunitV3** — 31.20.0 → 31.28.0

## 0.25.4 (2026-08-06)

### Bug Fixes
- **MCP: live tool catalog refresh** — The daemon now re-lists healthy MCP servers' tool catalogs on a throttled cadence, so servers that add, remove, rename, or edit tools mid-session become visible to the model without a disconnect + reconnect ([#1771](https://github.com/netclaw-dev/netclaw/pull/1771))
- **MCP permissions: scroll position preserved** — Editing tool-grid rows with the left/right keys no longer jumps the scroll position back to the top ([#1775](https://github.com/netclaw-dev/netclaw/pull/1775))
- **Shell approvals: static fd-dup redirects allowed** — `2>&1` and similar static file-descriptor duplications are no longer classified as dynamic shell syntax, so safe commands like `git status 2>&1` skip the approval prompt ([#1776](https://github.com/netclaw-dev/netclaw/pull/1776))

## 0.25.3 (2026-08-05)

### Features
- **Pre-execution authorization decisions** — Tool execution now reports the authorization decision with its reason (e.g. `ApprovalExemptShellCandidates`, `StoredApproval`) instead of a bare outcome ([#1745](https://github.com/netclaw-dev/netclaw/pull/1745))

### Bug Fixes
- **TUI: Escape no longer quits the app** — Escape is now a no-op at the root of every TUI page; Ctrl+Q is the only quit key. Fixes accidental app exit ([#1765](https://github.com/netclaw-dev/netclaw/pull/1765))
- **TUI: Escape denies pending approvals** — Escape during an approval prompt now denies the request instead of quitting the app ([#1760](https://github.com/netclaw-dev/netclaw/pull/1760))
- **Slack mention rendering** — `<@user>`, `<@subteam^group>`, and `<#channel>` mentions now render as rich-text elements, including labeled and bang forms and private-channel usergroups ([#1763](https://github.com/netclaw-dev/netclaw/pull/1763))
- **Model capability discovery no longer pollutes config** — Runtime-discovered context window and modality capabilities are no longer persisted to config; operator overrides stay authoritative ([#1761](https://github.com/netclaw-dev/netclaw/pull/1761))
- **Incompatible session history degrades gracefully** — Restored history containing media the active model can't accept is stripped with a warning instead of failing the turn; newly supplied incompatible media is hard-rejected with a clear error ([#1729](https://github.com/netclaw-dev/netclaw/pull/1729))
- **`doctor` shell approval fallback aligned** — The tool-audience doctor check now matches the actual Personal-profile shell approval behavior ([#1744](https://github.com/netclaw-dev/netclaw/pull/1744))
- **Shell approvals fail closed** — Shell tool execution now fails closed when no approval candidates are produced ([#1747](https://github.com/netclaw-dev/netclaw/pull/1747))
- **Regex timeout race removed** — Prompt-injection regex matching is now race-free and culture-invariant ([#1748](https://github.com/netclaw-dev/netclaw/pull/1748))
- **Bash approval analysis gaps fixed** — Shell syntax analysis expanded to close approval bypass gaps across hosts ([#1753](https://github.com/netclaw-dev/netclaw/pull/1753))
- **Shell path approval scope hardened** — Every parsed shell path is checked against the approval scope; nested globs fail closed and symlink glob matches are rejected ([#1768](https://github.com/netclaw-dev/netclaw/pull/1768))

### Dependency Updates
- **Bump ShellSyntaxTree** — 0.2.0-alpha → 0.2.0-beta.1
- **Bump SkiaSharp** — 4.150.1 → 4.151.0
- **Bump CsCheck** — 4.7.0 → 4.8.0

## 0.25.2 (2026-08-01)

### Features
- **Provider manager: delete providers** — Providers can now be removed from the provider list via the TUI provider manager ([#1726](https://github.com/netclaw-dev/netclaw/pull/1726))
- **DeepSeek provider support** — First-party DeepSeek model provider with full provider lifecycle, model catalog, and TUI integration ([#1725](https://github.com/netclaw-dev/netclaw/pull/1725))

### Dependency Updates
- **Bump OllamaSharp** — 5.4.27 → 5.4.30
- **Bump Grpc.Tools** — 2.82.0 → 2.83.0

## 0.25.1 (2026-07-31)

### Bug Fixes
- **MCP OAuth lifecycle hardened** — Takes back client registration from the MCP SDK to prevent credential loss on upgrade; fixes `token_endpoint_auth_method` hardcoding issue with RFC 7591 ([#1708](https://github.com/netclaw-dev/netclaw/pull/1708))
- **MCP tool-level auth failures now visible** — Daemon logs MCP `isError: true` responses at warning level, fixes `netclaw mcp auth` fallback for older daemons during upgrades ([#1720](https://github.com/netclaw-dev/netclaw/pull/1720))
- **MCP permissions focus and save interaction fixed in TUI** ([#1694](https://github.com/netclaw-dev/netclaw/pull/1694))
- **Revoke the highlighted approval in TUI** — Approval revocation now targets the currently highlighted item ([#1721](https://github.com/netclaw-dev/netclaw/pull/1721))

### Internal Improvements
- **Migrate to ModelContextProtocol SDK 2.0.0** — Brings in thread-safe token cache and updated OAuth flow ([#1714](https://github.com/netclaw-dev/netclaw/pull/1714))
- **GitHub Copilot GHE: route models through advertised responses endpoint** with model catalog support ([#1707](https://github.com/netclaw-dev/netclaw/pull/1707))

### Dependency Updates
- **Bump Anthropic SDK** — 12.35.1 → 12.39.0
- **Bump Mattermost.NET** — 5.0.3 → 5.0.7
- **Bump Netclaw.SkillClient** — 0.4.0 → 0.4.1
- **Bump OpenTelemetry** — 1.16.0 → 1.17.0
- **Bump OllamaSharp** — 5.4.25 → 5.4.27
- **Bump SkiaSharp.NativeAssets.Linux** — 4.148.0 → 4.150.1
- **Bump Microsoft.SourceLink.GitHub** — 10.0.300 → 10.0.301
- **Bump Akka** — Akka.Cluster.Sharding and Akka.Persistence updated

## 0.25.0 (2026-07-18)

This stable release concludes the 0.25.0 beta cycle (five beta releases from 0.25.0-beta.1 through beta.5) and adds a round of final polish focused on installation, CLI reliability, and daemon shutdown robustness.

### Features
- **Automated shell PATH integration for installers** — Unix and Windows installers now automatically update the user's shell profile or PATH registry on install. Unix installers detect the active shell (bash, zsh, fish) and source a self-guarding `~/.netclaw/env` script from the correct RC file with duplicate prevention. Windows installers modify the User-scope PATH and broadcast `WM_SETTINGCHANGE`. Use `--skip-shell` (`-SkipShell` on Windows) to opt out ([#1687](https://github.com/netclaw-dev/netclaw/pull/1687))
- **Timestamped HMAC verification for webhooks** — Webhook routes now verify request timestamps alongside HMAC signatures to prevent replay attacks, with configurable HMAC algorithm support ([#1660](https://github.com/netclaw-dev/netclaw/pull/1660))
- **TSV support in content scanner** — Added `text/tab-separated-values` as a recognized MIME type, allowing TSV files to be scanned and processed by the content pipeline ([#1645](https://github.com/netclaw-dev/netclaw/pull/1645))
- **Preserve Git working context across sessions and subagents** — A bounded, audience-aware Git working-context snapshot (branch, worktree, repository, upstream, changed files) now stays current in the system prompt, and coding subagents inherit recent-file/project context so parent sessions merge back only confirmed successful child edits. Measured 20% → 100% success rate on a linked-worktree coding eval ([#1630](https://github.com/netclaw-dev/netclaw/pull/1630))
- **User-written `AGENTS.md` for application-specific agent guidance** — Operators can now author `~/.netclaw/identity/AGENTS.md`, layered after NetClaw's embedded operating core and inherited by sub-agents, to give the running agent deployment-specific mission and workflow guidance. Seeded with a minimal scaffold during init without overwriting existing guidance ([#1622](https://github.com/netclaw-dev/netclaw/pull/1622))
- **Discord DM reminder delivery** — Reminders can now be delivered to Discord DMs via improved `DiscordReminderTargetResolver` ([#1609](https://github.com/netclaw-dev/netclaw/pull/1609))
- **Named model configuration & provider runtime validation** — New `NamedModelConfiguration` and `ProviderRuntimeValidation` types, config schema updates, and CLI wizard improvements for provider/model setup ([#1610](https://github.com/netclaw-dev/netclaw/pull/1610))
- **SkillServer native sub-agent sync** — Optional native manifest sidecar sync for server-managed sub-agents, keeping RFC skill sync primary while downloading verified native artifacts when available. Local sub-agent files load before server-feed files so user-authored definitions always win ([#1539](https://github.com/netclaw-dev/netclaw/pull/1539))
- **GitHub Enterprise Copilot support** — Authenticate GitHub Copilot tokens against GHE instances and route requests to the correct data residency endpoint ([#1509](https://github.com/netclaw-dev/netclaw/pull/1509), [#1512](https://github.com/netclaw-dev/netclaw/pull/1512), [#1555](https://github.com/netclaw-dev/netclaw/pull/1555))
- **Slack native processing status** — Real-time processing indicators in Slack instead of generic "working" messages ([#1524](https://github.com/netclaw-dev/netclaw/pull/1524))
- **Reminder failure visibility** — Operators can now see when reminders fail or are skipped ([#1503](https://github.com/netclaw-dev/netclaw/pull/1503))
- **Degraded startup mode** — Daemon starts with a "no valid model" banner when no provider is configured, instead of failing host startup ([#1540](https://github.com/netclaw-dev/netclaw/pull/1540))
- **Synced skill resources via shell** — Skill resources synced from the cloud can now execute via shell commands ([#1551](https://github.com/netclaw-dev/netclaw/pull/1551))
- **DwarfStar (ds4) provider support** — New openai-compatible backend strategy supporting DwarfStar models ([#1349](https://github.com/netclaw-dev/netclaw/pull/1349))
- **Inherit embedded AGENTS.md for sub-agents** — Sub-agents now inherit the parent session's embedded operating rules from AGENTS.md ([#1490](https://github.com/netclaw-dev/netclaw/pull/1490))
- **Show advertised skill count for remote skill servers** — The Skill Sources config screen now displays how many skills each remote server advertises ([#1452](https://github.com/netclaw-dev/netclaw/pull/1452))

### Bug Fixes
- **MCP arguments shown in approval prompts** — Fixed: approval prompts now display full MCP tool arguments for better operator context ([#1689](https://github.com/netclaw-dev/netclaw/pull/1689))
- **CLI reports unresolved model references cleanly** — Fixed: CLI no longer crashes with opaque errors when a model reference cannot be resolved ([#1680](https://github.com/netclaw-dev/netclaw/pull/1680))
- **CLI handles model migration errors without crashing** — Fixed: model migration validation errors are now reported gracefully instead of crashing the CLI ([#1678](https://github.com/netclaw-dev/netclaw/pull/1678))
- **CLI rejects numeric model modalities** — Fixed: model modality values are now validated as strings, rejecting numeric input ([#1677](https://github.com/netclaw-dev/netclaw/pull/1677))
- **Daemon-stop session drain bounded** — Fixed: `netclaw daemon stop` now properly bounds the session drain timeout, preventing hangs on interactive-tool-paused sessions and giving the CLI adequate headroom ([#1673](https://github.com/netclaw-dev/netclaw/pull/1673))
- **Reminders rescan definitions before startup alerts** — Fixed: startup alerts now fire only after reminder definitions are fully loaded, preventing missed or duplicate reminders on restart ([#1653](https://github.com/netclaw-dev/netclaw/pull/1653))
- **OpenAI client 2.12 compatibility** — Fixed: updated OpenAI provider plugin to work with OpenAI client 2.12+ changes ([#1654](https://github.com/netclaw-dev/netclaw/pull/1654))
- **Attachment path guidance** — Fixed: authoritative attachment path guidance now points to the correct session media directory ([#1686](https://github.com/netclaw-dev/netclaw/pull/1686))
- **Windows actor checks made deterministic** — Fixed: Windows actor lifecycle tests were non-deterministic ([#1661](https://github.com/netclaw-dev/netclaw/pull/1661))
- **Ollama smoke test pinned** — Fixed: pinned Ollama installer to a specific release for smoke test stability ([#1659](https://github.com/netclaw-dev/netclaw/pull/1659))
- **VHS process group timeout** — Fixed: full VHS process group now times out properly during smoke tests ([#1655](https://github.com/netclaw-dev/netclaw/pull/1655))
- **Screenshot regression determinism** — Fixed: screenshot regression suite now uses pixel comparison with blank/partial retry and Termina seam fixes ([#1451](https://github.com/netclaw-dev/netclaw/pull/1451))
- **Legacy model environment overrides serialized** — Fixed: legacy model environment overrides are now properly serialized ([#1656](https://github.com/netclaw-dev/netclaw/pull/1656))
- **Subagent token usage tracked** — Sub-agent token usage is now properly tracked and reported ([#1597](https://github.com/netclaw-dev/netclaw/pull/1597))
- **Subagent fail-closed for unattended approvals** — Fixed: subagents fail safely when approvals are required but no human is present ([#1616](https://github.com/netclaw-dev/netclaw/pull/1616))
- **Memory core: curation documents stable** — Fixed: memory curation documents were unstable across writes, causing cross-session memory recall issues ([#1575](https://github.com/netclaw-dev/netclaw/pull/1575))
- **Model set/picker preserves modalities** — Fixed: model selection UI now preserves model modalities correctly ([#1610](https://github.com/netclaw-dev/netclaw/pull/1610))
- **Slack processing status serialization** — Fixed: Slack processing status now serializes correctly for all states ([#1556](https://github.com/netclaw-dev/netclaw/pull/1556))
- **Autonomous sessions can write workspace directory** — Fixed: autonomous sessions could not write to the workspace directory ([#1498](https://github.com/netclaw-dev/netclaw/pull/1498))
- **Reminder duplicate-execution guard** — Fixed: Mode A reminder session wedge prevented the duplicate guard from releasing ([#1500](https://github.com/netclaw-dev/netclaw/pull/1500))
- **Reset stops daemon first** — Fixed: `netclaw reset` now stops the daemon before proceeding and shows a progress screen ([#1494](https://github.com/netclaw-dev/netclaw/pull/1494))
- **MCP tool errors display cleanly** — Fixed: MCP errors now show as attributed messages instead of raw JSON dumps ([#1510](https://github.com/netclaw-dev/netclaw/pull/1510))
- **TUI: auto-advance the add-skill-server flow** — Fixed: skill server probe flow now auto-advances on successful connection ([#1458](https://github.com/netclaw-dev/netclaw/pull/1458))
- **TUI: auto-start init health checks** — Fixed: init health checks now start automatically ([#1454](https://github.com/netclaw-dev/netclaw/pull/1454))
- **TUI: discoverable Done rows** — Added "Done" back-out rows across Security & Access menus and config screens ([#1448](https://github.com/netclaw-dev/netclaw/pull/1448), [#1441](https://github.com/netclaw-dev/netclaw/pull/1441))
- **TUI: session browser selection highlight** — Fixed: session browser now shows selection highlight ([#1531](https://github.com/netclaw-dev/netclaw/pull/1531))
- **TUI: identity redo timezone loop** — Fixed: identity redo would loop on timezone; also fixed session browser regression ([#1518](https://github.com/netclaw-dev/netclaw/pull/1518))
- **TUI: flaky host crash during reset** — Fixed: thread-unsafe ReactiveProperty access during reset caused crashes ([#1525](https://github.com/netclaw-dev/netclaw/pull/1525))
- **Subagent terminal result summaries** — Fixed: subagent terminal results now display properly ([#1519](https://github.com/netclaw-dev/netclaw/pull/1519))
- **UTF-8 BOM in skill frontmatter** — Fixed: skill scanner now strips UTF-8 BOM before parsing YAML frontmatter ([#1583](https://github.com/netclaw-dev/netclaw/pull/1583))
- **Block system and external skill mutations** — Fixed: skill system now blocks mutations to system and externally-synced skills ([#1457](https://github.com/netclaw-dev/netclaw/pull/1457))
- **Self-monitoring spawn_agent liveness respected** — Fixed: tool pipeline now respects self-monitoring spawn_agent liveness ([#1456](https://github.com/netclaw-dev/netclaw/pull/1456))

### Breaking Changes
- **Removed silent local-ollama fallback** — Users without any provider configured will now see a "no valid model" banner instead of an automatic fallback to local Ollama. Explicit provider configuration is now required for full functionality ([#1540](https://github.com/netclaw-dev/netclaw/pull/1540))

### Improvements
- **Tool execution pipeline refactoring** — Restructured the session tool execution pipeline with isolated invocation scope, composed execution pipeline, typed sub-agent context isolation, and proper lifecycle cleanup ([#1641](https://github.com/netclaw-dev/netclaw/pull/1641), [#1643](https://github.com/netclaw-dev/netclaw/pull/1643), [#1644](https://github.com/netclaw-dev/netclaw/pull/1644), [#1646](https://github.com/netclaw-dev/netclaw/pull/1646))
- **Logical skill access and authoritative inventory refresh** — Skill loading now resolves through logical `skill_load`/`skill_read_resource` access with native > managed-feed > external precedence. Startup, sync, watcher, and `skill_manage` inventory rebuilds centralized through one live-source refresher ([#1634](https://github.com/netclaw-dev/netclaw/pull/1634))
- **Session actor decomposed into transient-state handlers** — `LlmSessionActor` decomposed into smaller, focused handlers for improved maintainability and testability ([#1496](https://github.com/netclaw-dev/netclaw/pull/1496))
- **Log stream partitioned by session** — Each session now gets its own `session.log` while `daemon.log` remains sparse. Cleaner logs and better observability with OTEL union support ([#1499](https://github.com/netclaw-dev/netclaw/pull/1499))
- **Memory core redesign** — Shared curation evaluator unified across both memory write pipelines so they can never diverge. July 2026 audit: revived curation LLM, balanced prompt, and recall precision re-tune ([#1575](https://github.com/netclaw-dev/netclaw/pull/1575), [#1568](https://github.com/netclaw-dev/netclaw/pull/1568))

### Security
- **Pinned Microsoft.OpenApi to 2.7.5** — Addresses CVE-2026-49451 ([#1543](https://github.com/netclaw-dev/netclaw/pull/1543))
- **Suppressed GHSA-2m69-gcr7-jv3q (SQLitePCLRaw CVE-2025-6965)** — Temporarily suppressed until upstream patches available. Tracking: dotnet/efcore#38257 ([#1444](https://github.com/netclaw-dev/netclaw/pull/1444))

### Dependency Updates
- **OpenAI SDK** — Updated to 2.12 (required for API compatibility) ([#1654](https://github.com/netclaw-dev/netclaw/pull/1654))
- **Microsoft.Extensions.AI** — 10.6.0 → 10.8.0 ([#1650](https://github.com/netclaw-dev/netclaw/pull/1650))
- **Microsoft.AspNetCore.DataProtection** — 10.0.9 → 10.0.10 ([#1657](https://github.com/netclaw-dev/netclaw/pull/1657))
- **Microsoft.NET.Test.Sdk** — 18.7.0 → 18.8.1 ([#1651](https://github.com/netclaw-dev/netclaw/pull/1651))
- **Mattermost.NET** — 5.0.0 → 5.0.3 ([#1626](https://github.com/netclaw-dev/netclaw/pull/1626))
- **SkillServer** — `Netclaw.SkillClient` 0.4.0-beta.4 → 0.4.0 (stable) ([#1638](https://github.com/netclaw-dev/netclaw/pull/1638))
- **Anthropic SDK** — 12.30.0 → 12.35.1 ([#1488](https://github.com/netclaw-dev/netclaw/pull/1488), [#1561](https://github.com/netclaw-dev/netclaw/pull/1561))
- **Akka** — Akka.Cluster.Sharding and Akka.Persistence 1.5.69 → 1.5.70 ([#1560](https://github.com/netclaw-dev/netclaw/pull/1560))
- **Testcontainers** — 4.12.0 → 4.13.0 ([#1563](https://github.com/netclaw-dev/netclaw/pull/1563))
- **YamlDotNet** — 18.0.0 → 18.1.0 ([#1516](https://github.com/netclaw-dev/netclaw/pull/1516))
- **Verify.XunitV3** — 31.19.1 → 31.20.0 ([#1446](https://github.com/netclaw-dev/netclaw/pull/1446))

## 0.25.0-beta.5 (2026-07-16)

### Features
- **Timestamped HMAC verification for webhooks** — Webhook routes now verify request timestamps alongside HMAC signatures to prevent replay attacks, with configurable HMAC algorithm support ([#1660](https://github.com/netclaw-dev/netclaw/pull/1660))
- **TSV support in content scanner** — Added `text/tab-separated-values` as a recognized MIME type, allowing TSV files to be scanned and processed by the content pipeline ([#1645](https://github.com/netclaw-dev/netclaw/pull/1645))

### Bug Fixes
- **Reminders rescan definitions before startup alerts** — Fixed: startup alerts could fire before reminder definitions were fully loaded, causing missed or duplicate reminders on restart ([#1653](https://github.com/netclaw-dev/netclaw/pull/1653))
- **OpenAI client 2.12 compatibility** — Updated OpenAI provider plugin to work with the breaking API change in OpenAI SDK 2.12 (`OpenAIClientOptions` → `ResponsesClientOptions`) ([#1654](https://github.com/netclaw-dev/netclaw/pull/1654))

### Improvements
- **Tool execution pipeline refactoring** — Restructured the session tool execution pipeline with isolated invocation scope, composed execution pipeline, typed subagent context isolation, and proper lifecycle cleanup. Fixes subagent fatal spawn failures ([#1641](https://github.com/netclaw-dev/netclaw/pull/1641), [#1643](https://github.com/netclaw-dev/netclaw/pull/1643), [#1644](https://github.com/netclaw-dev/netclaw/pull/1644), [#1646](https://github.com/netclaw-dev/netclaw/pull/1646))

### Dependency Updates
- **Bump OpenAI client to 2.12** — Required for compatibility with the updated SDK API ([#1654](https://github.com/netclaw-dev/netclaw/pull/1654))
- **Bump Microsoft.Extensions.AI** — 10.6.0 → 10.8.0 ([#1650](https://github.com/netclaw-dev/netclaw/pull/1650))
- **Bump Microsoft.AspNetCore.DataProtection** — 10.0.9 → 10.0.10 ([#1657](https://github.com/netclaw-dev/netclaw/pull/1657))
- **Bump Microsoft.NET.Test.Sdk** — 18.7.0 → 18.8.1 ([#1651](https://github.com/netclaw-dev/netclaw/pull/1651))
- **Bump Mattermost.NET** — 5.0.0 → 5.0.3 ([#1626](https://github.com/netclaw-dev/netclaw/pull/1626))

## 0.25.0-beta.4 (2026-07-14)

### Features
- **Preserve Git working context across sessions and subagents** — A bounded, audience-aware Git working-context snapshot (branch, worktree, repository, upstream, changed files) now stays current in the system prompt, and coding subagents inherit recent-file/project context so parent sessions merge back only confirmed successful child edits. Measured 20% → 100% success rate on a linked-worktree coding eval ([#1630](https://github.com/netclaw-dev/netclaw/pull/1630))
- **User-written `AGENTS.md` for application-specific agent guidance** — Operators can now author `~/.netclaw/identity/AGENTS.md`, layered after Netclaw's embedded operating core and inherited by sub-agents, to give the running agent deployment-specific mission and workflow guidance. Seeded with a minimal scaffold during init without overwriting existing guidance ([#1622](https://github.com/netclaw-dev/netclaw/pull/1622))

### Bug Fixes
- **Memory curation no longer overwrites existing documents on collision** — Fixed: when a curation Create decision targeted an anchor that already had a document, the write silently overwrote the existing title, body, and classification with no history — observed 88 times in 14 days in production, including one case that destroyed an LLM-merged document. Collisions now append the new content under a dated separator instead of overwriting; a verbatim duplicate is skipped as a no-op ([#1637](https://github.com/netclaw-dev/netclaw/pull/1637))
- **STDIO MCP server arguments no longer rewritten** — Fixed: configured STDIO MCP server arguments were being rewritten by the daemon; the daemon now preserves them as configured. Also simplified to one daemon-owned client per configured MCP server, removing Playwright-specific session-scoped process handling ([#1636](https://github.com/netclaw-dev/netclaw/pull/1636))

### Improvements
- **Logical skill access and authoritative inventory refresh** — Skill loading now resolves through logical `skill_load`/`skill_read_resource` access instead of physical skill-root paths, with native > managed-feed > external precedence. Startup, sync, watcher, and `skill_manage` inventory rebuilds are now centralized through one live-source refresher publishing atomic registry snapshots ([#1634](https://github.com/netclaw-dev/netclaw/pull/1634))

### Dependency Updates
- **Bump SkillServer to stable** — `Netclaw.SkillClient` 0.4.0-beta.4 → 0.4.0 (stable release) ([#1638](https://github.com/netclaw-dev/netclaw/pull/1638))

## 0.25.0-beta.3 (2026-07-12)

### Features
- **Discord DM reminder delivery** — Reminders can now be delivered to Discord DMs via improved `DiscordReminderTargetResolver` ([#1609](https://github.com/netclaw-dev/netclaw/pull/1609))
- **Named model configuration & provider runtime validation** — New `NamedModelConfiguration` and `ProviderRuntimeValidation` types, config schema updates, and CLI wizard improvements for provider/model setup ([#1610](https://github.com/netclaw-dev/netclaw/pull/1610))

### Bug Fixes
- **Model set/picker preserves hand-set modalities** — Re-selecting the same model no longer wipes operator-set `InputModalities`/`OutputModalities` and `ContextWindow`. Added `--input-modalities`, `--output-modalities`, `--clear-modalities`, and `--clear-context-window` CLI flags ([#1610](https://github.com/netclaw-dev/netclaw/pull/1610))
- **Slack processing status serialization** — Slack processing status updates are now serialized to prevent race conditions during concurrent sends ([#1556](https://github.com/netclaw-dev/netclaw/pull/1556))
- **Sub-agent token usage tracked in daily stats** — Sub-agent LLM calls now record token usage, making them visible in `netclaw stats` ([#1597](https://github.com/netclaw-dev/netclaw/pull/1597))
- **Subagents fail closed for unattended approvals** — When a subagent requires approval but the session is unattended, it now fails closed instead of proceeding or hanging ([#1616](https://github.com/netclaw-dev/netclaw/pull/1616))

## 0.25.0-beta.2 (2026-07-07)

### Bug Fixes
- **UTF-8 BOM in skill frontmatter** — Fixed: skill scanner now strips UTF-8 BOM (`\uFEFF`) before parsing YAML frontmatter, and populates `SkillName` on all `SkillScanIssue` records so degenerate frontmatter no longer crashes the scan ([#1583](https://github.com/netclaw-dev/netclaw/pull/1583))
- **Model capability provenance logging** — Fixed: daemon now logs effective model capabilities with their provenance source, improving diagnostic visibility for model configuration issues ([#1584](https://github.com/netclaw-dev/netclaw/pull/1584))

### Dependency Updates
- **Bump SkillServer** — `Netclaw.SkillClient` 0.4.0-beta.1 → 0.4.0-beta.3 and adapt to API changes ([#1593](https://github.com/netclaw-dev/netclaw/pull/1593))

## 0.25.0-beta.1 (2026-07-05)

### Features
- **SkillServer native sub-agent sync** — Optional native manifest sidecar sync for server-managed sub-agents, keeping RFC skill sync primary while downloading verified native artifacts when available. Local sub-agent files load before server-feed files so user-authored definitions always win ([#1539](https://github.com/netclaw-dev/netclaw/pull/1539))

### Bug Fixes
- **Systemd shell tool PATH** — Fixed: daemon now captures the operator's full PATH into the systemd EnvironmentFile instead of relying on a hardcoded list, so tools like `~/.dotnet/dotnet` are visible to the shell tool ([#1565](https://github.com/netclaw-dev/netclaw/pull/1565))

### Memory
- **Shared curation evaluator** — Unified curation logic across both memory write pipelines (inline per-session actor and daemon checkpoint-worker) so they can never diverge again ([#1575](https://github.com/netclaw-dev/netclaw/pull/1575))
- **Memory audit quick wins** — July 2026 audit: revived curation LLM, balanced prompt, and recall precision re-tune ([#1568](https://github.com/netclaw-dev/netclaw/pull/1568))

## 0.24.4 (2026-07-03)

### Bug Fixes
- **Environment-variable-only configuration** — Fixed doctor misdiagnosis and OAuth config-file side effect that prevented daemon startup when configuration is supplied entirely via environment variables ([#1569](https://github.com/netclaw-dev/netclaw/pull/1569))
- **Remote daemon CLI** — Fixed: CLI no longer demands a local daemon config file when the daemon is explicitly configured as remote ([#1567](https://github.com/netclaw-dev/netclaw/pull/1567))

### Dependency Updates
- **Bump Anthropic SDK** — 12.34.1 → 12.35.1 ([#1561](https://github.com/netclaw-dev/netclaw/pull/1561))
- **Bump Akka group** — Akka.Cluster.Sharding and Akka.Persistence 1.5.69 → 1.5.70 ([#1560](https://github.com/netclaw-dev/netclaw/pull/1560))

## 0.24.3 (2026-07-03)

### Features
- **GitHub Enterprise Copilot support** — Authenticate GitHub Copilot tokens against GHE instances and route requests to the correct data residency endpoint ([#1509](https://github.com/netclaw-dev/netclaw/pull/1509), [#1512](https://github.com/netclaw-dev/netclaw/pull/1512), [#1555](https://github.com/netclaw-dev/netclaw/pull/1555))
- **Slack native processing status** — Real-time processing indicators in Slack instead of generic "working" messages ([#1524](https://github.com/netclaw-dev/netclaw/pull/1524))
- **Reminder failure visibility** — Operators can now see when reminders fail or are skipped ([#1503](https://github.com/netclaw-dev/netclaw/pull/1503))
- **Degraded startup mode** — Daemon starts with a "no valid model" banner when no provider is configured, instead of failing host startup. Removed silent local-ollama default ([#1540](https://github.com/netclaw-dev/netclaw/pull/1540))
- **Synced skill resources via shell** — Skill resources synced from the cloud can now execute via shell commands ([#1551](https://github.com/netclaw-dev/netclaw/pull/1551))

### Bug Fixes
- **Autonomous sessions can write workspace directory** — Fixed: autonomous sessions could not write to the workspace directory ([#1498](https://github.com/netclaw-dev/netclaw/pull/1498))
- **Reminder duplicate-execution guard** — Fixed: Mode A reminder session wedge prevented the duplicate guard from releasing ([#1500](https://github.com/netclaw-dev/netclaw/pull/1500))
- **Reset stops daemon first** — Fixed: `netclaw reset` now stops the daemon before proceeding and shows a progress screen ([#1494](https://github.com/netclaw-dev/netclaw/pull/1494))
- **MCP tool errors display cleanly** — Fixed: MCP errors now show as attributed messages instead of raw JSON dumps ([#1510](https://github.com/netclaw-dev/netclaw/pull/1510))
- **TUI identity redo timezone loop** — Fixed: identity redo would loop on timezone; also fixed session browser regression ([#1518](https://github.com/netclaw-dev/netclaw/pull/1518))
- **Subagent terminal result summaries** — Fixed: subagent terminal results not displaying properly ([#1519](https://github.com/netclaw-dev/netclaw/pull/1519))
- **Flaky host crash during install reset** — Fixed: thread-unsafe ReactiveProperty access during reset caused crashes ([#1525](https://github.com/netclaw-dev/netclaw/pull/1525))
- **Session browser selection highlight** — Fixed: session browser didn't show selection highlight in TUI ([#1531](https://github.com/netclaw-dev/netclaw/pull/1531))
- **Slack active thread status refresh** — Fixed: thread status not refreshing during tool loops and buffered messages ([#1534](https://github.com/netclaw-dev/netclaw/pull/1534))
- **Stable memory handles** — Fixed: memory handles were unstable, affecting cross-session memory ([#1538](https://github.com/netclaw-dev/netclaw/pull/1538))
- **GHE Copilot routing** — Fixed: GHE Copilot chat now routes to the token's `endpoints.api` host instead of hardcoded `api.githubcopilot.com` ([#1555](https://github.com/netclaw-dev/netclaw/pull/1555))
- **Security: Microsoft.OpenApi CVE-2026-49451** — Pinned to 2.7.5 to address vulnerability ([#1543](https://github.com/netclaw-dev/netclaw/pull/1543))

### Breaking Changes
- **Removed silent local-ollama fallback** — Users without any provider configured will now see a "no valid model" banner instead of an automatic fallback to local Ollama. Explicit provider configuration is now required for full functionality ([#1540](https://github.com/netclaw-dev/netclaw/pull/1540))

### Performance
- **Log stream partitioned by session** — Each session now gets its own `session.log` while `daemon.log` remains sparse. Cleaner logs and better observability with OTEL union support ([#1499](https://github.com/netclaw-dev/netclaw/pull/1499))