## 1. Protocol adapter and lease

- [x] 1.1 Add the modern and legacy catalog notification profiles to the MCP runtime adapter.
- [x] 1.2 Add a generation-owned notification lease with bounded signal coalescence and `TimeProvider` timeout support.
- [x] 1.3 Install handlers before client creation and establish modern acknowledgement or legacy capability support.

## 2. Lifecycle and catalog refresh

- [x] 2.1 Attach one required lease to each MCP client candidate and published snapshot.
- [x] 2.2 Activate the lease after publication and dispose it before its client.
- [x] 2.3 Reuse one refresh transaction for poll and notification paths with changed, unchanged, and failed results.
- [x] 2.4 Keep poll repair, last-good retention, generation rules, and safe structured logs.

## 3. Automated proof

- [x] 3.1 Test modern tool and prompt delivery, partial acknowledgement, method failure, timeout, and listener closure.
- [x] 3.2 Test legacy direct delivery, unsupported capabilities, and the absence of a listen request.
- [x] 3.3 Test duplicate coalescence, pre-publication delivery, generation behavior, and failed refresh retention.
- [x] 3.4 Test reconnect renewal, stale lease rejection, shutdown cleanup, and poll repair.
- [x] 3.5 Add an SDK-level test for the raw listen request and matching acknowledgement.

## 4. Product and operator guidance

- [x] 4.1 Update PRD-006 with MCP catalog notification requirements and acceptance criteria.
- [x] 4.2 Update the `netclaw-operations` system skill and increase its metadata version.

## 5. Validation and cleanup

- [x] 5.1 Run restore, focused tests, full solution tests, and the MCP setup smoke scenario.
- [ ] 5.2 Run the behavioral eval suite, Slopwatch, header verification, and strict OpenSpec validation.
- [x] 5.3 Run CRAP analysis and remove duplicate code or unnecessary moving parts.
