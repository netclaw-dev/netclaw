# Microsoft Teams channel integration

This runbook describes how to package, configure, deploy, and operate the
Netclaw Microsoft Teams channel.

## Security boundary

The Teams integration is disabled by default. It accepts only explicit tenant,
team, channel, and user identities.

The app package grants only these bot scopes:

- `personal`
- `team`
- `groupchat`

The package requests these app-scoped RSC permissions:

- `ChannelMessage.Read.Group`
- `ChatMessage.Read.Chat`

This permission is required for Teams to deliver an unmentioned channel reply
to the bot. The team owner consents to it during app installation or upgrade.
It delivers standard channel messages from that installed team to the bot
endpoint. Netclaw retains `MentionOnly=true` as its model-dispatch policy: it
admits an unmentioned message only when its canonical root was established by a
genuine bot mention from the same approved human. New roots, unknown roots, and
other senders are ignored before a session or model turn.

The package supports personal chats, standard team channels, and approved group
chats. It enables Teams file support for a bounded attachment pipeline.

`AllowAttachments` defaults to `false`. When enabled, the Teams adapter treats
a bounded content-URL `image/*` value as an image candidate. The adapter
downloads the bytes through the Teams trust gate. It detects a concrete MIME
type and creates a safe filename before the shared scanner accepts the file.
The generic pre-download gate does not classify `image/*` as Image. PNG, JPEG,
GIF, and WebP can reach a model with image support. Other verified image
formats remain path-only. A verified non-image remains rejected in Public
channels. It accepts personal file cards. It defers normal channel and
group-chat files.

The adapter uses the existing Bot Framework app credential for a Bot Connector
attachment URL. It uses the `https://api.botframework.com/.default` app scope.
This applies only to `smba.trafficmanager.net` and `.botframework.com` hosts.
SharePoint, OneDrive, and other trusted signed URLs do not receive that bearer
token. The adapter does not log or persist the token.

The package does not request message-write, `Chat.Read.All`,
`ChatMessage.Read.All`, `Files.Read.All`, or `Sites.Read.All`. It does not
enable private or shared channels, meetings, tabs, calling, or video.

## Prerequisites

Prepare these items before you start:

- A Microsoft Entra tenant with permission to register an application.
- Permission to create an Azure Bot resource.
- A public HTTPS endpoint for the Netclaw daemon.
- Public HTTPS privacy and terms pages for your organization.
- Canonical tenant, team, channel, and user IDs from an authenticated source.

Do not copy canonical IDs from unauthenticated messages or log text. Use an
authenticated tenant directory or an approved tenant administration process.

Configure a supported non-local exposure mode before registration. A reverse
proxy needs a non-loopback daemon address and explicit `Daemon.TrustedProxies`.

## Register the app and bot

1. Create a single-tenant Microsoft Entra application.
2. Record its application ID and tenant ID.
3. Create one client secret with the shortest practical expiry.
4. Remove the default Microsoft Graph `User.Read` permission if it exists.
5. Create a single-tenant Azure Bot that uses the Entra application.
6. Enter the application ID and tenant ID for the bot identity.
7. Enable the Microsoft Teams channel on the Azure Bot resource.
8. Set the bot messaging endpoint to `https://<public-host>/api/messages`.

Azure Bot supports one messaging endpoint. Use the same endpoint for personal
and team messages.

Use the Entra application **(client) ID** for the Azure Bot, package `AppId`,
`ClientId`, and `BotId`; all four values must represent the same application.
Do not use the Entra object ID or the Teams `28:` bot identifier. Increment the
package version for every manifest update, then upgrade or reinstall that
package in the exact target Team so its owner can grant the requested RSC
permission. Copy the client secret value, not the secret ID.

Do not add permissions beyond the package's required RSC entries. Do not
enable calling or meeting features.

## Build the Teams package

Run the package script from the Netclaw repository root:

```powershell
$BuildPackage = @{
    AppId = '00000000-0000-0000-0000-000000000000'
    DeveloperName = 'Example Operator'
    PrivacyUrl = 'https://example.com/privacy'
    TermsOfUseUrl = 'https://example.com/terms'
    OutputPath = './artifacts/netclaw-teams.zip'
    Version = '1.0.0'
    Verbose = $true
}

./deploy/teams/build-package.ps1 @BuildPackage
```

