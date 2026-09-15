## Purpose

Define secure Microsoft Teams GroupChat behavior and bounded inbound attachment ingress for the Teams channel.
The capability keeps canonical Teams identities, default-deny access, and shared untrusted-media controls authoritative.

## ADDED Requirements

### Requirement: GroupChat is a distinct durable Teams conversation scope
The system SHALL model Teams GroupChat as a distinct scope from Personal and Channel.
The system SHALL map one accepted canonical group-chat conversation to one durable session.
The session identity SHALL use the bounded canonical Teams encoding and the conceptual form `teams~<tenant>~groupchat~<conversation-id>/conversation`.
Personal and Channel session identities SHALL remain byte-for-byte compatible.
GroupChat SHALL not use a channel root or thread activity identifier.

#### Scenario: Two approved humans use one group chat
- **GIVEN** two approved humans send accepted messages in one canonical group chat
- **WHEN** the system creates their session identities
- **THEN** both messages use the same group-chat session
- **AND** each message retains its authenticated sender identity

#### Scenario: Invalid group-chat identity is rejected
- **WHEN** a GroupChat identity has a missing, malformed, or oversized canonical component
- **THEN** the system rejects it before actor or persistence key creation

### Requirement: GroupChat ingress is explicit and default deny
The system SHALL disable GroupChat ingress by default.
The system SHALL accept a GroupChat message only when the authenticated tenant matches configuration, group chats are enabled, the canonical chat ID is allowed, the sender is authorized, and mention policy passes.
The system SHALL assign the Team trust audience to an accepted group-chat message by default.
Installation of the app in a group chat SHALL not grant message or sender authority.

#### Scenario: Disabled GroupChat is rejected
- **GIVEN** the SDK receives an authenticated GroupChat message
- **AND** group chats are disabled
- **WHEN** the ingress policy evaluates the message
- **THEN** it rejects the message before session dispatch

#### Scenario: An allowed group chat has an unauthorized sender
- **GIVEN** a canonical group-chat ID is allowed
- **AND** the sender matches no allowed user or verified global group
- **WHEN** the ingress policy evaluates the message
- **THEN** it rejects the message

### Requirement: GroupChat uses global principal authorization and explicit mentions
The system SHALL authorize a GroupChat sender only through a global allowed user or verified membership in a global allowed group.
The system SHALL fail closed when no global principal is configured or group membership verification is unavailable.
Channel access overrides SHALL not grant GroupChat access.
When Teams mention-only mode is enabled, each accepted GroupChat message SHALL contain a verified structured bot mention.
The system SHALL not extend a GroupChat mention to later unmentioned messages.

#### Scenario: A verified global group member is accepted
- **GIVEN** group chats are enabled and the canonical chat ID is allowed
- **AND** the sender is a verified member of a configured global group
- **AND** the message has a verified bot mention when mentions are required
- **WHEN** the ingress policy evaluates the message
- **THEN** it accepts the message with Team audience

#### Scenario: A literal mention does not pass mention-only mode
- **GIVEN** mention-only mode is enabled
- **WHEN** a group-chat message contains only text that resembles a bot mention
- **THEN** the system ignores the message

### Requirement: GroupChat has flat conversation delivery semantics
The system SHALL deliver replies, typing indicators, approval cards, and proactive reminder output to the accepted group chat.
The system SHALL validate an approval action against the actual authenticated human sender.
The system SHALL preserve existing approval authority and replay protection.
The system SHALL recover the group-chat delivery destination after actor recovery and deliver a reminder at most once under the established durable delivery policy.

#### Scenario: A GroupChat approval has a different requester
- **GIVEN** a pending approval came from one authenticated group-chat participant
- **WHEN** another participant invokes its approval action
- **THEN** the system rejects the action unless the shared approval contract authorizes that participant

#### Scenario: GroupChat recovery delivers a reminder
- **GIVEN** an accepted group-chat message creates a reminder
- **AND** the actor restarts before the reminder fires
- **WHEN** the reminder executes
- **THEN** the system uses the recovered group-chat destination once

### Requirement: Teams package permissions are least privileged
The Teams package SHALL declare the `personal`, `team`, and `groupchat` bot scopes.
The package SHALL retain `ChannelMessage.Read.Group` and declare `ChatMessage.Read.Chat` for installed group-chat message delivery.
The package SHALL declare file support.
The package SHALL not request tenant-wide chat message or file permissions for this capability.

#### Scenario: The generated package requests GroupChat delivery
- **WHEN** the system generates a Teams package
- **THEN** the manifest contains the `groupchat` scope and `ChatMessage.Read.Chat`
- **AND** it contains no tenant-wide chat message or broad file-read permission

### Requirement: GroupChat metadata uses canonical authority
The system SHALL persist canonical group-chat IDs only.
The system SHALL use an abbreviated canonical ID as the safe display fallback.
The system SHALL permit an operator to enter a canonical group-chat ID.
Metadata discovery and metadata caching are deferred.
The system SHALL not add a broad directory or chat-read permission only for group-chat discovery.

#### Scenario: Metadata lookup cannot resolve a friendly label
- **GIVEN** the system cannot obtain group-chat metadata under least privilege
- **WHEN** an operator adds a group chat
- **THEN** the operator can save a validated canonical chat ID
- **AND** the saved authority does not depend on a display name

