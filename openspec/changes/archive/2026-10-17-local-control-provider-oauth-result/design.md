## Context

See `proposal.md` for the security problem.
The [local-control proof](../../../../docs/spec/GLOSSARY.md#local-control-proof) already proves host key-ring access for pairing-code creation.
The proof has an operation byte, a 30-second lifetime, and a single-use nonce cache.
The provider OAuth flow runs in the daemon. The browser callback stores the result in `OAuthPkceService` memory.
The host CLI polls the status endpoint with its normal daemon client.
In a tunnel mode that client sends the bootstrap device token, which a remote paired device also has in kind.

## Goals / Non-Goals

**Goals:**

- The host CLI finishes provider OAuth in every exposure mode.
- No device token, bootstrap token, or source address unlocks provider tokens.
- A proof for one operation or one OAuth state cannot unlock another.
- Reuse the current proof codec, validator, replay cache, rate limit, and request bounds.

**Non-Goals:**

- Do not give provider tokens to a remote paired device.
- Do not add a second proof scheme or a new host secret.
- Do not change the pairing proof bytes or purpose.
- Do not keep a token path on the status endpoint for the `LocalProcess` claim.

## Decisions

### Bind the operation and the state through the purpose chain

Each operation has a Data Protection purpose chain:

| Operation | Operation byte | Purpose chain |
|---|---|---|
| `generate-pairing-code` | `1` | `Netclaw.LocalControl.Pairing.v1` |
| `read-provider-oauth-result` | `2` | `Netclaw.LocalControl.ProviderOAuthResult.v1`, then the OAuth state |

Data Protection derives a different subkey for each purpose chain.
Thus a proof for another operation or another state fails authentication before the validator reads it.
The validator also compares the operation byte with the requested operation.
That second check refuses a payload that has the correct purpose and a wrong operation value.

The 26-byte layout does not change, so protocol version `1` stays.
An older daemon has no OAuth endpoint and returns `404`.

Alternatives:

- A state hash in the payload changes the layout and needs protocol version `2`.
- A new HMAC key duplicates the key-ring authority.
- A proof header on the status endpoint sends the proof to the paired-client endpoint, which can be remote.

### Read the tokens from a separate local-control endpoint

```text
schematic: omits rate limits, body bounds, and HTTP error mapping

host CLI (DaemonApi)
  POST {client endpoint}/api/provider/oauth/start         bearer token if the mode needs it
  GET  {client endpoint}/api/provider/oauth/status/{state} flags only
  when status == Completed:
    proof = protect(op 2, now, nonce) under purpose chain [ProviderOAuthResult.v1, state]
    POST {daemon config endpoint}/api/local-control/v1/provider-oauth-result {state, proof}
         no proxy, no redirect, no bearer token

daemon
  validator: authenticate purpose chain -> version -> operation byte -> time window -> record nonce
  OAuthPkceService: completed result for state ? 200 tokens : 409
```

| Decision or data | Owner | Lifetime |
|---|---|---|
| Proof creation | `DaemonApi` in the host CLI | Call-local |
| Local-control endpoint address | `DaemonApi` from daemon configuration | Call-local |
| Proof validation | `LocalControlPairingProofValidator` | Call-local |
| Accepted nonces | `LocalControlPairingProofValidator` | Process-local |
| Completed OAuth result | `OAuthPkceService` | Process-local, pruned after its TTL |
| Host key ring | Data Protection provider | Durable |

No actor owns this path. Nothing new persists. A daemon restart clears the nonce cache and the OAuth results.

### The CLI fails loudly

`OAuthFlowCoordinator` reads the result only after the status poll reports `Completed`.
A refused read, an older daemon, or an unsupported version stops the flow with an error message.
The CLI does not retry with the status endpoint or a bearer token.

## Risks / Trade-offs

- [An old CLI with a new daemon cannot finish provider OAuth] → The CLI and daemon ship together. The error text names the missing token payload.
- [The OAuth state travels in the request body] → The state is not a secret. The proof, not the state, carries the authority.
- [A plain HTTP non-loopback bind exposes an unused proof to an on-path observer] → The existing transport limit of the local-control proof applies. The single-use nonce limits the window to one read.
- [The replay cache is shared by both operations] → Nonces are 128-bit random values, so the operations cannot collide. A full cache fails closed for both operations.
- [A daemon restart between callback and read loses the result] → The operator runs the OAuth flow again.