The script requires absolute HTTPS policy URLs. It creates a ZIP file with
`manifest.json`, `color.png`, and `outline.png` at the package root.

Do not commit the generated package. Review its manifest before each upload.
The developer name has a 32-character limit. Increase the version for each
package update.

## Configure Netclaw

Put non-secret settings in `~/.netclaw/config/netclaw.json`:

```json
{
  "Teams": {
    "Enabled": true,
    "TenantId": "<tenant-id>",
    "ClientId": "<entra-application-id>",
    "BotId": "<entra-application-id>",
    "AuthenticationMode": "ClientSecret",
    "AllowDirectMessages": false,
    "AllowGroupChats": false,
    "AllowAttachments": false,
    "MentionOnly": true,
    "AllowedTeamIds": ["<canonical-team-id>"],
    "AllowedChannelIds": ["<canonical-channel-id>"],
    "AllowedGroupChatIds": ["<canonical-group-chat-id>"],
    "AllowedUserIds": ["<canonical-user-id>"],
    "ChannelAudienceOverrides": [
      {
        "TeamId": "<canonical-team-id>",
        "ChannelId": "<canonical-channel-id>",
        "Audience": "team"
      }
    ]
  }
}
```

`BotId` is the bare Microsoft application ID. Do not include the Teams `28:`
prefix.

Set the secret through the Netclaw secret store:

```text
netclaw secrets set Teams.ClientSecret <client-secret>
```

Do not put `ClientSecret` in `netclaw.json`. Do not paste the secret into logs,
issues, chat messages, or source control.

An empty team or channel allow-list rejects channel traffic. An empty user
allow-list accepts any sender in an allowed channel.

Group chats require `AllowGroupChats: true`, an exact tenant match, and an
exact canonical group-chat ID. They require a global allowed user or verified
global allowed-group member. Channel overrides never authorize group chats.
An empty global principal list rejects group-chat traffic.

To enable Group Chat ingress without a file editor:

1. Open `netclaw config` > `Channels` > `Microsoft Teams`.
2. Select `Group Chat ingress: OFF` on the main Teams menu.
3. Press Space to select `Enable Group Chat ingress`.
4. Press Enter to apply the change.

The control is also available when Teams is disabled. It does not need a Graph
lookup. Esc discards changes. A failed save keeps the review page open for a
retry and leaves the persisted setting unchanged.

The menu shows `Group Chat ingress: ON` after the save. Add a chat through
`Add a channel or Group Chat`, then configure an allowed global user or group.
The chat picker does not enable ingress. Existing configurations with
`AllowGroupChats: false` remain disabled until an operator enables them.
To disable ingress, open the same row, clear the checkbox, and press Enter.
Netclaw preserves saved chat IDs when you disable ingress.

The daemon watches its configuration file. A valid save triggers an automatic
coordinated daemon restart. Wait for the daemon to become ready before a new
test message. A manual container restart is not part of this TUI flow.

With `MentionOnly: true`, every group-chat message needs a structured bot
mention. A prior group-chat message does not create a broad continuation rule.

Use `Manage channels and permissions` in `netclaw config` to inspect saved
channels and Group Chats. Use `Add a channel or Group Chat` for the picker or
the advanced canonical-ID path. Display names are labels only. Copy each ID
from an authenticated source.

The management screens show global and exact channel user/group grants.
Select a `Remove user` or `Remove group` row to remove an exact grant.
The TUI asks for confirmation before it removes an exact grant. It names the
channel and explains the sender rule that will apply after activation.
Netclaw saves the access-list change even when the Teams adapter is disabled.
Netclaw preserves its connection credentials in that case.

If the last global user or group is removed, an allowed channel without an
exact grant accepts any verified sender after configuration activation.
Personal and Group Chat ingress deny until a global user or group exists.

Attachments remain disabled until `AllowAttachments` is true. Netclaw stages
each accepted file, checks its size and content, then removes unsafe input.
It never accepts a normal channel or group-chat file without safe access.

Personal chats require `AllowDirectMessages: true` and an exact
`AllowedUserIds` match. Production configurations must list approved user IDs.

`MentionOnly` defaults to `true`. Keep it enabled. With the package RSC
permission, it still ignores every unmentioned new or unknown root and permits
only the same approved human's continuation of a root they established with a
genuine bot mention.

Use `ChannelAudienceOverrides` for canonical IDs that contain configuration
delimiters. An exact team and channel entry takes precedence over a team entry.

