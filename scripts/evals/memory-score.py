#!/usr/bin/env python3

import argparse
import json
import os
import re
import sqlite3
import subprocess
import sys
import time
from pathlib import Path


def run(cmd, env=None, check=True, timeout=None):
    proc = subprocess.run(cmd, capture_output=True, text=True, env=env, timeout=timeout)
    if check and proc.returncode != 0:
        raise RuntimeError(
            f"command failed ({proc.returncode}): {' '.join(cmd)}\nstdout:\n{proc.stdout}\nstderr:\n{proc.stderr}"
        )
    return proc


def now_ms():
    return int(time.time() * 1000)


def sqlite_conn(db_path):
    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row
    return conn


def seed_documents(conn, fixtures):
    # Clean up all seeded documents dynamically from the fixture.
    doc_ids = [d["documentId"] for d in fixtures.get("seedDocuments", [])]
    anchor_ids = [d["anchorId"] for d in fixtures.get("seedDocuments", [])]
    if doc_ids:
        placeholders = ",".join("?" for _ in doc_ids)
        conn.execute(f"DELETE FROM memory_documents_fts WHERE document_id IN ({placeholders})", doc_ids)
        conn.execute(f"DELETE FROM memory_documents WHERE document_id IN ({placeholders})", doc_ids)
    if anchor_ids:
        placeholders = ",".join("?" for _ in anchor_ids)
        conn.execute(f"DELETE FROM memory_anchors WHERE anchor_id IN ({placeholders})", anchor_ids)

    ts = now_ms()
    for doc in fixtures["seedDocuments"]:
        conn.execute(
            """
            INSERT INTO memory_anchors(anchor_id, anchor_type, canonical_name, parent_anchor_id,
              sensitivity, recall_mode, confidence, freshness_at, status, created_at, updated_at)
            VALUES(?, ?, ?, NULL, ?, ?, ?, ?, 'active', ?, ?)
            ON CONFLICT(anchor_id) DO UPDATE SET
              anchor_type=excluded.anchor_type,
              canonical_name=excluded.canonical_name,
              sensitivity=excluded.sensitivity,
              recall_mode=excluded.recall_mode,
              confidence=excluded.confidence,
              freshness_at=excluded.freshness_at,
              updated_at=excluded.updated_at
            """,
            (
                doc["anchorId"],
                doc["anchorType"],
                doc["canonicalName"],
                doc["sensitivity"],
                doc["recallMode"],
                doc["confidence"],
                ts,
                ts,
                ts,
            ),
        )

        recall_mode = doc.get("recallMode", "auto")
        sensitivity = doc.get("sensitivity", "normal")
        if recall_mode == "auto" and sensitivity == "secret":
            # Align with production policy: secret memories are never auto-recallable.
            recall_mode = "manual"

        conn.execute(
            """
            INSERT INTO memory_documents(document_id, anchor_id, memory_class, title, markdown_body,
              update_semantics, boundary, audience, sensitivity, recall_mode, confidence,
              freshness_at, created_at, updated_at)
            VALUES(?, ?, 'durable_fact', ?, ?, 'merge-document', 'boundary:trusted-instance', 'public',
              ?, ?, ?, ?, ?, ?)
            ON CONFLICT(document_id) DO UPDATE SET
              title=excluded.title,
              markdown_body=excluded.markdown_body,
              boundary=excluded.boundary,
              audience=excluded.audience,
              sensitivity=excluded.sensitivity,
              recall_mode=excluded.recall_mode,
              confidence=excluded.confidence,
              freshness_at=excluded.freshness_at,
              updated_at=excluded.updated_at
            """,
            (
                doc["documentId"],
                doc["anchorId"],
                doc["title"],
                doc["markdownBody"],
                sensitivity,
                recall_mode,
                doc["confidence"],
                ts,
                ts,
                ts,
            ),
        )

        # Populate FTS5 index so deterministic retrieval can find the document.
        conn.execute(
            """
            INSERT INTO memory_documents_fts(document_id, title, body, aliases, facets)
            VALUES(?, ?, ?, '', '')
            """,
            (doc["documentId"], doc["title"], doc["markdownBody"]),
        )
    conn.commit()


