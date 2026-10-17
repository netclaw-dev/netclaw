## Why

The update command shows a tampering warning when the `manifest.json.sig` download times out.
A timeout does not show tampering. The warning sends the operator to the wrong cause.

## What Changes

- A `.sig` download that times out or fails with a transport error becomes a network failure. The update still aborts.
- The error message for that failure names the network problem. It does not warn of tampering.
- An HTTP error status for the `.sig` file, such as 404, stays a signature failure. The update still aborts.
- A real signature mismatch stays a signature failure with the tampering warning.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `manifest-signature-verification`: The scenario "Missing signature file aborts update" now separates an HTTP error status from a timeout or transport error. A new scenario covers the timeout case.

## Impact

- `src/Netclaw.Configuration/Feeds/UpdateCheckService.cs` (`FetchVerifiedManifestAsync`).
- `src/Netclaw.Cli.Tests/Cli/UpdateCommandTests.cs`.
- No change to the manifest format, the signing key, or the daemon behavior.