## Directory discovery and group authorization

The same Entra application that hosts the Teams bot can be used for directory
lookup. Netclaw uses an application `ClientSecretCredential` with the
`https://graph.microsoft.com/.default` scope. It uses `TenantId`, `ClientId`,
and `ClientSecret` for app-only Graph access. `BotId` configures Teams
transport. It does not authenticate Graph access. Netclaw never stores Graph
access tokens and it does not create a second Graph secret.

An Entra administrator must grant admin consent for exactly these Microsoft
Graph **application** permissions:

- `Team.ReadBasic.All` — Team display metadata used for discovery.
- `Channel.ReadBasic.All` — channel names and descriptions.
- `User.Read.All` — user display name, UPN, and mail metadata.
- `GroupMember.Read.All` — security/Microsoft 365 group discovery and checked
  group membership.
- `Chat.ReadBasic.All` — optional Group Chat metadata discovery.

Do not add `Directory.Read.All` for this feature. The Teams package RSC
permission described above is separate from these Graph application
permissions.

`Chat.ReadBasic.All` is optional. It grants tenant-wide application access to
basic chat metadata and requires administrator consent. The permission does
not prove that the Netclaw app is installed in a selected chat. Without this
permission, Group Chat ingress and local removal still use configured
canonical IDs.

To find a named chat, select `Add a channel or Group Chat` > `Group Chat`.
Enter part or all of its title in `Group Chat name`, then press Enter.
The search matches Group Chat titles without case sensitivity. It does not
search user names or require a participant selection.

Microsoft Graph has no tenant-wide chat-title search endpoint for application
credentials. Netclaw reads tenant user IDs with `User.Read.All`, then reads
their chat metadata in bounded batches. It matches titles locally and removes
duplicate chat IDs. This process does not read chat messages or change access
lists.

The search continues automatically and retains matches as new batches arrive.
Netclaw paces consecutive batches to reduce Graph throttles.
It visits other users before it exhausts one user's chat pages. Title search
does not request participant expansion. The status shows cumulative user,
chat-record, request, and match counts. The user count advances when that
user's chat pages finish; other counts can advance before it changes.
Chat records can include duplicates shared by several users. The match list
contains each canonical Group Chat ID once.

Select a match to review it, or use `Stop search` / `Ctrl+S` to pause.
The run pauses at twenty batches or two minutes if metadata remains.
Select `Resume search` to use the last successful checkpoint. A stopped or
failed batch can repeat when resumed. Checkpoints expire after five minutes;
an expired checkpoint requires a fresh search.

An incomplete scan or Graph error is not proof that a chat does not exist.
The TUI reports failures and unavailable sources explicitly. Use the advanced
canonical-ID path if a chat has no title or discovery is unavailable. A
selected chat still needs `Group Chat ingress: ON` and principal grants.

Netclaw does not request `Chat.ReadBasic.WhereInstalled`, `Chat.Read.All`, or
chat message-read permissions for this picker. Group-chat authorization uses
configured canonical IDs. The directory cache has a bounded lifetime.

`netclaw config` now lists **Microsoft Teams** after Mattermost. The secure
connection flow captures Tenant ID, application/client ID, Bot ID, and a
masked client secret. The menu shows `Configure Teams connection` until the
connection is complete. It then shows `Connection & credentials`. Existing
secrets render as `configured`; blank secret input preserves them. The
configuration UI searches Teams, channels, users, and groups through a bounded
shared directory boundary. The advanced/manual path accepts canonical Teams
and Entra object IDs directly, so an existing valid configuration remains
manageable when discovery is unavailable.

Names, UPNs, and mail addresses are presentation metadata only. Channel lists
can show `Team name / Channel name` after a directory lookup. Netclaw persists
and compares canonical IDs only. Directory labels are cache data. For example:

```json
{
  "Teams": {
    "AllowedGroupIds": ["<group_allow_teams_netclaw-object-id>"],
    "ChannelAccessOverrides": [
      {
        "TeamId": "<team-id>",
        "ChannelId": "<ai-testing-channel-id>",
        "AllowedUserIds": ["<operator-object-id>"],
        "AllowedGroupIds": ["<ai-testing-operators-group-id>"]
      }
    ]
  }
}
```

