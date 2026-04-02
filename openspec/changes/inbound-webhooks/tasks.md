## 1. Config and ingress plumbing

- [ ] 1.1 Add `Webhooks` configuration types and JSON schema entries for named routes, verifier settings, prompt overlay, notify settings, and notification targets.
- [ ] 1.2 Add daemon-side webhook route registry and verification services (route lookup, signature/secret validation, event filtering, delivery-id extraction).
- [ ] 1.3 Expose `/api/webhooks/{route}` ingress endpoints with request-size enforcement, duplicate suppression, rate limiting, and fail-closed rejection behavior.

## 2. Webhook session execution

- [ ] 2.1 Add `ChannelType.Webhook` and session-launch plumbing for accepted deliveries.
- [ ] 2.2 Add additive route prompt-overlay injection so webhook routes augment the base system prompt without replacing it.
- [ ] 2.3 Implement webhook invocation execution that normalizes the payload, launches one autonomous session per accepted delivery, and tracks `NotifyPolicy` success/failure.
- [ ] 2.4 Emit deterministic operational receipt alerts for accepted deliveries with route/event/delivery metadata.

## 3. Human-facing notification routing

- [ ] 3.1 Add webhook notification-target handling that maps configured Slack targets into prompt/tool instructions without changing reminder semantics.
- [ ] 3.2 Reuse the existing proactive Slack thread path so webhook-triggered notifications create Slack-native threads/sessions rather than rebinding the original webhook session.

## 4. Validation and documentation

- [ ] 4.1 Add tests for route lookup, verifier failures, request-size rejection, duplicate suppression, rate limiting, accepted-session launch, prompt overlay injection, and `Required` vs `Conditional` notify behavior.
- [ ] 4.2 Update config and operator docs for webhook route registration, ingress security expectations, and Slack notification-target setup.
- [ ] 4.3 Update any required system skill/docs for config-format changes and run the relevant test/quality gates (`dotnet test`, `dotnet slopwatch analyze`, and evals if skill content changes).
