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

### Extract shared ExposureMode parse/wire-value utility
- **Source:** RALPH run 20260401-171023, review after iteration 5 (finding #4)
- **Issue:** ExposureMode parsing and wire-value conversion logic is duplicated in 6 locations across 3 assemblies (`Netclaw.Configuration`, `Netclaw.Cli`, `Netclaw.Daemon`). The doctor check duplicates `ParseMode`/`ToWireValue` because `Netclaw.Cli` doesn't have `InternalsVisibleTo` for `Netclaw.Configuration`. Adding a new ExposureMode variant requires synchronized changes in all 6 locations.
- **Decision needed:** Should `DaemonConfig.ParseExposureMode()` and a canonical `ToWireValue()` be made `public` on the `Netclaw.Configuration` assembly so other assemblies can reuse them? Or create a new `ExposureModeSerializer` public utility type? Or accept the duplication given assembly boundary constraints?
- **Date parked:** 2026-04-01
