#!/usr/bin/env python3
"""Replay sanitized tool-loop facts against the beta.3 exact cycle detector."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import struct
import sys
from collections.abc import Callable
from pathlib import Path
from typing import Any, Mapping, Sequence


BASELINE_RELEASE = "0.27.0-beta.3"
BASELINE_SOURCE_SHA256 = "0e7a3d4b324040ba5aaeb38391090b1cd842a6a8f64856389b428ad52cb22789"
BASELINE_SOURCE_PATH = "src/Netclaw.Actors/Sessions/Handlers/TurnStateTracker.cs"
BASELINE_SOURCE_REVISION = "a29de869ef3a0117f6c30fc8547b2ed2362b2777"
SOURCE_BOUNDARY = "operator extracts symbolic facts before repository entry; raw session data remains local"
MAXIMUM_PERIOD = 3
MAXIMUM_HISTORY = MAXIMUM_PERIOD * 2
_TOKEN = re.compile(r"^[a-z][a-z0-9_-]{0,31}$")
_PROHIBITED_NAMES = frozenset(
    {
        "prompt",
        "raw_prompt",
        "response",
        "raw_response",
        "args",
        "arguments",
        "arguments_json",
        "path",
        "paths",
        "result",
        "raw_result",
        "call_id",
        "session_id",
        "user_id",
        "channel_id",
        "timestamp",
        "url",
        "credential",
        "token",
    }
)
_CALL_FIELDS = frozenset({"tool", "argument_shape", "outcome_category", "result_shape"})
_CASE_FIELDS = frozenset({"id", "session_group", "split", "label", "historical_request_count", "historical_exact_suffix_count", "events"})
_ROOT_FIELDS = frozenset({"schema_version", "baseline", "sanitization", "cases"})
_REQUIRED_PROHIBITED_FIELDS = frozenset(
    {"prompt", "raw_prompt", "response", "raw_response", "args", "arguments", "arguments_json", "path", "paths", "result", "raw_result", "call_id", "session_id", "user_id", "channel_id", "timestamp", "url", "credential", "token"}
)
_OUTCOME_ORDER = {"success": 0, "invalid_input": 1, "access_denied": 2, "not_found": 3, "transient_failure": 4, "recoverable_correction": 5}


class FixtureError(ValueError):
    """The fixture does not satisfy the sanitized corpus contract."""


def _require_object(value: Any, location: str) -> Mapping[str, Any]:
    if not isinstance(value, dict):
        raise FixtureError(f"{location} must be an object")
    return value


def _require_fields(value: Mapping[str, Any], expected: frozenset[str], location: str) -> None:
    unknown = set(value) - expected
    missing = expected - set(value)
    if unknown:
        raise FixtureError(f"{location} has unknown fields: {', '.join(sorted(unknown))}")
    if missing:
        raise FixtureError(f"{location} is missing fields: {', '.join(sorted(missing))}")
    if any(str(name).lower() in _PROHIBITED_NAMES for name in value):
        raise FixtureError(f"{location} has a prohibited raw-data field")


def _token(value: Any, location: str) -> str:
    if not isinstance(value, str) or not _TOKEN.fullmatch(value):
        raise FixtureError(f"{location} must be a lower-case sanitized token")
    return value


def _fact_token(value: Any, prefix: str, location: str) -> str:
    token = _token(value, location)
    if not re.fullmatch(re.escape(prefix) + r"[0-9]{4}", token):
        raise FixtureError(f"{location} must use the {prefix} symbolic namespace")
    return token


def validate_fixture(value: Any) -> dict[str, Any]:
    """Validate and return a detached sanitized fixture.

    The validator accepts symbolic facts only. It does not accept prompts,
    arguments, paths, result text, identifiers, timestamps, or credentials.
    """

    root = _require_object(value, "fixture")
    _require_fields(root, _ROOT_FIELDS, "fixture")
    if root["schema_version"] != 1:
        raise FixtureError("fixture.schema_version must be 1")

    baseline = _require_object(root["baseline"], "fixture.baseline")
    _require_fields(baseline, frozenset({"release", "detector", "source_path", "source_revision", "source_sha256", "maximum_period", "maximum_history"}), "fixture.baseline")
    if baseline["release"] != BASELINE_RELEASE or baseline["detector"] != "exact-action-outcome":
        raise FixtureError("fixture baseline is not the locked beta.3 detector")
    if baseline["source_path"] != BASELINE_SOURCE_PATH or baseline["source_revision"] != BASELINE_SOURCE_REVISION or baseline["source_sha256"] != BASELINE_SOURCE_SHA256:
        raise FixtureError("fixture baseline source fingerprint does not match beta.3")
    if baseline["maximum_period"] != MAXIMUM_PERIOD or baseline["maximum_history"] != MAXIMUM_HISTORY:
        raise FixtureError("fixture baseline limits do not match beta.3")

    sanitization = _require_object(root["sanitization"], "fixture.sanitization")
    _require_fields(sanitization, frozenset({"source_boundary", "prohibited_fields"}), "fixture.sanitization")
    if sanitization["source_boundary"] != SOURCE_BOUNDARY:
        raise FixtureError("fixture.sanitization.source_boundary is not the fixed local-only boundary")
    prohibited = sanitization["prohibited_fields"]
    if not isinstance(prohibited, list) or set(prohibited) != _REQUIRED_PROHIBITED_FIELDS:
        raise FixtureError("fixture.sanitization.prohibited_fields does not match the fixed deny list")

    cases = root["cases"]
    if not isinstance(cases, list) or not cases:
        raise FixtureError("fixture.cases must be a non-empty list")
    normalized_cases: list[dict[str, Any]] = []
    seen_ids: set[str] = set()
    for case_index, raw_case in enumerate(cases):
        case = _require_object(raw_case, f"fixture.cases[{case_index}]")
        _require_fields(case, _CASE_FIELDS, f"fixture.cases[{case_index}]")
        case_id = _fact_token(case["id"], "incident_" if str(case["id"]).startswith("incident_") else "control_", f"fixture.cases[{case_index}].id")
        if case_id in seen_ids:
            raise FixtureError(f"duplicate case id: {case_id}")
        seen_ids.add(case_id)
        session_group = _fact_token(case["session_group"], "group_", f"fixture.cases[{case_index}].session_group")
        split = _token(case["split"], f"fixture.cases[{case_index}].split")
        label = _token(case["label"], f"fixture.cases[{case_index}].label")
        if split not in {"train", "holdout"} or label not in {"harmful", "productive", "uncertain"}:
            raise FixtureError("case split must be train or holdout and label must be harmful, productive, or uncertain")
        request_count = case["historical_request_count"]
        suffix_count = case["historical_exact_suffix_count"]
        if not isinstance(request_count, int) or not isinstance(suffix_count, int):
            raise FixtureError("case request counts must be integers")
        if request_count < 0 or suffix_count < 0 or suffix_count > request_count:
            raise FixtureError("case request counts are outside the valid range")
        events = case["events"]
        if not isinstance(events, list) or not events:
            raise FixtureError(f"fixture.cases[{case_index}].events must be a non-empty list")
        normalized_events: list[dict[str, str]] = []
        for event_index, raw_event in enumerate(events):
            event = _require_object(raw_event, f"fixture.cases[{case_index}].events[{event_index}]")
            kind = _token(event.get("kind"), "event.kind")
            if kind not in {"completed", "candidate", "boundary"}:
                raise FixtureError(f"event.kind must be completed, candidate, or boundary, got {kind}")
            fields = frozenset({"kind", "boundary", "expected_decision"}) if kind == "boundary" else frozenset({"kind", "calls", "expected_decision"})
            _require_fields(event, fields, f"fixture.cases[{case_index}].events[{event_index}]")
            decision = _token(event["expected_decision"], "event.expected_decision")
            if kind == "boundary":
                if event["boundary"] not in {"new_turn", "compaction", "recovery"} or event["expected_decision"] != "none":
                    raise FixtureError("boundary events need a supported boundary and none decision")
                normalized = {"kind": kind, "boundary": event["boundary"], "expected_decision": decision}
            else:
                calls = event["calls"]
                if not isinstance(calls, list) or not calls:
                    raise FixtureError("tool events must contain a non-empty calls list")
                normalized_calls = []
                for call in calls:
                    call_fields = _CALL_FIELDS if kind == "completed" else frozenset({"tool", "argument_shape"})
                    call_object = _require_object(call, "event.call")
                    _require_fields(call_object, call_fields, "event.call")
                    normalized_call = {"tool": _fact_token(call_object["tool"], "tool_", "call.tool"), "argument_shape": _fact_token(call_object["argument_shape"], "argument_class_", "call.argument_shape")}
                    if kind == "completed":
                        normalized_call["outcome_category"] = _token(call_object["outcome_category"], "call.outcome_category")
                        if normalized_call["outcome_category"] not in {"success", "invalid_input", "access_denied", "not_found", "transient_failure", "recoverable_correction"}:
                            raise FixtureError("call.outcome_category is not a supported typed outcome")
                        normalized_call["result_shape"] = _fact_token(call_object["result_shape"], "result_class_", "call.result_shape")
                    normalized_calls.append(normalized_call)
                if (kind == "completed" and decision != "execute") or (kind == "candidate" and decision not in {"execute", "correct", "stop"}):
                    raise FixtureError("tool event has an invalid expected decision")
                normalized = {"kind": kind, "calls": normalized_calls, "expected_decision": decision}
            normalized_events.append(normalized)
        if normalized_events[-1]["kind"] != "candidate":
            raise FixtureError(f"case {case_id} must end with a candidate event")
        completed_count = sum(event["kind"] == "completed" for event in normalized_events)
        expected_prefix_count = request_count - suffix_count + min(suffix_count, 2)
        if completed_count != expected_prefix_count:
            raise FixtureError(f"case {case_id} historical counts do not match the replay prefix")
        normalized_cases.append({"id": case_id, "session_group": session_group, "split": split, "label": label, "historical_request_count": request_count, "historical_exact_suffix_count": suffix_count, "events": normalized_events})
    groups_by_split: dict[str, set[str]] = {"train": set(), "holdout": set()}
    for case in normalized_cases:
        groups_by_split[case["split"]].add(case["session_group"])
    if groups_by_split["train"] & groups_by_split["holdout"]:
        raise FixtureError("a session group cannot appear in train and holdout")
    return {
        "schema_version": 1,
        "baseline": dict(baseline),
        "sanitization": {"source_boundary": sanitization["source_boundary"], "prohibited_fields": list(prohibited)},
        "cases": normalized_cases,
    }


def load_fixture(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise FixtureError(f"cannot load fixture {path}: {error}") from error
    return validate_fixture(value)


def verify_baseline_source(source_root: Path) -> None:
    """Fail when the source file for the locked beta.3 detector changes."""

    source = source_root / BASELINE_SOURCE_PATH
    try:
        actual = hashlib.sha256(source.read_bytes()).hexdigest()
    except OSError as error:
        raise FixtureError(f"cannot read locked baseline source {source}: {error}") from error
    if actual != BASELINE_SOURCE_SHA256:
        raise FixtureError(f"locked baseline source changed: expected {BASELINE_SOURCE_SHA256}, got {actual}")


def extract_sanitized_case(
    case_id: str,
    session_group: str,
    split: str,
    label: str,
    observations: Sequence[Mapping[str, Any]],
) -> dict[str, Any]:
    """Extract one case from already-sanitized semantic observations.

    This boundary rejects raw-shaped fields before replay. The caller must
    provide symbolic tool, argument, outcome, and result facts.
    """

    events = [dict(observation) for observation in observations]
    completed_count = sum(event.get("kind") == "completed" for event in events)
    candidate = {"id": case_id, "session_group": session_group, "split": split, "label": label, "historical_request_count": completed_count, "historical_exact_suffix_count": 0, "events": events}
    fixture = {
        "schema_version": 1,
        "baseline": {"release": BASELINE_RELEASE, "detector": "exact-action-outcome", "source_path": BASELINE_SOURCE_PATH, "source_revision": BASELINE_SOURCE_REVISION, "source_sha256": BASELINE_SOURCE_SHA256, "maximum_period": 3, "maximum_history": 6},
        "sanitization": {"source_boundary": SOURCE_BOUNDARY, "prohibited_fields": sorted(_REQUIRED_PROHIBITED_FIELDS)},
        "cases": [candidate],
    }
    return validate_fixture(fixture)["cases"][0]


def _hash_fields(fields: Sequence[str]) -> str:
    digest = hashlib.sha256()
    for field in fields:
        encoded = field.encode("utf-8")
        digest.update(struct.pack(">i", len(encoded)))
        digest.update(encoded)
    return digest.hexdigest().upper()


def _action(event: Mapping[str, str]) -> str:
    # These synthetic values stand in for validated, canonical arguments.
    calls = []
    for call in event["calls"]:
        arguments = json.dumps({"shape": call["argument_shape"]}, separators=(",", ":"), ensure_ascii=False)
        argument_hash = hashlib.sha256(arguments.encode("utf-8")).hexdigest().upper()
        calls.append((call["tool"], _hash_fields(["accepted", "", argument_hash])))
    fields = []
    for tool, call_hash in sorted(calls):
        fields.extend((tool, call_hash))
    return _hash_fields(fields)


def _completed_value(event: Mapping[str, str]) -> tuple[str, str]:
    action = _action(event)
    outcomes = []
    for call in event["calls"]:
        arguments = json.dumps({"shape": call["argument_shape"]}, separators=(",", ":"), ensure_ascii=False)
        argument_hash = hashlib.sha256(arguments.encode("utf-8")).hexdigest().upper()
        call_hash = _hash_fields(["accepted", "", argument_hash])
        result_hash = _hash_fields(["result:" + call["result_shape"]])
        category = call["outcome_category"]
        outcomes.append((call["tool"], call_hash, _OUTCOME_ORDER[category], category.title().replace("_", ""), result_hash))
    fields = []
    for tool, call_hash, _, category, result_hash in sorted(outcomes):
        fields.extend((tool, call_hash, category, result_hash))
    outcome = _hash_fields(fields)
    return action, outcome


def mutated_completed_value_ignoring_result(event: Mapping[str, str]) -> tuple[str, str]:
    """Known-bad detector mutation for corpus sensitivity tests."""

    return _action(event), _hash_fields(["mutated-outcome"])


def mutated_completed_value_ignoring_arguments(event: Mapping[str, str]) -> tuple[str, str]:
    """Known-bad detector mutation for argument sensitivity tests."""

    normalized = {
        "calls": [dict(call, argument_shape="argument_class_0000") for call in event["calls"]]
    }
    return _completed_value(normalized)


def mutated_action_ignoring_arguments(event: Mapping[str, str]) -> str:
    """Known-bad detector mutation for candidate argument sensitivity tests."""

    normalized = {
        "calls": [dict(call, argument_shape="argument_class_0000") for call in event["calls"]]
    }
    return _action(normalized)


class ReplayDetector:
    """Provide the detector contract that the replay runner invokes."""

    def __init__(
        self,
        completed_value: Callable[[Mapping[str, Any]], tuple[str, str]] = _completed_value,
        action_value: Callable[[Mapping[str, Any]], str] = _action,
    ) -> None:
        self._completed_value = completed_value
        self._action_value = action_value

    def completed_value(self, event: Mapping[str, Any]) -> tuple[str, str]:
        return self._completed_value(event)

    def action_value(self, event: Mapping[str, Any]) -> str:
        return self._action_value(event)

    def decide(
        self,
        history: list[tuple[str, str]],
        candidate: str,
        last_blocked: str | None,
    ) -> tuple[str, str | None]:
        if last_blocked == candidate:
            return "stop", last_blocked
        for period in range(1, MAXIMUM_PERIOD + 1):
            required = period * 2
            if len(history) < required:
                continue
            start = len(history) - required
            if history[start : start + period] == history[start + period : start + required] and candidate == history[start][0]:
                return "correct", candidate
        return "execute", last_blocked


BASELINE_DETECTOR = ReplayDetector()


def replay_case(case: Mapping[str, Any], detector: ReplayDetector = BASELINE_DETECTOR) -> dict[str, Any]:
    history: list[tuple[str, str]] = []
    last_blocked: str | None = None
    decisions: list[str] = []
    candidate_dispatch_count = 0
    completed_event_count = 0
    request_index = 0
    first_intervention_request_index = 0
    for event in case["events"]:
        if event["kind"] == "boundary":
            decisions.append("none")
            if event["boundary"] in {"new_turn", "recovery"}:
                history.clear()
                last_blocked = None
            continue
        request_index += 1
        if event["kind"] == "completed":
            history.append(detector.completed_value(event))
            if len(history) > MAXIMUM_HISTORY:
                history.pop(0)
            decisions.append("execute")
            completed_event_count += 1
            if last_blocked is not None and history[-1][0] != last_blocked:
                last_blocked = None
            continue
        candidate = detector.action_value(event)
        decision, last_blocked = detector.decide(history, candidate, last_blocked)
        decisions.append(decision)
        if decision != "execute" and first_intervention_request_index == 0:
            first_intervention_request_index = request_index
        candidate_dispatch_count += decision == "execute"
        if decision == "correct" and last_blocked is None:
            raise AssertionError("correction must retain the blocked action")
    expected = [event["expected_decision"] for event in case["events"]]
    return {
        "id": case["id"],
        "label": case["label"],
        "decisions": decisions,
        "expected": expected,
        "candidate_dispatch_count": candidate_dispatch_count,
        "completed_event_count": completed_event_count,
        "first_intervention_request_index": first_intervention_request_index,
        "passed": decisions == expected,
    }


def replay_fixture(fixture: Mapping[str, Any], detector: ReplayDetector = BASELINE_DETECTOR) -> dict[str, Any]:
    checked = validate_fixture(fixture)
    cases = [replay_case(case, detector=detector) for case in checked["cases"]]
    return {
        "baseline": checked["baseline"],
        "cases": cases,
        "summary": {"case_count": len(cases), "passed_count": sum(case["passed"] for case in cases), "all_passed": all(case["passed"] for case in cases)},
    }


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("fixture", type=Path)
    parser.add_argument("--verify-source-root", type=Path, help="audit the current source against the recorded baseline fingerprint")
    args = parser.parse_args(argv)
    try:
        metrics = replay_fixture(load_fixture(args.fixture))
        if args.verify_source_root is not None:
            verify_baseline_source(args.verify_source_root)
    except FixtureError as error:
        print(f"fixture error: {error}", file=sys.stderr)
        return 2
    print(json.dumps(metrics, ensure_ascii=False, sort_keys=True, separators=(",", ":")))
    return 0 if metrics["summary"]["all_passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
