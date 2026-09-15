# Teams image transfer progress and recovery

## Incident and comparison

This correction starts from `dev` commit `d922cd25` after PRs #61, #62, and #63.
It implements the next step in [the Teams transfer plan](image-transfer-hardening-plan.md).
The [Teams attachment contract](../../openspec/changes/complete-teams-groupchat-and-attachments/specs/microsoft-teams-channel/spec.md) remains the behavior authority.
See also [the engineering glossary](../spec/GLOSSARY.md).

The September 6 operator log shows a body-stage timeout at 60,001.7722 milliseconds.
The configured limit is 60,000 milliseconds, and the outer token did not request cancellation.
Read-only inspection of the same container adds these successful transfers:

| Bytes | Complete download duration |
|---:|---:|
| 693,102 | 40,611.0918 ms |
| 171,232 | 20,444.7812 ms |
| 237,073 | 21,868.6345 ms |
| 1,266,021 | 56,566.6825 ms |

These durations include token acquisition, HTTP headers, body reads, and local file writes.
The evidence confirms that the deployed 60-second correction still rejects some transfers.
It does not identify the cause of the low transfer rate or prove whether each failed body made progress.
The container reports no proxy environment variables.
No deployment or container configuration changed during this investigation.

Teams and Discord use the same `StreamingAttachmentDownloader.DownloadToFileAsync` method.
It streams into a temporary file with an 81,920-byte buffer and enforces the byte limit.
Discord uses a 10-second operation budget and a trusted attachment URL without the Teams bearer flow.
Teams inline images use the authenticated Bot Connector attachment endpoint.
Microsoft documents this [authenticated inline-image path](https://microsoft.github.io/teams-sdk/python/in-depth-guides/file-handling/receiving-inline-images/).
Discord's success therefore does not establish equivalent Teams endpoint performance.

The local defects are a fixed deadline without progress information and no recovery from a temporary body stall.
Sequential attachment work also accumulates per-file waits inside the SDK activity limit.
This correction addresses those defects without a claim that it repairs Microsoft's service or the network.

## Transfer behavior

`TeamsAttachmentTransfer` observes one HTTP attempt inside the Teams SDK adapter.
It wraps the response stream and retains the shared downloader's byte limit, file copy, and cleanup behavior.
Each pending asynchronous body read receives a 30-second idle deadline.
A positive read ends that deadline; the next read receives a new one.
This permits long transfers that continue to deliver bytes.

The Teams image path has a separate 240-second absolute download limit.
That limit includes token acquisition and both HTTP attempts.
It does not restart after headers, progress, or a retry.
Content verification retains its separate 30-second limit.

A body idle timeout permits one new GET of the same captured URL.
The first response closes, and the shared helper removes its partial file before the second GET.
The retry starts from byte zero; it does not combine data from separate responses.
The validated URL and bearer remain local to the same downloader call.
The retry never replays an actor message or registers another model turn.

Caller cancellation, authorization failure, HTTP failure, byte-limit rejection, and scanner rejection do not trigger this retry.
The adapter awaits the actual cancelled read before disposal or retry.
It never leaves a detached read behind.
The second idle timeout produces an explicit `download-body-idle` rejection.
An outer cancellation takes precedence over an idle timeout and follows the existing actor reservation-release path.

The image budget covers provisional `image/*`, concrete inline image declarations, and catalog-classified Personal file images.
Only the provisional MIME declaration receives normalization from detected bytes.
Concrete declarations and filenames still reach the shared scanner for validation.
Other file types retain the existing shared ingress path and operation budgets.

## Batch and actor behavior

The Teams binding processes at most three images concurrently within one activity.
It first processes other attachment types serially through the shared ingress path.
One 270-second batch deadline includes every download, verification, and queue wait inside attachment processing.
The existing file-count and per-file byte policies still apply.

| Boundary | Budget for an activity with attachments |
|---|---:|
| Complete attachment batch | 270 seconds |
| Binding route | 280 seconds |
| Conversation route | 285 seconds |
| Host route | 290 seconds |
| SDK activity token | Existing 300 seconds |

Activities without attachments retain their 10-, 15-, and 20-second route budgets.
The margins cover normal work outside attachment processing; they cannot guarantee arbitrary mailbox or transport delays.
The SDK activity token remains the independent outer limit.

Each image operation writes only its own result slot.
The binding assembles accepted results and rejection text in the original attachment order after the operations complete.
A batch-local gate serializes the synchronous inbox reservation and file move.
This preserves the shared writer's contract when attachments have equal filenames.
No actor state, persistence schema, or shared file writer changes.

A batch deadline cancels active operations and prevents queued operations from starting.
The binding retains completed accepted results and reports that some attachments timed out.
It sends only accepted content and the existing safe text to the normal pipeline.
An image-only batch with no accepted content produces no model input.
The terminal acknowledgement still follows the completed dispatch or rejection path.

## Safe diagnostics

The SDK adapter emits one `attachment_transfer` summary per HTTP attempt.
Its fields include outcome, attempt number, host class, authentication state, stage, received bytes, response length, and durations.
`headers_ms` measures time before response headers.
`last_read_age_ms` measures time since the last positive read and remains absent if no positive read occurred.
The outcome distinguishes completion, idle retry, exhausted idle retry, and other failure.

The summary contains no resource URL, token, body content, filename, raw exception, or new conversation identifier.
The existing `attachment_download_completed` and `attachment_accepted` events retain their separate meanings.
The former confirms transfer completion; the latter confirms verified content acceptance.
The batch emits an explicit `attachment_batch_rejected reason=batch-deadline` event when its own deadline expires.

## Automated proof and owner retest

The SDK tests verify a real PNG larger than two MiB across 80 virtual seconds with continued body progress.
They check a partial-body stall followed by success, two stalls, byte limits, cancellation, exact bytes, cleanup, and diagnostic redaction.
Actor tests cover the concurrency bound, ordered results, duplicate suppression, batch cancellation, and later activity recovery.
The Teams regression suite also covers attachment policy, concrete MIME checks, personal conversations, and channel routes.

After CI and deployment, retain the existing personal history and repeat the original image sequence.
Test one photo of several megabytes, then several photos in one activity within the configured file limits.
Repeat the sequence in a channel post and thread.
Confirm that each reply describes the current images and that accepted content remains in the original order.
If a failure remains, retain the safe transfer summary with the terminal ingress event.

This correction keeps finite limits for stalled services and large batches.
It does not promise that every transfer completes within four minutes or that every batch fits the SDK activity limit.
The owner reviews the draft PR, monitors CI, merges, deploys, and performs the live retest.

## Local validation

- Teams regression suite: 373 passed, no failures or skips.
- Full Release solution tests: 8,553 passed, no failures, and 17 existing platform or opt-in skips.
- Full Release build: no errors; one existing demo-project `ASPIRE010` warning.
- Slopwatch: no issues.
- Copyright headers, BOM checks, local document links, Teams scope, and whitespace checks pass.

Commands:

```bash
dotnet build Netclaw.slnx -c Release --no-restore -m:1
dotnet test src/Netclaw.Daemon.Tests/Netclaw.Daemon.Tests.csproj -c Release --no-build --no-restore -m:1 --filter 'FullyQualifiedName~Teams' --blame-hang-timeout 300s --blame-hang-dump-type mini
dotnet test Netclaw.slnx -c Release --no-build --no-restore -m:1 --blame-hang-timeout 300s --blame-hang-dump-type mini
dotnet slopwatch analyze
pwsh -NoProfile -File scripts/Add-FileHeaders.ps1 -Verify
bash scripts/check-no-bom.sh
git diff --check
```