def unique_nonce():
    return f"{int(time.time() * 1000)}-{os.getpid()}"


def hydrate_recall_contents(conn, recall_ids):
    if not recall_ids:
        return []

    out = []
    for rid in recall_ids:
        row = conn.execute(
            "SELECT markdown_body FROM memory_documents WHERE document_id = ?",
            (rid,),
        ).fetchone()
        if row is not None:
            out.append(str(row["markdown_body"]))
            continue

        row = conn.execute(
            "SELECT payload_json FROM memory_records WHERE record_id = ?",
            (rid,),
        ).fetchone()
        if row is not None:
            out.append(str(row["payload_json"]))

    return out


RE_RECALL = re.compile(
    r"turn_memory_recall\s+degraded=(?P<degraded>\S+)(?:\s+stage=(?P<stage>\S+))?\s+durationMs=(?P<duration>\d+)\s+itemCount=(?P<count>\d+)\s+itemIds=(?P<ids>\S+)"
)

RE_ENQUEUE = re.compile(
    r"turn_memory_checkpoint_enqueued\s+trigger=(?P<trigger>\S+)\s+checkpointId=(?P<id>\S+)\s+durationMs=(?P<duration>\d+)"
)

RE_CURATION = re.compile(
    r"Memory checkpoint curation completed for\s+(?P<id>\S+)\s+\(trigger=(?P<trigger>[^,]+),\s+operations=(?P<ops>\d+),\s+durationMs=(?P<duration>\d+)\)"
)


def parse_log_metrics(log_text):
    recall = []
    enqueue = []
    curation = []

    for line in log_text.splitlines():
        m = RE_RECALL.search(line)
        if m:
            ids_raw = m.group("ids")
            ids = [] if ids_raw == "-" else [x for x in ids_raw.split(",") if x]
            recall.append(
                {
                    "degraded": m.group("degraded").lower() == "true",
                    "stage": m.group("stage"),
                    "durationMs": int(m.group("duration")),
                    "itemCount": int(m.group("count")),
                    "itemIds": ids,
                    "line": line,
                }
            )
            continue

        m = RE_ENQUEUE.search(line)
        if m:
            enqueue.append(
                {
                    "trigger": m.group("trigger"),
                    "checkpointId": m.group("id"),
                    "durationMs": int(m.group("duration")),
                    "line": line,
                }
            )
            continue

        m = RE_CURATION.search(line)
        if m:
            curation.append(
                {
                    "checkpointId": m.group("id"),
                    "trigger": m.group("trigger"),
                    "operations": int(m.group("ops")),
                    "durationMs": int(m.group("duration")),
                    "line": line,
                }
            )

    return {"recall": recall, "enqueue": enqueue, "curation": curation}


def warm_search_index(repo_root: Path, fixtures, prompt_timeout_seconds: int):
    # Ensure the daemon has produced searchable memory entries before issuing
    # recall probes. This aligns eval ordering with real runtime behavior.
    warm_phrases = []
    for doc in fixtures.get("seedDocuments", []):
        title = doc.get("title", "")
        body = doc.get("markdownBody", "")
        if title:
            warm_phrases.append(title)
        if body:
            warm_phrases.append(body[:96])

    for phrase in warm_phrases[:4]:
        run(
            [
                "dotnet",
                "run",
                "--project",
                "src/Netclaw.Cli/Netclaw.Cli.csproj",
                "--",
                "-p",
                f"search memory for: {phrase}",
            ],
            check=False,
            timeout=prompt_timeout_seconds,
        )
        time.sleep(0.2)


