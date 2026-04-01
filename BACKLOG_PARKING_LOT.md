# Backlog Parking Lot

> Items parked here need a human decision before they can be worked on.
> RALPH loops do NOT pick up items from this file — only from `IMPLEMENTATION_PLAN.md`.
>
> Each item includes: what it is, where it came from, and what decision is needed.

---

## Items Awaiting Decision

<!-- Add parked items below. Example:

### Extract hardcoded brand colors to config
- **Source:** RALPH run 20260201-143022, iteration 5
- **Issue:** Brand colors are hardcoded in 3 CSS files. Should they be CSS variables or config?
- **Decision needed:** Design decision — CSS custom properties vs theme config file
- **Date parked:** 2026-02-01

-->

### Exchange endpoint integration test timing
- **Source:** RALPH run 20260401-171023, review after iteration 15 (finding #5)
- **Issue:** `POST /api/pair/exchange` is a new HTTP endpoint added in M7.C2 that coordinates PairingCodeService + DeviceRegistry + rate limiter. Per testing strategy, new endpoints should have integration tests in the same iteration. Currently only unit tests exist for the underlying service. Task 10.4 in `device-pairing/tasks.md` plans a full integration test ("generate code → exchange → connect with token → authenticated session") but this is deferred to a later task.
- **Decision needed:** Should M7.C2 retroactively add a focused integration test for the exchange endpoint HTTP behavior (400/401/200 responses, rate limiting), or is the planned full-flow integration test in task 10.4 sufficient?
- **Date parked:** 2026-04-01