A verified member of `group_allow_teams_netclaw` may interact in an otherwise
authorized Teams channel. The explicit user in the example gains access only
to `AI-Testing` (plus any global permission); a group match never bypasses the
tenant, Team, channel, root, audience, or mention checks.

For an allowed channel, global and matching channel-specific user/group rules
are unioned. If none are configured, legacy allowed-channel behavior remains
unchanged. Once any rule exists, a sender must match one. DMs always remain
fail-closed: `AllowDirectMessages` must be true and the sender must match a
global user ID or verified global group membership. Channel-specific rules do
not grant DM access.

Group membership uses Graph `checkMemberGroups` against the authenticated
sender object ID. Requests are deduplicated, split into batches of at most 20
groups, and stop after a positive result. An explicit allowed user bypasses
Graph completely. A timeout, 401/403, exhausted/throttled 429, unavailable
service, or malformed response denies group-derived access with a safe reason;
it never weakens authorization.

Directory calls use a bounded in-memory cache. User profiles and membership
evidence last 10 minutes. Team, channel, and group lookup values last 30
minutes. User search values last 5 minutes. Cache keys hash canonical
tenant/resource data and contain neither secrets nor raw typed search text.
Approval card callbacks do not issue a new Graph request just to enrich a
display label: they use the Teams callback name, an already-cached profile, or
`Authorized operator`.

The status screen marks `Chat.ReadBasic.All` as optional. Without it, Netclaw
still permits manual Group Chat IDs and local removal. It cannot show Group
Chat topics or participant preview data.

`netclaw doctor` reports offline, non-secret Teams configuration diagnostics
and lists the consent required for configured group authorization. It never
prints or probes client secrets, access tokens, authorization headers, or full
tenant responses.

## Phase 1.1 runtime modernization

The Teams transport uses the stable Microsoft Teams SDK for .NET 2.1 native
ASP.NET Core host. The public contract remains one authenticated
`POST /api/messages` endpoint with the existing body ceiling and rate limit.
Netclaw translates native typed SDK activities at that edge before actor and
session routing; Microsoft SDK types do not enter Netclaw channel contracts.

Existing `Teams` configuration and secret ownership remain unchanged. SDK 2.1
uses its native compatibility mapping when `AzureAd:ClientId` is absent: it
maps `Teams.ClientId`, `Teams.TenantId`, and secret-backed
`Teams.ClientSecret` into its in-memory `AzureAd` client-credential model.
Netclaw rejects enabled Teams configuration that also sets `AzureAd:ClientId`.
Do not add an `AzureAd` configuration block or duplicate the client secret.

The migration preserves personal, Posts, Threads, ACL, mention, attachment,
proactive delivery, typing, and Adaptive Card approval semantics. The named
`teams-sdk` policy selects `AzureAd` only for `/api/messages`; generic daemon
endpoints retain the upstream `AuthSelector` behavior. Native SDK telemetry
source subscription is deferred. Existing `ChannelTelemetry` remains in use,
and Phase 1.1 adds no global ASP.NET Core instrumentation or a second telemetry
pipeline.

Before deploying a Phase 1.1 build, run the controlled Personal, Posts,
Threads, approval, typing, and duplicate-approval smoke matrix in the live
handover record. Do not treat this documentation update as live validation.

## Validate a development tunnel

Use a development tunnel only for a bounded test period.

1. Start the daemon with an isolated owner-only Netclaw home.
2. Start the approved HTTPS development tunnel.
3. Set the Azure Bot messaging endpoint to the tunnel `/api/messages` URL.
4. Run `netclaw doctor`.
5. Run `netclaw status`.
6. Stop the daemon and tunnel after the test.
7. Restore the production endpoint before you leave the test environment.

Never publish a tunnel URL in a log, document, commit, or test fixture.

## Sideload the package

1. Open **Apps** in Microsoft Teams.
2. Open **Manage your apps**.
3. Select **Upload an app**.
4. Select **Upload a custom app**.
5. Upload the generated ZIP file.
6. Upgrade or reinstall the app in the approved team.
7. Have the team owner approve both required RSC requests.

Your tenant policy can disable custom app upload. Ask a Teams administrator to
approve or upload the package when required.

## Publish for production

1. Complete the privacy, legal, security, and ownership review.
2. Build a package with the production app ID and policy URLs.
3. Confirm that the manifest requests `personal`, `team`, and `groupchat` bot
   scopes with both required RSC permissions.
