## 1. Proof Codec and Validator

- [x] 1.1 Add `LocalControlOperation` with an operation byte and a purpose chain, and verify the fixed pairing vector and the state-bound purpose chain.
- [x] 1.2 Make the validator check the requested operation, and verify cross-operation, cross-state, wrong-byte, replay, and time-window refusals.

## 2. Daemon Endpoints

- [x] 2.1 Add the failing endpoint tests first, and record that 20 of 25 fail on the old endpoint.
- [x] 2.2 Add `POST /api/local-control/v1/provider-oauth-result` with the shared request bounds, rate limit, and rejection map.
- [x] 2.3 Remove the token fields from the status endpoint, and verify that no caller in any exposure mode gets a token.

## 3. Host CLI

- [x] 3.1 Move proof creation for both operations into `DaemonApi`, and verify the direct endpoint, no bearer token, and the state binding.
- [x] 3.2 Make `OAuthFlowCoordinator` read the result after `Completed` and fail with a visible error on refusal.

## 4. Proof and Documentation

- [x] 4.1 Update `docs/architecture/tool-authorization.md` and sync the `local-control-proof` spec.
- [x] 4.2 Add a focused mutation target for the operation and purpose-chain checks.
- [x] 4.3 Run the live lab in `local`, `tailscale-serve`, `tailscale-funnel`, and `cloudflare-tunnel` modes for host CLI success and remote-device denial.
- [x] 4.4 Run the focused tests, Slopwatch, and the header check.
