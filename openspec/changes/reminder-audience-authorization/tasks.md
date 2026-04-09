## 1. Shared Reminder Audience Validation

- [x] 1.1 Add a shared reminder audience authorization helper/contract in the reminder save path that compares requested reminder audience against the caller's source audience using existing `TrustAudience` ordering
- [x] 1.2 Extend reminder save commands/callers to carry the creator's current source audience / authority into `ReminderManagerActor`
- [x] 1.3 Fail reminder writes closed when source authority context is missing, invalid, or the requested audience exceeds the creator's authority

## 2. Conversational and Tool Minting

- [x] 2.1 Update `SetReminderTool` so omitted `audience` inherits from the creating session/channel audience instead of documenting or relying on deployment default fallback
- [x] 2.2 Update reminder protocol/model comments and any reminder tool messaging to describe persisted source-audience inheritance and lower-audience allowance
- [x] 2.3 Ensure reminder save responses return clear validation errors for invalid audience values and over-privileged requests

## 3. REST, Admin, CLI, and Import Enforcement

- [x] 3.1 Update daemon reminder create/import/admin write paths to resolve the caller's authority ceiling server-side and pass it through reminder save validation
- [x] 3.2 Reject serialized reminder imports whose `Audience` value is invalid or broader than the authenticated caller's source authority
- [x] 3.3 Reject non-conversational reminder write requests when the server cannot determine the caller's reminder audience authorization context

## 4. Execution Semantics

- [x] 4.1 Remove execution-time deployment-default fallback semantics for newly validated reminder definitions so execution trusts the stored reminder audience
- [x] 4.2 Update reminder execution logging/comments to reflect that stored audience is authoritative because minting-time validation is mandatory

## 5. Tests

- [x] 5.1 Unit test reminder audience comparison: equal audience allowed, lower audience allowed, higher audience rejected
- [x] 5.2 Unit/integration test `SetReminderTool` and reminder save flow: omitted conversational audience persists the creating session/channel audience
- [x] 5.3 Unit/integration test reminder save/import paths: invalid audience value rejected with clear error and no persistence
- [x] 5.4 Unit/integration test reminder save/import paths: over-privileged audience rejected for REST/admin/CLI/import callers
- [x] 5.5 Update execution tests to verify stored reminder audience is used directly and deployment posture no longer broadens reminders whose audience was omitted during conversational creation

## 6. Spec and Doc Sync

- [x] 6.1 Sync delta specs to main specs via `/opsx-sync` after implementation is verified
