## Context

See `proposal.md` for the purpose and PRD links.
The first implementation starts at `2fc8cf53d2a6ddc47d7f91f14476099a2b6b4bd6`.
The audit reviews `79d66eedaeee8c4cb40f123103bf3edb4c538c98` before merge.
It finds provider policy in the base, incomplete result contracts, and weak causal assertions.
The user authorizes these corrections and forbids merges and auto-merge.

## Goals / Non-Goals

**Goals:**

- Keep one actor-local lifecycle, engine, cursor, and prompt-projection owner.
- Keep provider admission order in the provider actor.
- Return one complete transport result across the provider boundary.
- Pass complete canonical `ChannelInput` values to common operations.
- Make required common construction inevitable.
- Prove rejection and lifecycle order through causal observations.
- Investigate old output during pipeline replacement through a controlled test.

**Non-Goals:**

- A workflow framework, provider-policy bag, step interpreter, registrar, or additional actor.
- Uniform gate order or error policy across providers.
- Changes to user capabilities, authorization grants, persistent IDs, or event formats.
- A merge, auto-merge request, release, or deployment.

## Decisions

### Provider admission and one common state owner

The provider actor owns its full ordered admission entrypoint.
The common base owns initialization, recovery, hydration state, prompt projection, cursor application, and trusted-turn admission.
The existing engines remain the sole owners of their existing state.
The queue writer, handle, pending list, and engines stay private to the common owner.

The provider prepares content and authority from its existing DTO.
It constructs a complete `ChannelInput` before the shared hydration operation.
Common code does not replace supplied contents or targets.
The existing hydration engine may add the adopted-context projection.
No parallel authority carrier is required.

The schematic live flows retain these orders:

```text
Slack:
  Start the inbound deadline and receipt log.
  Consume an applicable text approval response.
  Classify the live text and prepare attachments.
  Reject empty content, closed ingress, unavailable queue, and stale events.
  Construct the complete input and apply deferred hydration.
  Start a write deadline linked to the inbound deadline.
  Write the input, advance pending state, and refresh provider activity.
  Retain the outer Slack error boundary.

Discord and Mattermost:
  Reject closed ingress and empty transport input.
  Consume an applicable text approval response.
  Start the preparation deadline and classify text.
  Require the queue before attachment work.
  Construct the complete input and apply deferred hydration.
  Start an independent write deadline.
  Write the input and advance pending state.
  Retain the provider write catches and exception propagation.
```

These flows omit transport logs and attachment internals. The implementation retains their authority checks.
The write deadline starts after hydration. A shared operation must not start that deadline earlier.
A closed Discord ingress rejects an approval-text input before the approval flow.
Slack retains its earlier approval-text path and its session-authority verdict.
A rejected input writes no ordinary live turn.

### Complete transport outcomes

Transport hooks return success or a failure with required kind and message.
The result family must actually prevent an external failure case without its descriptor.
A private parameterless record constructor alone is insufficient proof of a closed family.
The common actor records the first failed output result until the completed turn.
Its private adapter can preserve the output engine's Boolean API.
The provider no longer calls a protected failure callback under a mutable mode flag.

Direct notices may discard their transport result, as before.
A failed content output reports its completed turn once.
A failed empty-turn fallback reports that turn's failure.
A failed feedback pipe still propagates to actor supervision.

### Construction and real provider effects

The base constructor creates its required handle, engines, logger, recovery handlers, and initial behavior.
The provider passes the logger data that previously entered `InitializeBinding`.
Constructor code must not execute provider overrides before provider fields exist.
Stored method delegates may execute only after construction.
The design removes the second initialization call and its nullable engine state.

Provider operations name the real lifecycle phase.
Mattermost cancels pulse activity before the deactivation drain.
Slack clears its indicator in its own PostStop before base disposal.
The reinitialize phase retains each provider's current cleanup effect.
Discord needs no mandatory empty activity override.

The common actor applies and replays cursor events.
Each provider selects its existing persistence mechanism through one narrow actor operation.
Slack keeps `PersistAsync`. Discord and Mattermost keep `Persist`.
The base needs no provider-identity branch for these choices.
The output engine already removes its failed prompt. The actor does not remove a second same-call entry.
Incidental warning punctuation and diagnostic names do not justify provider policy hooks.

### Deadlines and output identity

The existing injected TimeProvider controls deadline token sources.
Slack retains linked inbound and write cancellation.
Discord and Mattermost retain their independent write deadlines.
No new configuration value or clock carrier is required.

A controlled test first injects old output during drain on the old source.
If that test fails for the intended assertion, the reliability correction uses the handle's existing generation.
Each output callback carries the generation captured by its materialization.
The actor also distinguishes callbacks from a previous actor incarnation.
It rejects obsolete or uninitialized output before any transport, journal, cursor, or stop effect.
There is no second generation counter or reset state machine.
The producer and every callback consumer must use the same representation.

### Preserve persistence and authority

Keep these exact prefixes and the existing URI escape:

- `slack-thread-cursor-`
- `discord-session-cursor-`
- `mattermost-session-cursor-`

Keep `CursorAdvanced`, `PendingApprovalPromptTracked`, and `PendingApprovalPromptCleared` and their serializer bindings.
Keep journal event order, replay behavior, and truncation rules.
The correction adds no journal event or journal write.

The trusted-turn path compares session identity before ingress and queue admission.
It retains original sender acknowledgement and all source authority and delivery metadata.
A wrong-session reminder receives a Nack and writes no input.
A permitted reminder retains its mint-time authority.
The session remains the authority for durable admission and approval verdicts.
Slack still rejects a malformed missing required history dependency before pipeline creation.
Discord and Mattermost retain their supported no-history state.

### Alternatives and synthesis

Two isolated designs compare direct actor operations with provider-object composition.
The direct design hides common mutable state without another provider object or admission facade.
Composition still needs actor-owned persistence, timers, command handlers, and cleanup.
Its larger contract adds a boundary without removing those duties.
The direct design is selected.
The composition review contributes the incarnation and generation checks for the conditional reliability correction.
No supported alternate-vendor runner exists. The parent performs synthesis without an alternate-vendor judge.

## Migration Plan

1. Repair causal assertions and pin provider order on the existing source.
2. Prove the stale-output sequence before its reliability correction.
3. Replace the incomplete common contracts and restore provider admission ownership.
4. Verify provider behavior, journal identity, authority, files, and cleanup through the combined suites.
5. Update the existing dependent PRs through actual `gh stack` commands.
6. Retain reviewed heads, actual line accounts, mutation receipts, and current-head CI evidence.

The parent can regroup the unmerged slices when the corrected contract crosses all providers.
Every delivered slice must compile and pass its relevant checks.
No legacy compatibility path remains in the final source.

## Risks / Trade-offs

- Visible provider order repeats some sequence code. Shared state and common algorithms still have one owner.
- A broad protected API can repeat the old leak. Keep only operations that protect real common state or authority.
- Early write deadlines alter behavior. Construct the write deadline only after deferred hydration.
- A partial result can hide delivery failure. Require one complete outcome from every transport path.
- Negative assertions can pass early. Use later admitted sentinels or controlled phase entry and exact sequences.
- An integer generation can repeat after actor restart. Include actor-incarnation identity at that boundary.
- A reliability correction changes a failure path. Require a red reproduction and focused mutation review before acceptance.
