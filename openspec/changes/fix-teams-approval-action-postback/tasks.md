## 1. Rejection attribution

- [x] 1.1 Separate the Teams approval validation gates and record one fixed safe reason code; verify the focused rejection-matrix tests cover every required code
- [x] 1.2 Keep trusted source-card, requester, route, nonce, offered-option, and retry checks intact; verify that mismatched callbacks do not submit a shared approval decision

## 2. Teams postback correction

- [x] 2.1 Trace and correct the proven SDK-shaped callback locator or identity boundary mismatch; verify a current-dev live-shaped invoke regression changes from Rejected to the authoritative terminal result
- [x] 2.2 Preserve Personal, Posts, and Threads routing and recovery isolation; verify approve and deny actions reach the matching pending approval for each scope
- [x] 2.3 Preserve terminal semantics; verify deny returns Denied, expiry returns Expired with one fresh nonce, and duplicate or stale callbacks return a neutral terminal response

## 3. Compatibility and verification

- [x] 3.1 Preserve the PR #46 serialized Teams card delivery regression; verify the existing SDK reply-client and serializer tests pass unchanged
- [x] 3.2 Run focused Teams, translator, personal-channel, and shared approval-flow tests; verify Slack, Discord, and Mattermost shared-flow suites also pass
- [x] 3.3 Run `dotnet build Netclaw.slnx`, the full solution tests, `dotnet slopwatch analyze`, strict OpenSpec validation, and file-header verification; record the results in the pull request