### Requirement: Teams attachment ingress is disabled by default
The system SHALL disable Teams attachment ingress by default.
When attachments are disabled, the system SHALL retain the current fail-closed rejection of non-rendering attachment shapes.
The system SHALL ignore the Teams HTML text-rendering wrapper.
The system SHALL use the shared attachment count, byte, scanner, MIME, signature, image-normalization, and workspace controls.

#### Scenario: Disabled attachment ingress receives an attachment-only message
- **GIVEN** Teams attachment ingress is disabled
- **WHEN** a Personal message contains a file attachment and no safe text
- **THEN** the system rejects the attachment before a model turn
- **AND** it retains durable activity ownership to prevent repeated rejection work

#### Scenario: Text accompanies a rejected attachment
- **GIVEN** a Teams message has nonempty safe text and a rejected attachment
- **WHEN** the binding processes the message
- **THEN** it sends the safe text through one normal model turn
- **AND** it never sends rejected attachment bytes to the model

#### Scenario: A Teams text wrapper is present
- **WHEN** a Teams message contains only the known HTML text-rendering wrapper
- **THEN** the system processes the message text
- **AND** it does not treat the wrapper as a downloadable file

### Requirement: Inline images use authenticated bounded retrieval
The system SHALL accept authenticated inline PNG, JPEG, GIF, and WebP images where the Teams SDK provides a supported attachment shape.
The system SHALL support that image behavior in Personal, Channel, and GroupChat scopes.
The system SHALL keep a captured raw URL only at the authenticated daemon boundary and expose only SDK-free metadata to actors.
The system SHALL not fetch a URL from user text or persist an attachment download URL.
The system SHALL enforce HTTPS, an allowlisted host, no redirects, streaming byte limits, cancellation, MIME and signature validation, image decode validation, normalization, and safe temporary-file cleanup.

#### Scenario: A valid inline image reaches a capable model
- **GIVEN** attachment ingress is enabled and a Teams message contains a valid inline PNG
- **AND** the selected model accepts image input
- **WHEN** the system accepts the message
- **THEN** the shared media pipeline supplies image content to the model

#### Scenario: An inline image has mismatched bytes
- **GIVEN** a Teams attachment declares an image MIME type
- **WHEN** signature or image decoding fails
- **THEN** the system rejects the attachment without a model turn

#### Scenario: A valid attachment is the only input
- **GIVEN** a valid supported attachment and no message text
- **WHEN** the binding accepts the attachment
- **THEN** it creates exactly one model turn from safe attachment content

### Requirement: Personal files use managed shared attachment staging
The system SHALL accept a Personal attachment with the Teams file-download information shape only after safe bounded download and shared validation.
The system SHALL support catalog-approved text, structured-data, image, document, and legacy-office formats.
The system SHALL stage a validated non-image file in the existing managed session attachment location.
The system SHALL keep attachment content untrusted and separate from executable message text.
The system SHALL not implement a Teams-specific document parser.

#### Scenario: A Personal CSV remains untrusted data
- **GIVEN** a user sends a valid Personal CSV and executable message text
- **WHEN** the system accepts the activity
- **THEN** the message text stays executable user text
- **AND** the staged CSV is available only as untrusted attachment data

#### Scenario: A file name contains a traversal attempt
- **WHEN** Teams supplies an attachment name with a traversal, absolute path, control character, or reserved device name
- **THEN** the system does not use the value to construct a file path
- **AND** it generates a safe internal name or rejects the attachment

### Requirement: Channel and GroupChat ordinary files fail closed without a proven route
The system SHALL support a Channel or GroupChat ordinary file only when a secure authenticated download path is proven without broad tenant permissions.
When that route is not proven, the system SHALL reject the ordinary file with a safe reason and SHALL not create a model turn from inaccessible content.
Inline image handling SHALL remain independent from ordinary-file handling.

#### Scenario: A channel file requires broad Microsoft 365 permissions
- **GIVEN** the only available channel-file retrieval route requires broad file or chat permissions
- **WHEN** the system receives that channel file shape
- **THEN** it rejects the file
- **AND** it does not add the broad permission

### Requirement: Teams configuration and management expose bounded controls
The Teams configuration SHALL add disabled-by-default GroupChat and attachment controls.
The configuration SHALL persist canonical allowed group-chat IDs and preserve backward compatibility when all new fields are absent.
The Teams management UI SHALL show group-chat control and count, group-chat management, and attachment status.
The UI SHALL show a safe abbreviated canonical ID when no friendly label exists.
The schema, persistence mapping, doctor, and runtime policy SHALL consume the same canonical configuration values.

#### Scenario: Existing Teams configuration has no new fields
- **GIVEN** an existing Teams configuration omits GroupChat and attachment fields
- **WHEN** the system loads it
- **THEN** GroupChat remains disabled
- **AND** attachment ingress remains disabled

#### Scenario: The UI saves a group-chat selection
- **WHEN** an operator adds a GroupChat through a friendly selection or canonical entry
- **THEN** the configuration stores only the canonical chat ID
- **AND** runtime ingress evaluates that canonical ID
