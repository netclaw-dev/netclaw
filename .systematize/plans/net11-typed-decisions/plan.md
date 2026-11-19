# Netclaw runtime quality stack

This file retains the scope, decisions, and verification receipts for four dependent PRs.
The [original execution plan](https://github.com/netclaw-dev/netclaw/blob/85eb57348/.systematize/plans/net11-typed-decisions/plan.md) remains in Git history.
The [decision trail](decisions.tsv) records material choices and failed checks.

## Scope and authority

Use .NET 11 and C# 15. Consolidate internal authorization outcomes and extract two local retry policies.
Preserve public authorization contracts, authority, configuration, journal records, snapshots, and transport behavior.
Keep the current actor runtime. Add no Akka.Durable dependency, generic decider framework, or new journal event.

The user authorized implementation and four stacked PRs through the installed `github/gh-stack` version `0.1.0`.
The user accepted focused verification in place of the blanket ten-live-lane template.
The original delivery goal excludes merges and deployments. The user later authorized a merge after final verification.
Complete the verified handoff first. Treat the later merge authority as a separate action. Do not deploy or release.

The dirty primary checkout stays at `76071cfe9e721dee864d60d1379a497d97f19414` with its existing untracked plan intact.
The parent owns publication. Each source owner used a separate checkout or exclusive write scope.
API operations resolve `netclaw-dev/netclaw`; pushes use `upstream` for that repository.
No published branch was renamed or rewritten. Earlier repairs used normal fast-forward pushes.
Unpublished children used `gh stack rebase --no-trunk`. No operation advanced the primary checkout's `dev` branch.

## Stack receipts

Native GitHub stack `2801` contains the four PRs in this order.
The final documentation commit follows the PR4 source candidate. The final handoff records its exact SHA.

| Slice | PR | Published source head | Base |
|---|---|---|---|
| Runtime | [2799](https://github.com/netclaw-dev/netclaw/pull/2799) | `4bb384446d3d61f186e494e2c067c693d3b3ffa8` | `dev` at `76071cfe9e721dee864d60d1379a497d97f19414` |
| Authorization | [2800](https://github.com/netclaw-dev/netclaw/pull/2800) | `9b2c84e5f18f562cd0c68840c2b2dae3f2228278` | PR1 at `4bb384446d3d61f186e494e2c067c693d3b3ffa8` |
| Delivery | [2802](https://github.com/netclaw-dev/netclaw/pull/2802) | `48b897c19d8cad1732a15414b4491b67927a0224` | PR2 at `9b2c84e5f18f562cd0c68840c2b2dae3f2228278` |
| Gateway | [2803](https://github.com/netclaw-dev/netclaw/pull/2803) | `07a2d68e3bd382db27ed254cfb8970c25311bf98` | PR3 at `48b897c19d8cad1732a15414b4491b67927a0224` |

The parent ran the actual init, add, submit, view, and local rebase commands.
`gh stack submit --auto --open --remote upstream` created each ready PR. The `--auto` flag generates PR text; it does not enable automatic merge.
The parent replaced generated bodies, preserved the extension footer, and applied the existing repository labels.
The parent verified each remote head and predecessor after submission.

## Runtime evidence

SDK `11.0.100` and C# `15` apply to the supported projects. The source generator retains `netstandard2.0`.
The upgrade preserves ordinal character semantics and adds the required explicit console cancellation token.
A shared configuration-file guard preserves rejection of broken optional-file links under the new JSON provider.
Aspire receives its required `13.6.5` patch. The shared package property remains in place.
The .NET 11 x64 binaries require `x86-64-v2`. Linux Arm64 keeps its existing hardware minimum.

The local Release build passes with zero errors and one ASPIRE010 warning.
The Linux non-NativeShell suite passes 16,235 cases and skips 99 platform or opt-in cases across 14 assemblies.
Both standalone tools build. All nine selected configuration mutants die.
An unsafe link-check inversion fails all three linked regression rows. The restored source passes.
Incrementalist exits 232 under both SDK 10 and SDK 11. This pre-existing result is not a passed check.

Candidate `db9b20a4756ac557c1ab5273e2972e5885ed8b04` supplies the local binaries, image, and database receipts.
The image reaches healthy state and retains state owner `1654:1654`.
All 29 native tapes and 10 scenarios pass their semantic checks.
The original outer shell exits 143 because cleanup matches its command line. A corrected file launcher passes a teardown check with exit zero.
The harness removes its private homes and services. The smoke model proves no model recall.

Released .NET 10 binary `0.28.3`, source `584023bda9ab4a30c7f3f6d8fbd0a7091b5a88ae`, creates the upgrade database.
The candidate restores its untouched latest snapshot. A second case selects an older snapshot and replays the title journal tail.
Both retain all four old serialized history messages and complete the second turn once.
Existing recovery fixtures cover pending work and authority. These native database cases do not replace those fixtures.
The final PR1 head adds only nine ordinal CLI comparisons after the local candidate.
All five current-head workflows pass, including the three test hosts, Linux and macOS native smoke, Docker, and Mattermost.
A prior macOS watcher assertion passes on retry. Its original callback remains unidentified; PR3 isolates the test event source.

## Authorization evidence

Internal producers and consumers use one closed family with four cases. The terminal DTO conversion disappears.
The public `ToolAuthorizationDecision` record stays byte-identical to PR1 and retains its generated record semantics.
The executor exception boundary, rule order, grant evidence, shell trace, and launch authorization stay intact.
The call-local consent candidate retains temporary-path advice until grant coverage resolves it.
The paired covered and uncovered structured-file cases prove that order.

The focused actor suite passes 1,185 cases and skips three platform cases. The linked fixture passes 118 cases.
A further 101 cases cover serializer fixtures, deployed V2 consent, legacy coordination, SQLite, child consent, and abandonment.
A precompiled public consumer produces identical output after the assembly swap.
A temporary fifth case causes five actual production switches to fail with `CS8509`. The original source bytes are restored.
The focused gates kill three order mutants, three authority mutants, and two evidence mutants.
The pinned Stryker 5.0 compiler uses Preview only for mutation compilation. Product projects retain C# 15.

The full corpus compares 55,479 shell inputs and 1,646,555 decisions per side against PR1.
The official comparison reports zero differences across every emitted decision field.
Both raw outputs have SHA-256 `9c1a979bc1f0480dd3abb67d7e5e16597ad5253efd9421adebebca2a6afa912f`.
The original launcher exits 143 while the runner continues. A fresh official comparison of the retained outputs exits zero.
The preserved gzip files pass integrity and raw-hash checks. The direction check reports zero transitions across 639 rows.
Candidate `5d5372a92c30df67e43edf8da1be0638c6dc770b` passes Linux publication and both affected native tapes.
Its source tree is `3df7cfd614ff7914f9b7a768999bd68cdd653997`.

The final selector repair changes only two stale result-type markers and plan receipts. Its production tree stays unchanged.
Both normalized selectors match the original spans. Counts remain 33 killed plus two documented duplicate removals, and nine killed.
All four applicable current-head workflows pass. Hosted validation run `44176201032` proves the repaired exact-mutant gates.

## Delivery and race evidence

The existing handler returns phase-specific Ready and Busy actions. The actor retains state, counters, nudges, and effects.
Ready stale feedback returns before a reminder report. Busy feedback preserves the existing report rule.
Routed replies retain child ownership. The retry budget stays at two. Journal and snapshot representations do not change.

The focused suite passes 138 cases. The linked fixture passes 45 cases.
The former Unknown actor case and its direct replacement both reject the same unsafe retry classification.
The restored source passes 46 direct and retained transport cases.
The reminder gate kills 16 selected mutants. The combined gate kills 75 selected mutants.
A separate stale-turn inversion fails its earlier-turn case. All four restored stale-turn cases pass.
Candidate `3a2baecaa345c0a3d49dd83a4426cbbc593a4fd9` passes both affected native tapes.
The reminder tape proves command admission; actor fixtures prove failure effects and recovery.
The policy migration removes one equivalent actor fixture. Later race tests are separate additions.

The operator's local `analyze-racy-test` workflow supplies Akka role analysis first and concurrency role analysis second.
The named profiles are unavailable. Separate generic delegates use the exact local role prompts.
Windows job `134382355152` records the third execution and a fixture read that blocks the history append handle.
Four fixture polls now query the manager. The actor serializes its history query with append and settlement.
A gated Nack test proves that the query replies after settlement with the failed row and failure count.
It proves actor order; it does not reproduce Windows file sharing on Linux.
The focused reminder repair passes 179 cases. The current Windows actor and daemon suites pass 5,627 and 1,758 cases.
The serial native shell case also passes.

The watcher fixture previously mixed manual events with native callbacks. The historical macOS callback remains unidentified.
The actor receives a required subscription factory. The host supplies native events, and the actor fixture controls its event source.
Startup reconciliation, debounce, restart admission, native filters, error callbacks, and disposal retain their behavior.
The focused repair passes 41 cases, including three real-host startup-gap cases. The linked fixture passes 13 cases.
The exact startup hash inversion is killed. Unrelated compiler artifacts remain outside the selected mutation span.
The current macOS actor and daemon suites pass 7,319 and 1,778 cases. No test hang occurs.
Current validation run `44189798044` passes all 27 applicable jobs. Native smoke run `44189797981` passes all five jobs.

## Gateway evidence

`GatewayRetryPolicy` calculates a retry schedule and a delay after Ready. The actor retains lifecycle ownership.
The sequence starts at zero, then uses five seconds and doubles to the five-minute cap.
Ready resets the delay at 60 seconds. A future timestamp preserves the delay.
The actor retains the nullable timestamp guard, conditional clock read, attempt identity, replies, timers, subscriptions, and transport effects.

The focused suite passes 63 cases. The linked fixture passes 12 cases.
The stale-start control fails the intended readiness assertion. The combined disconnect control exposes an extra start on both transports.
This disconnect control tests both guards together. It does not prove that either redundant guard alone is necessary.
The restored source passes all three control cases. The focused Stryker gate kills both stability comparison mutants.
Four timing actor instances become two combined instances and one new stale-start instance.
The change provides modest testability value. It proves no runtime speedup or broad complexity reduction.

Candidate `c03cdc94f` passes the native channel configuration tape and daemon lifecycle scenario.
Those checks exercise no live external gateway connection. Actor contracts cover transport and timer effects.
After the restack onto PR3 repair `48b897c19`, all 11 code, test, and gate blobs match the tested candidate.
Separate PR [2795](https://github.com/netclaw-dev/netclaw/pull/2795) remains open at `5384367585306f34d56295e996d73cf4e811a76e`.
This base contains no transport stop deadline. The PR claims no such deadline.

Hosted [job 134419950222](https://github.com/netclaw-dev/netclaw/actions/runs/44190971755/job/134419950222) passes all three gates.
It kills 75 child-owner mutants, two chat-close mutants, and both gateway mutants.
The job takes 11 minutes 12 seconds. Its gate step takes 10 minutes 54 seconds.
This single observation fits the 15-minute target and 25-minute timeout. It is not a controlled performance comparison.
Validation run `44190971755` passes all 27 applicable jobs. Native smoke run `44190971785` passes all five jobs.

## Final verification

Independent behavior and compatibility reviews accept the source slices. Four separate shipping reviewers return PASS+NOTES.
Each reviewer reads the complete published diff and retained real-artifact receipts. No shipping reviewer claims a fresh native execution.

| PR | Independent reviewer | Stable source-candidate patch ID |
|---|---|---|
| 2799 | `ship_runtime_review` | `8c468190fadaae8235da22d43c20e00a275d7d7b` |
| 2800 | `ship_authorization_review` | `7962e152f5d55088e9a6a8136471ea8381964da3` |
| 2802 | `ship_delivery_review` | `00ef8fac7a976d5a3d408e5cad564e0a6a714a7e` |
| 2803 | `ship_gateway_review` | `764667cc0a7de682a16573e10a8a506cca53ebbd` |

The source-candidate heads and bases appear in the stack table. A final documentation revision changes the PR4 full patch identity.
The final audit must confirm that all product, test, and gate blobs stay unchanged and review the documentation diff.
These are local source verdicts, not GitHub owner approvals or merge permission.
Deslop is unavailable. Direct cleanup checks cover dead paths, duplicate constructs, comments, and unexpected file changes.
No alternate-vendor judge is available. The report claims no such review.
Slopwatch reports zero issues. Copyright headers, whitespace checks, and strict OpenSpec validation pass on the combined tree.
The published stack adds 50 production C# lines and 317 test C# lines. Those counts exclude this final documentation revision.
The authorization slice removes duplicate representation. The retry slices provide small test seams with modest structural savings.
No measured reduction in total test duration exists.

### OpenSpec verification report

| Dimension | Source-candidate result |
|---|---|
| Completeness | All 15 implementation tasks are complete. The four source-candidate hosted gate sets pass. |
| Correctness | Corpus, public consumer, fixed-byte fixtures, replay, negative controls, exact mutants, and native checks support the preserved contracts. |
| Coherence | Existing actors retain state and effects. Local pure policies replace the selected branches. No framework or persistence model is added. |

The change adds no capability or delta specification. Its metadata intentionally skips new specs.
OpenSpec reports all 15 tasks complete. Strict validation passes before the archive; no specification sync is required.
The proposal links the preserved PRD and capability requirements. The implementation changes no stated product policy.
The report finds no unresolved source defect or contract divergence. Existing proof limits remain explicit above.
All four source heads pass their applicable workflows. PR1, PR3, and PR4 each have 41 successful checks and one expected skip.
PR2 has 40 successful checks and one expected skip. All four independent patch identities match the audited remote refs.
The final documentation successor still needs fresh exact-head checks before handoff or merge.

Final publication and handoff use this procedure:

- Push only the final documentation update to PR4. Verify its exact head and unchanged source blobs.
- Obtain fresh hosted checks at that final head. Retain the exact-head results in the final handoff receipt.
- Audit heads, predecessors, labels, readiness, review threads, native stack membership, and automatic merge state.
- Hand the four verified PR links and exact head receipts to the user before the separately authorized merge action.

The final handoff receipt records execution of this procedure. The archived task file records the 15 completed implementation tasks.

The local raw receipts stay under `/tmp/netclaw-quality-artifacts-20261117`; shipping verdicts use `/tmp/netclaw-quality-shipping-20261117`.
The archived [OpenSpec change](../../../openspec/changes/archive/2026-11-17-simplify-runtime-decisions/proposal.md) links the preserved PRD and capability requirements.
Use [the engineering glossary](../../../docs/spec/GLOSSARY.md) and [tool authorization](../../../docs/architecture/tool-authorization.md) for shared contracts.
The evidence does not constitute a release or native execution on every release target.