4. Submit the package through the Teams admin center.
5. Ask a Teams administrator to approve and publish the app.
6. Install the app only in approved teams and accounts.

Do not use a development tunnel as the production endpoint. Use a stable HTTPS
host with normal certificate, monitoring, and recovery controls.

## Tool approvals

Teams sends a native Adaptive Card for a tool approval. The card preserves the
order and labels that the session supplies. All approval cards target Adaptive
Cards schema 1.5.

Each card has a Fluent icon, a large state title, and the `NETCLAW SECURITY
CONTROL` subtitle. A semantic banner describes the state. A two-column table
shows bounded display facts. Terminal cards have a centered status footer.

| Card state | Icon | Card tone |
| --- | --- | --- |
| Pending | ShieldLock | accent |
| Granted | ShieldCheckmark | good |
| Denied | ShieldDismiss | attention |
| Card expired | ClockDismiss | warning |
| Already processed or unavailable | Info or Warning | neutral |

| Decision | Card style | Effect |
| --- | --- | --- |
| Once | positive | Allows the current call only. |
| This chat | default | Allows the scoped action for this session. |
| Always here | default | Saves a directory-scoped grant. |
| Always anywhere | default | Saves a global grant. |
| Deny | destructive | Refuses the current call. |

The Teams client can wrap the action row on a narrow display. This does not
change the action order or semantics. Each button sends an authenticated
`Action.Execute` callback. Netclaw validates the sender, tenant, conversation,
nonce, expiry, and persisted offered key before it accepts a decision.

Teams approval cards do not accept letter replies. Use a card button. This
keeps the signed card callback as the only decision path.

A granted card means that the session accepted a decision. It shows the exact
approval scope and `Pending execution`. It does not claim that the operation
finished. A denied card appears only after the user selects the explicit Deny
option. It shows `User rejected the request`.

When an approval reaches a terminal presentation state, Teams returns the
terminal Adaptive Card in the `Action.Execute` response. The Teams client
replaces the source pending card in place. Netclaw does not post a second
Granted, Denied, Already Processed, or Unavailable card.

An expired card is a Teams presentation event. When the requester submits an
expired pending card, its `Action.Execute` response replaces that source card
in place with the actionless Expired presentation. No decision was recorded.
The session approval remains pending, so Netclaw separately posts one new
pending card with a fresh nonce. Expiry creates no core decision.

The elevated visual exists for a future canonical risk signal. Teams does not
parse commands or infer risk. Normal cards never show a fabricated `SAFE` or
`HIGH` risk level.

### Channel-binding parity

Before a validated Teams activity enters a session, Netclaw applies the shared
prompt-injection classifier. A high-risk result is blocked and an unavailable
classifier fails closed. Teams does not fetch Graph message history, so it does
not perform channel-history hydration or backfill; that capability remains
unavailable until it has an authenticated, ordered, bounded history source.

Ordinary text and error output use the shared channel output lifecycle and
normal Teams delivery uses the shared safe transport failure path. Native
typing, proactive delivery, and Adaptive Cards remain Teams transport concerns.

An expired card does not send an implicit Deny to the session. Netclaw replaces
the opaque nonce binding with a fresh card while preserving the pending
session-owned approval. The old card cannot authorize an action; the replacement
card may be selected once by the original approved requester.

If presentation delivery fails, Teams records only bounded recovery state and
invalidates that attempted nonce binding. It does not persist the raw nonce,
deny or consume the session approval, or self-retry in a tight loop. A later
recovery, including actor restart, creates and delivers a fresh card. A Teams
delivery that succeeds without an activity ID is still a successful unbound
presentation; card action validation continues to use the nonce, sender,
tenant, offered option, and expiry checks.

Teams uses the generic approval system only after the normal session pipeline
has found an operation eligible. The adapter does not expand core tool
eligibility: host shell remains subject to Netclaw's existing Personal-only
security boundary. Neither a Team allow-list, approval override, card action,
nor persistent approval grant can make `shell_execute` available to Teams.

Persistent approvals remain in `~/.netclaw/config/tool-approvals.json` and are
managed exclusively by the core approval store. Teams does not write policy or
grants.

## Health checks

Run these checks after each deployment or secret rotation:

```text
netclaw doctor
netclaw status
curl http://127.0.0.1:5199/api/health/ready
```

