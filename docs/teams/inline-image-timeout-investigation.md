# Teams inline-image timeout correction

This focused correction starts at `e52499d0`, after merged PR #59.
This document records PR #60.
The [subsequent slow-download correction](slow-inline-image-downloads.md) updates the provisional-image deadline after the deployed container retest.
It retains the attachment and actor contracts from the Teams attachment change.
See [the attachment specification](../../openspec/changes/complete-teams-groupchat-and-attachments/specs/microsoft-teams-channel/spec.md)
and [the engineering glossary](../spec/GLOSSARY.md).

## Timeout ownership before the correction

The source inspection found four nested Ask and download deadlines.

| Owner | Original deadline | Source |
|---|---:|---|
| `TeamsIngressActorHost.SubmitAsync` | 2 seconds | `TeamsIngressActorHost.cs` |
| `TeamsActorConversationIngressSink` | 10 seconds | `TeamsPersonalRoutingActors.cs` |
| Channel conversation actor | 10 seconds | `TeamsChannelConversationActor.cs` |
| Personal and GroupChat conversation actor | 10 seconds | `TeamsPersonalRoutingActors.cs` |
| Attachment download | 10 seconds | Argument from `ProcessInboundAttachmentsAsync` |
| Subsequent content verification | Separate 10 seconds | Same operation argument |

The binding processes attachments in sequence.
It also waits for prompt classification, rejection delivery, pipeline initialization, and the input queue write.
The binding replies `Accepted` after this work completes.
An attachment-only rejection completes before that reply.

Netclaw passes the SDK callback token unchanged through its actors.
The SDK creates that token with a dedicated five-minute activity deadline.
It does not link that token to the HTTP disconnect token.
The HTTP token applies to initial request deserialization.
The pinned SDK source establishes this distinction:
[BotApplication.ProcessAsync](https://github.com/microsoft/teams.net/blob/5e45b035e2c6205667582e4c28ebc06069c5fa15/core/src/Microsoft.Teams.Core/BotApplication.cs#L186).

An Ask timeout expires the temporary reply actor.
It does not cancel the token inside the actor message.
Thus, the two-second host Ask explains the early HTTP completion.
The three ten-second deadlines explain the subsequent cancellation and late replies.

## Corrected Teams deadlines

`TeamsIngressTimeouts` owns the Teams route budget calculation.
No operator configuration property changes.

| Work or route | New deadline |
|---|---:|
| Download for one file | 30 seconds |
| Verification for one file | Separate 30 seconds |
| Binding Ask | `attachment_count * 60 seconds + 10 seconds` |
| Conversation route Ask | Binding deadline + 5 seconds |
| Host ingress Ask | Conversation deadline + 5 seconds |

One attachment receives route deadlines of 70, 75, and 80 seconds.
The extra ten seconds covers normal work beyond the attachment stages.
Approval operations retain their separate existing deadlines.

These budgets cover an available route, not arbitrary mailbox backlog or an indefinitely stalled downstream component.
The SDK five-minute activity token remains an independent upper limit.
Large batches can reach that limit before all per-file budgets expire.
Such cancellation must remain distinct from a file download deadline.

The binding retains its terminal acknowledgement contract:

```text
SDK authentication and translation
  -> ingress duplicate check
  -> conversation and binding ACL checks
  -> durable activity reservation
  -> attachment download and verification
  -> pipeline input queue write, or completed attachment-only rejection
  -> binding Accepted
  -> conversation result
  -> ingress result
  -> host result
```

This flow is schematic; it omits individual policy and classification steps.
The durable reservation stores an activity fingerprint, not a replayable activity payload.
A dispatch failure releases that reservation so a later request can retry.
Early acknowledgement would populate the ingress duplicate cache before dispatch succeeds.
That cache could then suppress a valid retry.
Therefore, this correction does not acknowledge before attachment work or detach work from an actor.

## Cancellation and safe diagnostics

The provisional inline-image path owns separate download-deadline and outer tokens.
A cancellation exception alone does not prove a deadline expired.
The path emits these reason codes:

- `download-deadline`: the download token expired without outer cancellation.
- `ingress-cancelled`: the outer token requested cancellation; this result also wins when both tokens fire.
- `download-http-error`: the HTTP operation failed, including HTTP 401.
- `download-failed`: another failure occurred, including an unrelated cancellation exception.

Outer cancellation propagates to the binding so it can release the durable reservation.
The download log includes only reason, host class, authentication state, elapsed milliseconds, configured deadline, outer cancellation state, and stage.
Stages identify token acquisition, request/response-header wait, response-header rejection, or body transfer.
The SDK downloader replaces external exceptions with bounded diagnostic facts.
It retains no original exception that could expose a URL or token.
The diagnostic exception remains local to the downloader call; it is not an actor message or persistence record.

The named `teams-attachments` client disables its default URL-bearing HTTP logs.
The client uses the explicit ingress deadline instead of the default 100-second `HttpClient.Timeout`.
Its HTTP timeout is therefore infinite; the caller still supplies the bounded operation token.
See [the HTTP timeout contract](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.timeout?view=net-10.0).
The Teams response handler observes headers without changing the shared streaming downloader.

## Transport inspection and owner retest

The named client retains the dedicated `SocketsHttpHandler` and disables redirects.
The request retains the framework defaults: HTTP/1.1 with `RequestVersionOrLower`.
The handler retains proxy inheritance, connection pooling, and an infinite connection timeout.
See [the connection timeout contract](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connecttimeout?view=net-10.0).
The factory retains its default two-minute handler lifetime.
The connection pool retains its default one-minute idle timeout and infinite pooled-connection lifetime.
The download token bounds the complete token/request/body operation.
No evidence currently justifies a protocol, proxy, pool, or `BotAcsId` change.

Bot Connector hosts retain the existing app-token scope, `https://api.botframework.com/.default`.
SharePoint, OneDrive, and other accepted signed URLs receive no Bot Framework bearer token.
No Graph permission changes.

After deployment, the owner must repeat text-plus-PNG and image-only input in an established Thread.
Confirm one model turn, concrete MIME verification, and no late route-result reply during a valid slow download.
If the request still stalls for 30 seconds, retain only the safe diagnostic fields.
A `stage=request` deadline means no response headers arrived.
Investigate the named Teams transport next; do not change the generic downloader or add `BotAcsId` without endpoint evidence.

Automated tests use gated operations and virtual time.
Virtual time drives the attachment deadline, but it does not advance Akka Ask timers.
Explicit budget assertions complement the complete host-to-binding route tests and their dead-letter observations.
These tests do not establish live tenant success or prove that every queue delay fits a finite deadline.

## Local validation

- Focused Teams tests: 358 passed, zero failures, zero skips.
- Full Release solution tests: 8,530 passed, zero failures, 17 platform or opt-in skips.
- Full Release solution build: zero errors and one demo-project `ASPIRE010` warning.
- Slopwatch: zero issues.
- Copyright headers, BOM, whitespace, and the Teams file-boundary check passed.

Commands:

```bash
dotnet test src/Netclaw.Daemon.Tests/Netclaw.Daemon.Tests.csproj -c Release --no-restore -m:1 --filter 'FullyQualifiedName~Teams'
dotnet build Netclaw.slnx -c Release --no-restore -m:1
dotnet test Netclaw.slnx -c Release --no-build --no-restore -m:1 --blame-hang-timeout 300s --blame-hang-dump-type mini
dotnet slopwatch analyze
pwsh -NoProfile -File scripts/Add-FileHeaders.ps1 -Verify
bash scripts/check-no-bom.sh
git diff --check
git diff --name-only dev...HEAD
```
