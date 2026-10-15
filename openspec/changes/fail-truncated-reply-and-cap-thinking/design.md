## Context

The session actor and the sub-agent actor each call a model and read a stream
of updates. Each actor has a processing watchdog with a no-progress timer. A
thinking update and a keepalive were both "progress" for that timer. Each actor
also had a text-success path that did not look at the finish reason.

See proposal.md for the motivation. The cap is a reliability boundary, not a
security boundary. The preview redaction is a security gate.

## Goals / Non-Goals

**Goals:**
- One place decides what counts as an answer update. The session and the
  sub-agent share it.
- A truncated reply fails loudly in both actors.
- The cap needs no new configuration key.

**Non-Goals:**
- No change to the compaction summary or the session memory observer. Both
  still accept a `length` reply. A follow-up item tracks them.
- No retry for a truncated reply.
- No change to the wall-clock timeout of a sub-agent.

## Decisions

**Classify each stream update as Keepalive, Thinking, or Answer.**
`StreamProgress.Classify` returns the class. Usage-only and empty updates are
Keepalive. Non-empty reasoning, and empty or whitespace-only text, are
Thinking. Non-empty text, a tool call, a finish reason, and other content are
Answer. `IsSubstantive` stays true for every class except Keepalive, so the
commit gate does not change.
Alternative: a second boolean next to `Substantive`. Rejected, because two
booleans allow a state that has no meaning.

**Arm the cap in the shared watchdog.** The watchdog starts a third timer at
three times the no-progress deadline. Thinking and Answer reset the no-progress
timer. Only Answer restarts the cap timer. The session and the sub-agent use
the same watchdog, so they use the same rule.
Alternative: a fixed cap value in configuration. Rejected, because the
no-progress timeout already carries the operator intent. A new key needs a
schema change and adds a second knob for one concern.

**Check `length` after the tool-call branch and after the empty-response retry.**
A reply with a tool call runs its tools. A thinking-only or empty reply keeps
its retry. A reply with text and no tool call fails. This order keeps each
existing path unchanged.
Alternative: check `length` first. Rejected, because it would fail a reply
that the retry path can recover.

**Redact the whole stub, then cut at 300 on a text-element boundary.** The
cut-first order can leave a partial secret that no redaction pattern matches.
The cut on a UTF-16 unit can leave a lone surrogate in JSON.

**Throttle sub-agent pings with a gate that reads the scheduler clock.** A ping
fires on the first substantive update, after 2 seconds, or on an Answer update
when the last ping was not an Answer. A later ping carries the strongest class
seen since the last ping, so no answer update is lost. If the clock moves
backward, the gate re-anchors.

## Risks / Trade-offs

- [A provider returns `length` for an answer that is nearly complete] -> The
  turn fails with a clear message. The operator can raise the output limit.
- [A provider drops a pending tool call when the finish reason is `length`] ->
  The reply has no tool call and fails as truncated. This is the safe result.
  The tool-call-with-length control test covers the self-hosted client only.
- [A slow model thinks for a long time and then answers] -> The cap is three
  times the no-progress timeout (60 minutes at the default 1200 s). An answer
  update restarts it.
- [The streaming text already reached a live viewer before the error] -> The
  viewer sees the cut-off text, then the error. The final reply is not posted.

## Migration Plan

No data migration and no configuration change. Roll back by revert. The
two new reason strings appear in sub-agent results and logs only.