Teams always reports degraded with a configured-but-unvalidated message in this
release. The readiness endpoint proves only daemon liveness.

Confirm that an approved message gets a reply under its original root. A
request without valid Bot Framework authentication cannot reach model dispatch.

After a package upgrade that adds RSC, first send one genuine-mentioned root.
Then send one unmentioned reply from the same approved human in that root. It
must continue the same session and reply in the same root. Confirm separately
that an unmentioned new root and an unmentioned reply from another human do not
create a session or model turn.

Run `netclaw status` after the smoke. Confirm that the Teams `recv`, `routed`,
and `replied` counters increase.

Check structured daemon logs for the stage and outcome. Do not enable payload
logging or retain tenant identifiers as evidence.

Record each live result with only counters and structural facts. Do not record
message bodies, activity payloads, identifiers, URLs, headers, tokens, or
secrets. See [live validation evidence](../teams/live-validation-evidence.md)
for the current standard Posts and Threads root results and open follow-ups.

## Group Chat and attachment owner smoke

This change is not live validated. Upgrade the Teams package before this smoke.
The package upgrade must include both RSC permissions and the `groupchat` scope.

1. Enable one canonical Group Chat ID and one approved global user.
2. Send a genuine structured bot mention in that group chat.
3. Confirm one Team-audience session and one scoped reply.
4. Send an unmentioned group-chat message and confirm no model turn occurs.
5. Send a mention from another approved user and confirm the same session uses their sender identity.
6. With attachments enabled, send one PNG, JPEG, GIF, or WebP image.
7. Confirm the image reaches the configured attachment policy without URL logging.
8. In a personal chat, send one approved file-download card.
9. Confirm it stages through the scanner and uses only a safe attachment projection.
10. Send an ordinary channel or Group Chat file.
11. Confirm Netclaw reports a safe deferral and creates no model turn for that file.

Stop at the first failed gate. Record only counters, scopes, and pass or fail.

## Rotate the client secret

1. Create a new Entra client secret.
2. Store it with `netclaw secrets set Teams.ClientSecret <new-secret>`.
3. Restart the Netclaw daemon.
4. Run the health checks.
5. Run one approved message smoke when your change policy requires it.
6. Revoke the old secret after the new secret works.

If validation fails, restore the old secret before you revoke it. Never print
either secret during diagnosis.

## Troubleshooting

- A disconnected connector usually indicates invalid credentials or an
  unreachable Bot Framework service.
- An unmentioned new or unknown channel root is ignored when `MentionOnly` is
  `true`. An unmentioned continuation is admitted only for the approved human
  who established that root with a genuine bot mention.
- A channel identity outside either allow-list is rejected before dispatch.
- A user outside `AllowedUserIds` is rejected before dispatch.
- An unmapped channel uses the `public` audience and cannot use restricted
  tools.
- An ordinary channel or Group Chat file is deferred without a broad Graph
  permission fallback.
- A client secret in `netclaw.json` fails the supported configuration model.

## Rollback

Before the first deployment of this change, take and verify a backup or
snapshot of the Teams persistence store. The current binary can read existing
Teams records, but it may write `teams-approval-reissued-v2` and
`teams-approval-forwarding-v2`, which the previous binary cannot read.

Rollback to the previous binary is straightforward only before either
incompatible manifest has been written. Afterwards, use a forward fix or stop
the daemon and restore the verified pre-deployment Teams persistence snapshot
before starting the previous binary. Do not run the previous binary against a
store containing the new manifests.

To stop Teams ingress without changing the binary:

1. Set `Teams.Enabled` to `false` in `netclaw.json`.
2. Restart the Netclaw daemon.
3. Confirm that the Teams connector is absent or disabled in `netclaw status`.
4. Withdraw or block the app in the Teams admin center.
5. Revoke the client secret when the rollback is permanent.

## Microsoft references

- [Register an Entra application](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app)
- [Configure Azure Bot authentication](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/how-to/authentication/add-authentication)
- [Enable RSC channel-message delivery](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/how-to/conversations/channel-messages-for-bots-and-agents)
- [Upload a custom Teams app](https://learn.microsoft.com/en-us/microsoftteams/platform/concepts/deploy-and-publish/apps-upload)
- [Publish a Teams app](https://learn.microsoft.com/en-us/microsoftteams/platform/concepts/deploy-and-publish/apps-publish-overview)
