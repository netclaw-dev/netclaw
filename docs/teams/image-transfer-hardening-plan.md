# Teams image transfer: investigation and repair plan

## Status and scope

The September 6 correction follows in [image transfer progress and recovery](image-transfer-progress-and-recovery.md).

This document records the September 5, 2026 investigation and the proposed repair sequence.
It starts from `dev` commit `73f55025`, which includes PR #62.
This PR changes documentation only.
It does not claim a runtime correction or a successful deployment retest.

The operator confirms that the personal-chat tool-name error is resolved.
The remaining failure occurs during Teams attachment retrieval.
The scope covers the Teams SDK adapter, Teams ingress and route actors, their tests, and Teams documentation.
Shared download, security, model-provider, session-history, and other channel code remain outside this scope.

The authorities are [PRD-009](../prd/PRD-009-input-adapters-and-unified-input.md) and
the [Teams attachment contract](../../openspec/changes/complete-teams-groupchat-and-attachments/specs/microsoft-teams-channel/spec.md).
See also [the engineering glossary](../spec/GLOSSARY.md) and
the [earlier timeout investigation](inline-image-timeout-investigation.md).
This targeted defect plan introduces no new product capability or policy boundary.

## Evidence

The operator supplied daemon logs and screenshots from `netclaw-proxicon-dev`.
The table uses the log timestamps, not the screenshot clock.
Tenant, conversation, activity, resource, and credential values are excluded.

| Attempt | Evidence | Interpretation |
|---|---|---|
| Personal building image | `16:50:12.312`: accepted PNG, 376,953 bytes, `inlined=True` | The verified image reached the Teams input path. |
| Same request | HTTP POST completed in 27,209.4930 ms; the screenshot shows the correct building answer. | This request succeeded. The HTTP duration includes work beyond the download. |
| Personal animal image | `16:51:58.371`: `download-deadline`, `elapsed_ms=30004.504`, `configured_deadline_ms=30000` | The download exceeded its 30-second budget. |
| Personal repeat | `16:52:49.479`: `download-deadline`, `elapsed_ms=30002.5946`, `configured_deadline_ms=30000` | A second download reached the same budget. |
| Both logged failures | `host_class=bot_connector`, `authenticated=True`, `outer_cancellation_requested=False`, `stage=body` | The adapter attached credentials and received successful response headers before the deadline. |
| Channel thread | The screenshot shows an attachment timeout followed by a request for an image. | The supplied excerpt does not establish this request's stage or duration. |

`stage=body` covers response reads and local file writes.
It does not prove that any body bytes arrived.
The current diagnostics cannot distinguish a stalled network read from a slow local file write.
They also cannot prove that either failed download would finish within 60 seconds.

The two logged failures occur before verified image content reaches the model.
They do not establish a model-provider failure or a personal actor defect.
The successful image request confirms that the complete path can work.

## Existing correction must precede the next diagnosis