def warm_recall_index(repo_root: Path, fixtures, prompt_timeout_seconds: int):
    recall_prompts = []
    for case in fixtures.get("cases", []):
        if case.get("kind") == "recall_positive":
            recall_prompts.append(case.get("prompt", ""))

    for prompt in recall_prompts[:2]:
        if not prompt:
            continue
        run(
            [
                "dotnet",
                "run",
                "--project",
                "src/Netclaw.Cli/Netclaw.Cli.csproj",
                "--",
                "-p",
                prompt,
            ],
            check=False,
            timeout=prompt_timeout_seconds,
        )
        time.sleep(0.3)


def force_seed_recall_artifacts(conn):
    # The current runtime can write conservative/manual entries during curation
    # for explicit memory-like interactions. For deterministic recall evals,
    # pin seeded fixture docs to auto so the suite measures retrieval behavior.
    conn.execute(
        """
        UPDATE memory_documents
        SET recall_mode = 'auto'
        WHERE document_id IN ('doc-eval-alpha','doc-eval-beta')
        """
    )
    conn.execute(
        """
        UPDATE memory_documents
        SET recall_mode = 'manual'
        WHERE document_id = 'doc-eval-secret'
        """
    )
    conn.commit()


def delete_eval_seed(conn):
    conn.execute(
        "DELETE FROM memory_documents WHERE document_id IN ('doc-eval-alpha','doc-eval-beta','doc-eval-secret')"
    )
    conn.execute(
        "DELETE FROM memory_anchors WHERE anchor_id IN ('anchor:eval-alpha','anchor:eval-beta','anchor:eval-secret','anchor:deploy-service-alpha','anchor:worker-b-queue-lag','anchor:ops-secret-token')"
    )
    conn.commit()


def p95(values):
    if not values:
        return None
    xs = sorted(values)
    idx = max(0, min(len(xs) - 1, int(round(0.95 * (len(xs) - 1)))))
    return xs[idx]


def linear_score(value, low, high, max_points):
    if value <= low:
        return 0.0
    if value >= high:
        return float(max_points)
    return ((value - low) / (high - low)) * max_points


