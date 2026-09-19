## 0. Coordination

- [ ] 0.1 Create the focused Proxicon issue with upstream references `netclaw-dev/netclaw#1401` and `netclaw-dev/netclaw#1946`, then verify its URL; GitHub Issues are currently disabled for this repository.

## 1. GroupChat identity and SDK-free contracts

- [x] 1.1 Add the distinct `GroupChat` Teams scope without changing existing scope values, then verify Personal and Channel scope compatibility tests pass.
- [x] 1.2 Add bounded GroupChat session create, parse, malformed-input, and oversized-input handling, then verify Personal and Channel session IDs remain byte-for-byte unchanged.
- [x] 1.3 Extend every SDK-free Teams trust, inbound activity, outbound destination, approval action, and persistence contract with explicit GroupChat semantics, then verify contract tests reject invalid scope combinations.
- [x] 1.4 Add a GroupChat destination that requires tenant, conversation, and validated service URL only, then verify it rejects channel roots and Personal user IDs.

## 2. Authenticated GroupChat ingress and routing

- [x] 2.1 Map SDK 2.1 `ConversationType.GroupChat` at the daemon edge and remove only verified structured bot mentions, then verify no Teams SDK type reaches channel contracts or actors.
- [x] 2.2 Review update and delete activity behavior for GroupChat, then verify unsupported mutation shapes reject before session work.
- [x] 2.3 Add GroupChat configuration gates for disabled access and canonical allowed chat IDs, then verify wrong tenant, disabled access, and unknown chat all fail closed.
- [x] 2.4 Apply global user or verified global group authorization to GroupChat only, then verify channel overrides cannot authorize a GroupChat sender.
- [x] 2.5 Apply per-message structured mention policy for GroupChat, then verify a literal mention and later unmentioned activity do not dispatch in mention-only mode.
- [x] 2.6 Route approved GroupChat messages to one flat durable conversation, then verify two users share one session while sender identities stay distinct.
- [x] 2.7 Extend reply, typing, approval, replay, recovery, and proactive reminder delivery for GroupChat, then verify requester checks and exactly-once delivery behavior remain intact.

## 3. GroupChat configuration, Graph, package, and TUI

- [x] 3.1 Add additive `AllowGroupChats` and `AllowedGroupChatIds` configuration with disabled defaults, then verify old configurations bind with GroupChat disabled.
- [x] 3.2 Update schema, editor model, persistence mapper, doctor, and runtime policy together, then verify invalid canonical IDs block persistence and persisted IDs reach runtime ACL evaluation.
- [x] 3.3 Investigate SDK and Graph support for bounded app-installed chat metadata, then verify the selected path adds no broad chat or directory permission.
- [x] 3.4 Defer metadata discovery and caching, then verify canonical IDs remain authority with a safe abbreviated display fallback.
- [x] 3.5 Add Teams TUI GroupChat management, canonical-ID entry, safe display labels, enable control, and summary count, then verify typed-input, reopen, and runtime-consumer coverage.
- [x] 3.6 Update the Teams manifest with `groupchat`, `ChatMessage.Read.Chat`, and `supportsFiles`, then verify deterministic package tests reject broad chat or file permissions.

## 4. Bounded Teams attachment ingress

- [x] 4.1 Add additive `AllowAttachments` configuration with a disabled default, then verify the legacy attachment rejection path remains active when disabled.
- [x] 4.2 Inspect the SDK 2.1 attachment download APIs and sanitized evidence, then document the exact authenticated inline-image and Personal-file retrieval routes at the daemon boundary.
- [x] 4.3 Implement a Teams-specific bounded downloader and SDK-free metadata, then verify no SDK attachment, raw URL, OAuth token, or download URL crosses into actors or persistence.
- [x] 4.4 Reuse shared attachment count, byte, MIME, signature, scanner, image-normalization, safe-name, and managed-storage controls, then verify an invalid name cannot construct a workspace path.
- [x] 4.5 Implement streaming limits, cancellation, safe redirect policy, and partial-file cleanup, then verify missing or false content length, timeout, HTTP failure, redirect escape, and cancellation reject safely.
- [x] 4.6 Implement authenticated inline PNG, JPEG, GIF, and WebP support for Personal, Channel, and GroupChat, then verify valid images use the existing inline or path-only provider decision.
- [x] 4.7 Implement Personal `application/vnd.microsoft.teams.file.download.info` staging for catalog-approved text, structured data, image, document, and legacy-office files, then verify CSV stays untrusted attachment data.
- [x] 4.8 Keep ordinary Channel and GroupChat files fail closed unless the investigation proves a bounded route without broad permissions, then verify an inaccessible file creates no model turn.
- [x] 4.9 Join attachment staging to durable activity idempotency, then verify an accepted Teams retry creates neither a second staged file nor a second model turn.

## 5. Teams-only regression proof and documentation

- [x] 5.1 Add deterministic GroupChat identity, translation, ACL, mention, session, output, approval, replay, recovery, and proactive-delivery tests, then verify Personal and Channel regressions remain unchanged.
- [x] 5.2 Add deterministic attachment tests for accepted images and Personal files, rejected MIME and signature mismatches, limits, traversal attempts, retries, wrappers, and scope-specific file handling.
- [x] 5.3 Update the Teams integration guide, package guide, configuration guide, Graph status text, and owner live smoke plan, then verify each statement separates RSC from Entra permissions.
- [x] 5.4 Update Teams TODO and non-goal wording, then verify it describes supported inline images, Personal files, and deferred ordinary Channel and GroupChat files accurately.
- [x] 5.5 Inspect the final diff for containment, then verify Slack, Discord, Mattermost, generic media, generic approval, generic session, and generic tool authorization implementations are unchanged except narrow additive seams.

## 6. Validation and review handoff

- [x] 6.1 Run focused Teams, Graph, configuration, TUI, package, and attachment tests, then record exact passing counts and any environment-gated exclusions.
- [x] 6.2 Run `dotnet restore Netclaw.slnx`, `dotnet build Netclaw.slnx`, and `dotnet test Netclaw.slnx`, then record exact results.
- [x] 6.3 Run the vulnerability, Slopwatch, header, whitespace, strict OpenSpec, and smoke gates from the feature brief, then record exact results.
- [x] 6.4 Prepare the owner GroupChat, image, and Personal-file live smoke steps, then verify the plan requires package upgrade and does not require a live tenant in CI.
- [x] 6.5 Prepare one focused PR from `feature/teams-groupchat-attachments` to `dev` without merge, deploy, or final upstream pull request, then verify its description states limits and owner gates.
