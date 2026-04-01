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

### ~~Exchange endpoint integration test timing~~ (RESOLVED)
- **Source:** RALPH run 20260401-171023, review after iteration 15 (finding #5)
- **Resolution:** Integration tests added in `PairingExchangeEndpointTests.cs` covering 200/400/401 responses, code consumption semantics, token authentication, expiry, and single-use enforcement.
- **Date resolved:** 2026-04-01