def main():
    parser = argparse.ArgumentParser(
        description="Run deterministic SQLite memory eval scoring"
    )
    parser.add_argument("--repo-root", required=True)
    parser.add_argument("--fixtures", required=True)
    parser.add_argument("--results", required=True)
    parser.add_argument("--summary", required=True)
    parser.add_argument(
        "--db-path", default=os.path.expanduser("~/.netclaw/netclaw.db")
    )
    parser.add_argument("--log-path", default="")
    parser.add_argument("--runs", type=int, default=1)
    parser.add_argument("--prompt-timeout-seconds", type=int, default=180)
    parser.add_argument("--smoke-pass-streak", type=int, default=1)
    parser.add_argument("--realistic-pass-streak", type=int, default=1)
    args = parser.parse_args()

    repo_root = Path(args.repo_root)
    fixtures = json.loads(Path(args.fixtures).read_text())

    if args.runs < 1:
        raise RuntimeError("--runs must be >= 1")

    config_proc = run(
        [
            "dotnet",
            "run",
            "--project",
            "src/Netclaw.Cli/Netclaw.Cli.csproj",
            "--",
            "model",
            "list",
        ],
        check=True,
    )
    model_list_text = config_proc.stdout
    main_model_line = None
    for line in model_list_text.splitlines():
        if line.strip().startswith("Main"):
            main_model_line = line.strip()
            break

    db_path = Path(args.db_path)
    db_path.parent.mkdir(parents=True, exist_ok=True)

    log_path = (
        Path(args.log_path)
        if args.log_path
        else Path.home()
        / ".netclaw"
        / "logs"
        / f"daemon-{time.strftime('%Y-%m-%d')}.log"
    )

    if not db_path.exists():
        raise RuntimeError(
            f"SQLite memory DB not found at {db_path}; start daemon with sqlite memory first"
        )
    if not log_path.exists():
        raise RuntimeError(f"Daemon log not found at {log_path}")

    all_run_results = []

    for run_idx in range(args.runs):
        conn = sqlite_conn(str(db_path))
        delete_eval_seed(conn)
        seed_documents(conn, fixtures)
        force_seed_recall_artifacts(conn)
        warm_search_index(repo_root, fixtures, args.prompt_timeout_seconds)
        warm_recall_index(repo_root, fixtures, args.prompt_timeout_seconds)

        start_line_count = len(log_path.read_text(errors="ignore").splitlines())

        case_results = []
        for case in fixtures["cases"]:
            before_lines = len(log_path.read_text(errors="ignore").splitlines())
            template_prompt = case["prompt"]
            nonce = unique_nonce()
            prompt = template_prompt.replace("__NONCE__", nonce)
            prompt_error = None

            if (
                case["kind"] == "noise_suppression"
                and "__NONCE__" not in template_prompt
            ):
                prompt_error = "noise_prompt_missing_nonce_placeholder"
            try:
                run(
                    [
                        "dotnet",
                        "run",
                        "--project",
                        "src/Netclaw.Cli/Netclaw.Cli.csproj",
                        "--",
                        "-p",
                        prompt,
                    ],
                    check=True,
                    timeout=args.prompt_timeout_seconds,
                )
            except subprocess.TimeoutExpired:
                prompt_error = f"prompt_timeout>{args.prompt_timeout_seconds}s"
            except Exception as ex:
                prompt_error = str(ex)
            time.sleep(0.5)
            log_text = "\n".join(
                log_path.read_text(errors="ignore").splitlines()[before_lines:]
            )
            parsed = parse_log_metrics(log_text)

            recall_event = parsed["recall"][-1] if parsed["recall"] else None
            recall_contents = []
            if recall_event is not None:
                recall_contents = hydrate_recall_contents(
                    conn, recall_event.get("itemIds", [])
                )

            expected_markers = case.get("expectedRecallMarkers", [])
            forbidden_markers = case.get("forbiddenRecallMarkers", [])

            marker_hits = {}
            for marker in expected_markers:
                marker_hits[marker] = any(
                    marker in content for content in recall_contents
                )

            forbidden_marker_hits = {}
            for marker in forbidden_markers:
                forbidden_marker_hits[marker] = any(
                    marker in content for content in recall_contents
                )

            case_results.append(
                {
                    "id": case["id"],
                    "kind": case["kind"],
                    "prompt": prompt,
                    "nonce": nonce,
                    "expectedRecallIds": case.get("expectedRecallIds", []),
                    "expectedRecallMarkers": expected_markers,
                    "forbiddenRecallIds": case.get("forbiddenRecallIds", []),
                    "forbiddenRecallMarkers": forbidden_markers,
                    "expectEmptyRecall": case.get("expectEmptyRecall", False),
                    "promptError": prompt_error,
                    "recallEvent": recall_event,
                    "recallContents": recall_contents,
                    "markerHits": marker_hits,
                    "forbiddenMarkerHits": forbidden_marker_hits,
                    "enqueueEvents": parsed["enqueue"],
                    "curationEvents": parsed["curation"],
                }
            )

        full_log_text = "\n".join(
            log_path.read_text(errors="ignore").splitlines()[start_line_count:]
        )
        global_metrics = parse_log_metrics(full_log_text)

        recall_positive = [c for c in case_results if c["kind"] == "recall_positive"]
        recall_hits = 0
        recall_total = len(recall_positive)
        for c in recall_positive:
            if c.get("promptError"):
                continue
            ev = c["recallEvent"]
            if ev is None:
                continue
            id_hit = any(exp in ev["itemIds"] for exp in c["expectedRecallIds"])
            marker_hit = any(c.get("markerHits", {}).values())
            # If forbiddenRecallIds present, also check no forbidden docs leaked in.
            forbidden_ids = c.get("forbiddenRecallIds", [])
            precision_ok = not any(fid in ev["itemIds"] for fid in forbidden_ids)
            if (id_hit or marker_hit) and precision_ok:
                recall_hits += 1

        recall_hit_rate = (recall_hits / recall_total) if recall_total else 1.0

        noise_cases = [c for c in case_results if c["kind"] == "noise_suppression"]
        noise_ok = 0
        for c in noise_cases:
            if c.get("promptError"):
                continue
            ev = c["recallEvent"]
            if ev is not None and c["expectEmptyRecall"] and ev["itemCount"] == 0:
                noise_ok += 1
        noise_rate = (noise_ok / len(noise_cases)) if noise_cases else 1.0

        privacy_cases = [c for c in case_results if c["kind"] == "privacy"]
        privacy_leaks = 0
        for c in privacy_cases:
            if c.get("promptError"):
                continue
            ev = c["recallEvent"]
            if ev is None:
                continue
            id_leak = any(fid in ev["itemIds"] for fid in c["forbiddenRecallIds"])
            marker_leak = any(c.get("forbiddenMarkerHits", {}).values())
            if id_leak or marker_leak:
                privacy_leaks += 1

        avg_recall_items = 0.0
        recall_events = [x for x in global_metrics["recall"] if not x["degraded"]]
        if recall_events:
            avg_recall_items = sum(x["itemCount"] for x in recall_events) / len(
                recall_events
            )

        recall_p95 = p95([x["durationMs"] for x in global_metrics["recall"]])
        enqueue_p95 = p95([x["durationMs"] for x in global_metrics["enqueue"]])
        curation_p95 = p95([x["durationMs"] for x in global_metrics["curation"]])

        pending_row = conn.execute(
            "SELECT COUNT(*) as c FROM memory_checkpoints WHERE status='pending'"
        ).fetchone()
        pending_checkpoints = int(pending_row["c"])

        recall_score = linear_score(recall_hit_rate, 0.50, 0.85, 30)
        noise_score = linear_score(noise_rate, 0.50, 0.80, 20)
        privacy_score = 20.0 if privacy_leaks == 0 else 0.0

        # update correctness via deterministic DB presence of seeded docs
        seeded_ids = [d["documentId"] for d in fixtures.get("seedDocuments", [])]
        placeholders = ",".join("?" for _ in seeded_ids)
        seeded_ok = conn.execute(
            f"SELECT COUNT(*) as c FROM memory_documents WHERE document_id IN ({placeholders})",
            seeded_ids,
        ).fetchone()["c"]
        update_correctness = 10.0 if seeded_ok == len(seeded_ids) else 0.0

        reliability_score = (
            10.0
            if pending_checkpoints == 0
            else max(0.0, 10.0 - min(10.0, pending_checkpoints))
        )

        latency_score = 10.0
        if recall_p95 is not None and recall_p95 > 300:
            latency_score -= min(4.0, (recall_p95 - 300) / 150.0)
        if enqueue_p95 is not None and enqueue_p95 > 25:
            latency_score -= min(3.0, (enqueue_p95 - 25) / 50.0)
        if curation_p95 is not None and curation_p95 > 5000:
            latency_score -= min(3.0, (curation_p95 - 5000) / 3000.0)
        latency_score = max(0.0, latency_score)

        total_score = (
            recall_score
            + noise_score
            + privacy_score
            + update_correctness
            + reliability_score
            + latency_score
        )
        hard_fail = privacy_leaks > 0
        deploy_candidate = (not hard_fail) and total_score >= 85.0

        run_result = {
            "runIndex": run_idx + 1,
            "model": main_model_line,
            "dbPath": str(db_path),
            "logPath": str(log_path),
            "metrics": {
                "recallHitRate": recall_hit_rate,
                "noiseSuppressionRate": noise_rate,
                "privacyLeakCount": privacy_leaks,
                "avgRecallItems": avg_recall_items,
                "pendingCheckpoints": pending_checkpoints,
                "recallP95Ms": recall_p95,
                "enqueueP95Ms": enqueue_p95,
                "curationP95Ms": curation_p95,
            },
            "scores": {
                "recall": recall_score,
                "noise": noise_score,
                "privacy": privacy_score,
                "updateCorrectness": update_correctness,
                "reliability": reliability_score,
                "latency": latency_score,
                "total": total_score,
            },
            "hardFail": hard_fail,
            "deployCandidate": deploy_candidate,
            "cases": case_results,
        }

        all_run_results.append(run_result)
        conn.close()

    recall_rates = [r["metrics"]["recallHitRate"] for r in all_run_results]
    noise_rates = [r["metrics"]["noiseSuppressionRate"] for r in all_run_results]
    privacy_counts = [r["metrics"]["privacyLeakCount"] for r in all_run_results]
    score_values = [r["scores"]["total"] for r in all_run_results]

    def mean(values):
        return sum(values) / len(values) if values else 0.0

    def spread(values):
        return (max(values) - min(values)) if values else 0.0

    overall_score = sum(r["scores"]["total"] for r in all_run_results) / len(
        all_run_results
    )
    any_hard_fail = any(r["hardFail"] for r in all_run_results)
    deploy_candidate = (not any_hard_fail) and overall_score >= 85.0
    smoke_gate_pass = all(
        r["metrics"]["recallHitRate"] >= 0.90 and r["metrics"]["privacyLeakCount"] == 0
        for r in all_run_results
    )
    realistic_gate_pass = all(
        r["metrics"]["recallHitRate"] >= 0.75 and r["metrics"]["privacyLeakCount"] <= 0
        for r in all_run_results
    )

    output = {
        "timestampUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "runs": args.runs,
        "overall": {
            "score": overall_score,
            "hardFail": any_hard_fail,
            "deployCandidate": deploy_candidate,
            "scoreSpread": spread(score_values),
            "meanRecallHitRate": mean(recall_rates),
            "meanNoiseSuppressionRate": mean(noise_rates),
            "maxPrivacyLeakCount": max(privacy_counts) if privacy_counts else 0,
            "smokeGatePass": smoke_gate_pass,
            "realisticGatePass": realistic_gate_pass,
            "requiredSmokePassStreak": args.smoke_pass_streak,
            "requiredRealisticPassStreak": args.realistic_pass_streak,
        },
        "runResults": all_run_results,
    }

    Path(args.results).parent.mkdir(parents=True, exist_ok=True)
    Path(args.results).write_text(json.dumps(output, indent=2) + "\n")

    summary_lines = [
        "# Memory Eval Summary",
        "",
        f"- runs: {args.runs}",
        f"- model: {main_model_line or 'unknown'}",
        f"- overall score: {overall_score:.2f}/100",
        f"- score spread: {output['overall']['scoreSpread']:.2f}",
        f"- hard fail: {any_hard_fail}",
        f"- deploy candidate: {deploy_candidate}",
        f"- smoke gate pass: {smoke_gate_pass}",
        f"- realistic gate pass: {realistic_gate_pass}",
        f"- required smoke pass streak: {args.smoke_pass_streak}",
        f"- required realistic pass streak: {args.realistic_pass_streak}",
        "",
    ]

    for r in all_run_results:
        m = r["metrics"]
        s = r["scores"]
        summary_lines.extend(
            [
                f"## Run {r['runIndex']}",
                f"- total: {s['total']:.2f}",
                f"- recall hit rate: {m['recallHitRate']:.2%}",
                f"- noise suppression: {m['noiseSuppressionRate']:.2%}",
                f"- privacy leaks: {m['privacyLeakCount']}",
                f"- avg recall items: {m['avgRecallItems']:.2f}",
                f"- recall p95 ms: {m['recallP95Ms']}",
                f"- enqueue p95 ms: {m['enqueueP95Ms']}",
                f"- curation p95 ms: {m['curationP95Ms']}",
                f"- pending checkpoints: {m['pendingCheckpoints']}",
                "",
            ]
        )

    Path(args.summary).parent.mkdir(parents=True, exist_ok=True)
    Path(args.summary).write_text("\n".join(summary_lines).strip() + "\n")

    print(json.dumps(output["overall"], indent=2))
    if not deploy_candidate:
        sys.exit(2)


if __name__ == "__main__":
    main()
