## 1. DaemonClient API Surface

- [x] 1.1 Add `ListSessionsAsync()` to `DaemonClient` — HTTP GET to `/api/sessions`, deserialize response into `List<SessionCatalogEntryDto>`, handle daemon-unreachable gracefully (return empty list + log)
- [x] 1.2 Add `SessionCatalogEntryDto` record to the CLI project (persistence ID, channel, title, turn count, last activity, log path) matching the daemon's `SessionCatalogEntry` shape
- [x] 1.3 Add `ResumeSessionAsync(string sessionId)` to `DaemonClient` — calls `EnsureSession` with the provided session ID and `"tui"` channel type, stores the session ID for subsequent `SendAsync` calls
- [x] 1.4 Extend `EnsureSessionInternalAsync` to accept an optional `sessionId` parameter so resumed sessions pass the catalog ID instead of `null`

## 2. CLI Command Wiring

- [x] 2.1 Add `--resume <id>` option parsing in `Program.cs` for the `chat` mode — extract session ID from args and pass to `ChatViewModel` or `DaemonClient`
- [x] 2.2 Add `sessions` mode in `Program.cs` — register Termina with `SessionsPage` route, require daemon connectivity
- [x] 2.3 Update help text to include `sessions` command and `--resume` flag documentation

## 3. TUI Session Browser

- [x] 3.1 Create `SessionsViewModel` — loads sessions via `DaemonClient.ListSessionsAsync()`, exposes observable list, handles selection, formats relative timestamps
- [x] 3.2 Create `SessionsPage` — Terminal.Gui `ListView` bound to `SessionsViewModel`, each row shows `[channel] title (N turns, Xm ago)`, empty state message when no sessions
- [x] 3.3 Wire session selection — on Enter/confirm, navigate from `SessionsPage` to `ChatPage` with the selected session ID as a resume parameter
- [x] 3.4 Handle empty state — show "No sessions found. Press Enter to start a new chat." and navigate to fresh `ChatPage`

## 4. ChatPage Resume Integration

- [x] 4.1 Accept optional resume session ID in `ChatViewModel` — when provided, call `ResumeSessionAsync(id)` instead of `CreateSessionAsync(channelType)`
- [x] 4.2 Show "Resumed: {title} (N turns)" indicator in chat history when session is resumed rather than freshly created
- [x] 4.3 Verify `EnsureSession` flow works for passivated sessions — the session actor rehydrates from journal and accepts new `SendUserMessage` commands after resume

## 5. Spec Updates

- [x] 5.1 Sync delta specs to main specs via `/opsx-sync` after implementation is verified