At this investigation, [PR #61](https://github.com/Proxicon/netclaw/pull/61) remains open and unmerged.
Its head is `39338680abd42231ab889626d16888e1ba0b682b`.
Current `dev` includes PR #60 and PR #62, but excludes PR #61.
The supplied logs agree with the 30-second limit in current `dev`.
They therefore do not test the 60-second correction.

PR #61 already proposes these Teams-only changes:

- Give provisional `image/*` downloads 60 seconds for token acquisition, headers, and body transfer together.
- Keep a separate 30-second content-verification limit.
- Allocate 90 seconds per attachment in the Teams route budget, plus the existing route margins.
- Record `attachment_download_completed` with elapsed time, configured deadline, and byte count before verification.
- Test delayed body completion, deadline rejection, cancellation, duplicate suppression, and partial-file cleanup.

For one attachment, its binding, conversation, and host budgets are 100, 105, and 110 seconds.
The independent SDK activity limit remains five minutes.
Large batches and route delays can still exhaust that outer limit.
The correction does not guarantee that every remote transfer completes.

The owner should review PR #61, await its CI result, then merge and deploy it before the next live comparison.
Do not duplicate its code in this plan PR.
Record the deployed image digest and source revision to establish which correction the retest exercises.

## Repair sequence and component ownership

The existing flow remains the basis for the repair:

```text
Authenticated SDK activity
  -> Teams ingress duplicate check
  -> Teams conversation and binding access checks
  -> durable activity reservation
  -> bounded attachment download
  -> content verification and audience policy
  -> verified image plus safe text in ChannelInput
  -> existing pipeline queue
  -> terminal route acknowledgement
  -> existing model response delivery through Teams
```

This flow is schematic; it omits individual attachment policy checks and rejection branches.
The shared pipeline consumes `ChannelInput` with concrete verified MIME types and image bytes.
Teams must never substitute a download URL or rejected bytes for that representation.

### 1. Establish the corrected deployment baseline

Retest PR #61 with the sequence below before changing another timeout or transport policy.
Confirm `configured_deadline_ms=60000` on the provisional-image path.
Compare `attachment_download_completed` with the later `attachment_accepted` event.
A download-complete event alone does not establish content acceptance or model delivery.

If the deployment still reports 30,000 ms, resolve its revision mismatch before a new runtime diagnosis.
If repeated transfers succeed, retain the evidence and close this incident without speculative transport changes.

### 2. Add Teams-local transfer diagnostics if 60-second failures remain

`TeamsSdkAttachmentDownloader` owns HTTP phase observations.
Reuse its request-local `StageObserver` mechanism and the injected `TimeProvider`.
Extend the Teams response handler to observe body reads without replacing the shared download loop.
Keep any content or stream decorator inside the Teams adapter.

Record only bounded facts:

- Time spent before successful response headers.
- Total bytes read from the response stream.
- Elapsed time since the last successful body read, when one exists.
- Whether a body read remained pending when the operation failed.
- The existing stage, host class, authentication state, and cancellation owner.

Use explicit absence for observations that never occurred.
Do not report zero elapsed time as proof that a missing phase completed.
Use request-local state; never share counters between downloads or persist them in actor state.
Emit a terminal summary, not an event for every buffer.
Keep URLs, tokens, body data, filenames, raw exceptions, and new resource identifiers out of these diagnostics.

`TeamsProvisionalInlineImageIngress` owns the total deadline and the safe terminal attachment outcome.
It must retain separate download and verification budgets and preserve outer-cancellation precedence.
New diagnostics must preserve stream ownership, disposal, cancellation, and bounded memory use.
They must not drain or buffer the body outside the shared byte-limit checks.

These observations narrow the fault location; they do not prove a local-disk fault by exclusion.
Use the resulting evidence to choose any subsequent transport correction.
Do not add retries, increase limits again, or change connection pools without that evidence.

### 3. Prove repeat transfers through the Teams actor routes

Extend the existing Teams SDK and route tests with distinct activity IDs in one established conversation.
Exercise success, body deadline, another body deadline, and subsequent success in sequence.
Cover Personal and Channel conversations, including the Posts and Threads routes.
Also retain the existing GroupChat regression suite.

The Teams binding actor owns the durable reservation and the pipeline queue write.
The reservation contains a fingerprint, not a replayable activity payload.
Keep the terminal acknowledgement after dispatch or completed rejection.
Do not detach attachment work, acknowledge early, or reset the session to recover a failed transfer.

Prove these outcomes:

- A successful transfer delivers the expected verified bytes through one pipeline input.
- The next image uses its own capture and content; it cannot reuse the prior image.
- A duplicate activity creates no additional download or model input.
- A failed image leaves no partial file and cannot contaminate a later successful transfer.
- An image-only rejection creates no model input.
- A rejection with safe text preserves exactly one text input and excludes rejected image bytes.
- Outer cancellation releases the reservation through the existing actor path.
- A later valid activity succeeds without a conversation reset.
- Concurrent conversations do not share captures, progress state, image content, or cancellation state.

Use controlled streams, explicit completion signals, and virtual time for attachment deadlines.
Use actor acknowledgements for route completion.
Virtual attachment time does not advance Akka Ask timers; retain explicit route-budget assertions.

## Why a timeout also produces a model answer

The current Teams contract retains safe text when an attachment fails.
Thus, the model receives the question without the rejected image.
Its request for an image is a text-only answer, not evidence of a second image download.
This plan preserves that contract.
Any future change to that user-visible behavior needs a separate Teams contract decision and tests.

## Validation for a subsequent code PR

Run the focused Teams SDK and actor tests, the complete Teams regression suite, and the full Release build and tests.
Run Slopwatch, copyright-header verification, BOM checks, and `git diff --check`.
Review the changed-file list against the Teams scope before a commit.
Use a fake model boundary for deterministic automated proof; retain the live operator retest for upstream behavior.

This documentation PR requires link, evidence, BOM, and whitespace checks.
It does not rerun runtime tests or claim new automated runtime coverage.

## Operator retest and acceptance

Preserve the existing personal conversation and its history.
After deployment, record the source revision, image digest, and safe diagnostic events for each attempt.

1. Send a personal greeting and confirm a normal reply.
2. Send the building image with text, then the animal image with text twice.
3. Send the animal image without text in the same personal conversation.
4. Repeat the image sequence in an established channel thread and a fresh thread.
5. Check both Posts and Threads channel layouts where available.
6. Confirm each successful reply describes the current image.
7. Compare download completion, content acceptance, and the Teams reply for each successful attempt.
8. For each failure, retain the deadline, stage, elapsed time, and outer-cancellation flag.

The owner monitors CI, merges, deploys, and performs this retest.
A plan PR or green automated tests do not establish live transfer reliability.
If the corrected deployment still fails, attach its safe evidence before implementation of the diagnostic extension.
