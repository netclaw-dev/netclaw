## ADDED Requirements

### Requirement: Group Chat discovery is participant scoped and optional
The system SHALL discover Group Chat metadata only through a selected canonical user ID.
The system SHALL request only that user's chats and SHALL not add that user to a principal allowlist.
The system SHALL treat `Chat.ReadBasic.All` as optional tenant-wide application metadata consent.
The system SHALL keep existing Group Chat ingress and local removal available without this consent.

#### Scenario: Metadata consent is absent
- **WHEN** chat metadata discovery returns a permission failure
- **THEN** existing saved chats remain visible and removable

### Requirement: Group Chat discovery is bounded and paged
The system SHALL return Group Chat metadata through an SDK-neutral paged contract.
The system SHALL use opaque continuation handles with tenant, selected-user, and generation scope.
The system SHALL reject malformed, foreign, expired, or mismatched continuations.
The system SHALL require a group chat type and canonical chat ID before addition.

#### Scenario: A match occurs on a later page
- **WHEN** the first page has a continuation and the second page has a valid Group Chat
- **THEN** the operator can load the later page and select that Group Chat

#### Scenario: A meeting has a chat-shaped identifier
- **WHEN** a metadata record has a meeting type and an ID ending in `@thread.v2`
- **THEN** the system excludes the record from Group Chat selection
