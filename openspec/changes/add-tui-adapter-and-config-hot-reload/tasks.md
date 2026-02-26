## 1. CLI Scaffold with Cocona + Termina Hosting

- [x] 1.1 Add Cocona and Termina package references to Directory.Packages.props and Netclaw.App.csproj
- [x] 1.2 Rewrite Program.cs as Cocona entry point with DI registration
- [x] 1.3 Create RunCommand.cs for daemon mode (`netclaw run`)
- [x] 1.4 Wire Termina as hosted service for TUI commands
- [x] 1.5 Verify `dotnet build` passes with new dependencies

## 2. TUI Chat Adapter (`netclaw chat`)

- [x] 2.1 Create TuiInputAdapter implementing adapter contract (SendUserMessage with entity key `tui/{sessionId}`)
- [x] 2.2 Create ChatCommand.cs that hosts actor system in-process and launches TUI
- [x] 2.3 Create ChatPage.cs with StreamingTextNode (scrollable history) and TextInputNode (multi-line input)
- [x] 2.4 Create ChatViewModel.cs with session lifecycle and broadcast subscription
- [x] 2.5 Implement inline tool activity panel (completed with duration, in-progress with spinner)
- [ ] 2.6 Implement MCP status indicator in status bar (green/yellow/red)
- [x] 2.7 E2E: user types → SendUserMessage → session actor → LLM → streaming response in TUI

## 3. TUI Onboarding Wizard (`netclaw init`)

- [ ] 3.1 Create InitCommand.cs that launches Termina wizard
- [ ] 3.2 Create InitWizardPage.cs with 6-step wizard layout (PanelNode, progress bar)
- [ ] 3.3 Create InitWizardViewModel.cs with step state machine and back-navigation
- [ ] 3.4 Implement Step 1: LLM provider (SelectionListNode + TextInputNode for API key / OAuth branch)
- [ ] 3.5 Implement Step 2: Slack configuration (masked TextInputNodes for tokens)
- [ ] 3.6 Implement Step 3: ACL bootstrap (owner identity + initial channels)
- [ ] 3.7 Implement Step 4: MCP servers (SelectionListNode with Memorizer recommended)
- [ ] 3.8 Implement Step 5: Exposure mode (SelectionListNode with security warnings)
- [ ] 3.9 Implement Step 6: Health check (live probe panel with SpinnerNodes → checkmarks)
- [ ] 3.11 Write config file to ~/.netclaw/config/netclaw.json on completion

## 4. Plain CLI Commands

- [ ] 4.1 Create DoctorCommand.cs — startup checks with remediation guidance, exit codes 0/1/2
- [ ] 4.2 Create ConfigCommands.cs — `config show` and `config validate`
- [ ] 4.3 Create AclCommands.cs — `acl validate`, `acl test`, `acl explain`
- [ ] 4.4 Create ProjectCommands.cs — `project list`, `project add`, `project remove`
- [ ] 4.5 Create ScheduleCommands.cs — `schedule list|show|pause|resume|delete`
- [ ] 4.6 Create remaining commands: `environment scan|show`, `mcp list|validate|test`, `memory show`, `tools list|policy`, `test smoke`, `personality reset`

## 5. Config Hot-Reload

- [ ] 5.1 Create ConfigWatcherService as IHostedService with FileSystemWatcher per watched file
- [ ] 5.2 Implement 500ms debounce for file change events
- [ ] 5.3 Implement validate-before-apply with rejection logging
- [ ] 5.4 Implement config file deletion handling (warn, keep existing config)
- [ ] 5.5 Publish ACL change events to policy engine via Akka pub/sub
- [ ] 5.6 Publish provider change events to provider factory via Akka pub/sub
- [ ] 5.7 Publish MCP profile change events to MCP manager via Akka pub/sub
- [ ] 5.8 Publish schedule change events to ScheduleManagerActor via Akka pub/sub
- [ ] 5.9 Integration test: config file write → debounce → validate → actor notification

## 6. Conversational Personality Bootstrap via TUI

- [ ] 6.1 Detect missing soul files on first `netclaw chat` session
- [ ] 6.2 Trigger bootstrap conversation flow in TUI (introduce, learn preferences, scan environment)
- [ ] 6.3 Write PERSONALITY.md, INSTRUCTIONS.md, USER.md to config directory
- [ ] 6.4 Test: bootstrap triggers when files missing, skips when files exist

## 7. Local E2E Validation

- [ ] 7.1 E2E: `netclaw chat` → session → tool call → streaming response
- [ ] 7.2 E2E: scheduled task → fresh session → result displayed
- [ ] 7.3 E2E: config change → hot-reload → policy refresh verified
- [ ] 7.4 CI tests pass without live provider credentials

## 8. Spec Sync

- [ ] 8.1 Sync delta specs from this change to main specs
- [ ] 8.2 Run `openspec validate --all --no-interactive` — passes
- [ ] 8.3 Archive change with `openspec archive add-tui-adapter-and-config-hot-reload`
