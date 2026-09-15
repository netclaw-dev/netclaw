# Teams slow inline-image downloads

This correction starts from `8378683a`, the `dev` merge of PR #60.
It follows [the timeout investigation](inline-image-timeout-investigation.md).
The [Teams attachment contract](../../openspec/changes/complete-teams-groupchat-and-attachments/specs/microsoft-teams-channel/spec.md) remains the behavior authority.
See also [the engineering glossary](../spec/GLOSSARY.md).

## Evidence from the deployed container

The operator supplied two daemon log excerpts from `netclaw-proxicon-dev` on September 5, 2026.
The excerpts contain these facts:

| Request | Evidence |
|---|---|
| Text plus image | `download-deadline`, `elapsed_ms=30010.3112`, `configured_deadline_ms=30000`, `stage=body` |
| Authentication | `host_class=bot_connector`, `authenticated=True` |
| Cancellation owner | `outer_cancellation_requested=False` |
| Image-only repeat | Verified PNG, 438,429 bytes, inline content accepted |
| Successful request duration | HTTP request completed in 27,531.7417 milliseconds |

The first download received successful HTTP headers before its 30-second deadline expired.
The body stage includes response reads and local file writes.
The log cannot identify which operation stalled or how much body data arrived.
The successful request duration includes more than the download itself.
It does not prove that the first request would complete within 60 seconds.

These observations support more time for the Teams inline-image download.
They do not indicate an authentication failure or a model image-capability failure.

## Focused correction

`TeamsIngressTimeouts` owns the fixed budgets.
The provisional `image/*` path uses 60 seconds for the complete download operation.
That limit includes token acquisition, response headers, and the body transfer.
It does not restart when headers arrive.
Content verification retains a separate 30-second limit.
Other attachment shapes retain the existing shared pipeline with its 30-second operation limits.

The Teams route budgets conservatively allow 90 seconds per attachment:

| Boundary | Deadline |
|---|---|
| Binding Ask | `10 seconds + attachment_count * 90 seconds` |
| Conversation Ask | Binding deadline + 5 seconds |
| Host Ask | Conversation deadline + 5 seconds |

One attachment receives 100, 105, and 110 seconds at these boundaries.
The SDK activity token retains its independent five-minute limit.
Four or more slow attachments can reach that outer limit before their individual budgets expire.
An outer cancellation still propagates to the actor and releases its durable reservation.
The route acknowledges only after attachment processing and the existing dispatch or rejection path completes.

The new `attachment_download_completed` event records elapsed milliseconds, the configured deadline, and the byte count.
It occurs before content verification and does not mean the attachment passed verification.
The later `attachment_accepted` event confirms acceptance.
The duration state is local to the ingress call and uses the injected `TimeProvider`.
The event contains no resource URL, token, or filename.

## Why the first request also produced a model reply

The Teams contract preserves safe message text when an attachment fails.
The model therefore received the question without the rejected image bytes.
An image-only rejection creates no model turn.
This correction preserves that contract and tests both cases at the new deadline.

## Automated proof and operator retest

The SDK adapter tests pause a PNG body after its first bytes.
A fake clock proves acceptance after 31 seconds and rejection at 60 seconds.
They also prove outer cancellation, partial-file removal, verified bytes, one HTTP attempt, and diagnostic redaction.
The host-route tests cover text-plus-image and image-only replies in an established channel thread.
They check success, deadline rejection, duplicate suppression, model-turn counts, and route dead letters.
The fake clock drives download deadlines; it does not virtualize Akka Ask timers or the shared scanner timeout.

After CI and deployment, repeat the original text-plus-image request and an image-only request.
Compare `attachment_download_completed` with `attachment_accepted` for each request.
If a timeout remains, retain the rejection event with its stage, elapsed time, and outer-cancellation flag.
The container retest must establish whether the additional time resolves the observed deployment failure.
