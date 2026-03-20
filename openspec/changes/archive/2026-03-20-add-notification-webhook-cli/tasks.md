## 1. CLI command surface

- [x] 1.1 Add `netclaw notification webhook` command routing and help text for `list`, `add`, `remove`, and `test`
- [x] 1.2 Implement selector parsing for webhook targets by zero-based index and unique name, including ambiguity errors

## 2. Config persistence and validation

- [x] 2.1 Add config helper logic to read and rewrite `Notifications.Webhooks` across `netclaw.json` and `secrets.json` while keeping webhook URLs and header values in secrets only
- [x] 2.2 Implement `add` and `remove` flows that stage mutations in memory, validate with `NotificationConfigValidator`, and write files in safe order
- [x] 2.3 Normalize legacy base-config webhook URLs and headers into `secrets.json` during CLI-managed operations

## 3. Probe execution and operator output

- [x] 3.1 Implement `list` output with stable indexes, safe identity details, redacted URL display, and redacted header reporting
- [x] 3.2 Implement `test` as a single bounded HTTP probe that honors configured timeout, avoids retries, and reports safe success/failure diagnostics

## 4. Verification and docs

- [x] 4.1 Add CLI tests for help, list, add, remove, selector ambiguity, validation failures, and secret redaction
- [x] 4.2 Add probe tests for HTTP success, HTTP failure, and timeout without retry behavior
- [x] 4.3 Update operator docs for the notification webhook command surface and config/secrets layout
