## 1. Implementation

- [x] 1.1 Classify a `.sig` timeout or transport error as a network failure in `FetchVerifiedManifestAsync`.
- [x] 1.2 Keep an HTTP error status and a signature mismatch as a signature failure.

## 2. Tests

- [x] 2.1 Test that `netclaw update` aborts with the network message when the `.sig` fetch times out or fails to connect.
- [x] 2.2 Test that `netclaw update` aborts with the tampering warning when the signature does not match.
