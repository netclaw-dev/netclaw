## 1. Minisign Format Parser

- [x] 1.1 Create `MinisignVerifier` static class in `Netclaw.Configuration/Security/` with public key parsing (2-byte algo + 8-byte key ID + 32-byte Ed25519 key from base64 line)
- [x] 1.2 Add signature file parsing (2-byte algo + 8-byte key ID + 64-byte Ed25519 signature from base64 line)
- [x] 1.3 Add Ed25519 signature verification method using `NSec.Cryptography` — accepts manifest bytes, signature bytes, and public key bytes
- [x] 1.4 Embed the public key from `feeds/releases/manifest.pub` as a compiled `ReadOnlySpan<byte>` constant
- [x] 1.5 Add unit tests for: valid signature accepted, tampered content rejected, malformed signature rejected, wrong key ID rejected

## 2. Manifest Signature Verification

- [x] 2.1 Add `FeedConstants.BinaryManifestSignatureUrl` for `manifest.json.sig` endpoint
- [x] 2.2 Change `UpdateCheckService.FetchManifestAsync` return type to a result type that distinguishes success, network failure, and signature failure
- [x] 2.3 In `FetchManifestAsync`, download `manifest.json.sig` after fetching the manifest, verify signature before deserializing, return signature failure result if verification fails
- [x] 2.4 Update `UpdateCommand.RunAsync` to check the result type and display appropriate error messages for signature failures vs. network failures
- [x] 2.5 Update `CheckForUpdateAsync` callers to handle the new result type (background checks treat signature failure same as network failure — log warning, return no-update)
- [x] 2.6 Add integration-style tests with real minisign-signed test fixtures (sign a test manifest, verify round-trip)

## 3. Periodic Daemon Update Check

- [x] 3.1 Convert `BinaryUpdateCheckService` from `IHostedService` to `BackgroundService` with `ExecuteAsync` loop
- [x] 3.2 Run initial check at startup (preserve existing behavior), then sleep 24 hours between rechecks
- [x] 3.3 Add `UpdateAvailable` to `AlertType` enum with wire type `update.available`
- [x] 3.4 Inject `IOperationalNotificationSink` into `BinaryUpdateCheckService` and emit `UpdateAvailable` alert when `result.IsUpdateAvailable` is true, with current/latest version in context
- [x] 3.5 Add unit tests for: alert emitted on update detection, alert not emitted when up-to-date, periodic timer fires after interval

## 4. Validation and Cleanup

- [x] 4.1 Run `dotnet build` to verify no compilation errors
- [x] 4.2 Run `dotnet test` to verify all tests pass (1,282 pass, 0 fail)
- [x] 4.3 Run `dotnet slopwatch analyze` to verify no new violations (3 pre-existing warnings only)
- [x] 4.4 Spec updates already in openspec/changes/verify-update-signatures/specs/netclaw-cli/spec.md (will sync at archive)
