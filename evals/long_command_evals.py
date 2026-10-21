#!/usr/bin/env python3
"""Check the long_command_background_job eval case.

Live failure, 18 October 2026: a slow shell command timed out at the 90 s
foreground limit two times. The runtime refused the third identical run, and
the model sent the same call again. This check reads only the tool calls in the
--json envelope, so the result does not depend on wall-clock time.

PASS requires both conditions:
- The agent sends the same foreground command at most two times.
- The agent uses a long-command strategy: a background job, a foreground
  timeout of 180 s or more, or the long-running-commands skill page.

A denied call never counts as a pass. The runtime can deny the command before it
runs, for example when the grant fixture lacks the program. A call that did not
run tests nothing. Thus the check also reads the headless log. A run with a
denied, missing, or unreadable result is INCONCLUSIVE, not PASS and not FAIL.
"""

import argparse
import json
from pathlib import Path
import re
import sys

COMMAND_MARKER = "slow_render"
SKILL_NAME = "netclaw-operations"
SKILL_PAGE = "long-running-commands.md"
# The fixture command sleeps 150 s and then prints. A timeout must be greater than
# the sleep, because the shell start and the print also take time. A 150 s timeout
# stops the command before it finishes.
MIN_TIMEOUT_SECONDS = 180
MAX_IDENTICAL_FOREGROUND = 2
DENIED_PREFIX = "Tool access denied"
# One record of the headless log: "[timestamp] TOOL_RESULT: tool call_id=ID result=TEXT".
LOG_HEADER = re.compile(r"^\[\d{4}-\d{2}-\d{2}T[^\]\r\n]+\] ", re.MULTILINE)
LOG_TOOL_RESULT = re.compile(r"TOOL_RESULT: (\S+) call_id=(\S+) result=([\s\S]*)")


def normalized_key(key):
    """Ignore case, underscores, and punctuation: _timeout_seconds equals TimeoutSeconds."""
    return re.sub(r"[^a-z0-9]", "", key.lower())


def argument(arguments, names):
    for key, value in arguments.items():
        if normalized_key(key) in names and value is not None:
            return value
    return None


def is_true(value):
    return value is True or (isinstance(value, str) and value.strip().lower() == "true")


def timeout_seconds(value):
    """Return the timeout as a number, or None when the value is not a number."""
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return value
    if isinstance(value, str):
        try:
            return float(value.strip())
        except ValueError:
            return None
    return None


def tool_results(headless_log):
    """Return {call id: result text} for each TOOL_RESULT record of the headless log."""
    headers = list(LOG_HEADER.finditer(headless_log))
    results = {}
    for index, header in enumerate(headers):
        end = headers[index + 1].start() if index + 1 < len(headers) else len(headless_log)
        # WriteLine adds one delimiter. Keep newlines that belong to the tool result.
        record = headless_log[header.end():end].removesuffix("\n")
        result = LOG_TOOL_RESULT.fullmatch(record)
        if result is not None:
            results[result[2]] = result[3]
    return results


def slow_command_call_ids(output):
    """Return the call ids of each shell_execute call that runs the slow command."""
    ids = []
    for call in output.get("toolCalls") or []:
        if call.get("toolName") != "shell_execute":
            continue
        raw = call.get("argumentsJson")
        arguments = json.loads(raw) if isinstance(raw, str) and raw else {}
        command = argument(arguments, {"command"})
        if isinstance(command, str) and COMMAND_MARKER in command:
            ids.append(call.get("callId"))
    return ids


def inconclusive(reason, evidence=None):
    return {"passed": False, "status": "inconclusive", "checks": {"slow_command_ran": False},
            "reason": reason, "evidence": evidence or {}}


def evaluate_run(output, headless_log):
    """Judge one trial: the envelope rules, after proof that the slow command ran.

    headless_log is the text of the headless log, or None when it is not readable.
    """
    report = evaluate(output)
    if report["reason"] == "invalid_envelope":
        return report
    ids = slow_command_call_ids(output)
    if not ids:
        # The model never sent the command. That is a model result, not a missing proof.
        return report
    if headless_log is None:
        return inconclusive("headless_log_unavailable")
    results = tool_results(headless_log)
    denied = [i for i in ids if results.get(i, "").startswith(DENIED_PREFIX)]
    if denied:
        return inconclusive("slow_command_denied", {"denied_call_count": len(denied), "call_count": len(ids)})
    missing = [i for i in ids if i not in results]
    if missing:
        return inconclusive("slow_command_result_missing",
                            {"missing_call_count": len(missing), "call_count": len(ids)})
    report["checks"]["slow_command_ran"] = True
    return report


def parse_calls(output):
    """Return a list of (tool name, arguments dict), or None for an invalid envelope."""
    if (not isinstance(output, dict) or not isinstance(output.get("sessionId"), str)
            or not output["sessionId"] or not isinstance(output.get("response"), str)):
        return None
    calls = output.get("toolCalls")
    if calls is None:
        return []
    if not isinstance(calls, list):
        return None
    parsed = []
    for call in calls:
        if not isinstance(call, dict) or not isinstance(call.get("toolName"), str):
            return None
        raw = call.get("argumentsJson")
        if raw is None or raw == "":
            arguments = {}
        else:
            try:
                arguments = json.loads(raw) if isinstance(raw, str) else None
            except ValueError:
                return None
        if not isinstance(arguments, dict):
            return None
        parsed.append((call["toolName"], arguments))
    return parsed


def evaluate(output):
    """Return {passed, status, checks, reason, evidence} for one parsed --json envelope."""
    calls = parse_calls(output)
    if calls is None:
        return {"passed": False, "status": "failed", "checks": {"envelope_valid": False},
                "reason": "invalid_envelope", "evidence": {}}
    foreground = {}
    background_calls = adequate_timeout_calls = skill_page_loads = 0
    for name, arguments in calls:
        if name == "skill_read_resource":
            values = [v.strip() for v in arguments.values() if isinstance(v, str)]
            if (any(v.lower() == SKILL_NAME for v in values)
                    and any(v.endswith(SKILL_PAGE) for v in values)):
                skill_page_loads += 1
            continue
        if name != "shell_execute":
            continue
        command = argument(arguments, {"command"})
        if not isinstance(command, str) or COMMAND_MARKER not in command:
            continue
        if is_true(argument(arguments, {"background"})):
            background_calls += 1
            continue
        timeout = timeout_seconds(argument(arguments, {"timeoutseconds", "timeout"}))
        if timeout is not None and timeout >= MIN_TIMEOUT_SECONDS:
            adequate_timeout_calls += 1
            continue
        # A missing or short timeout cannot finish the command. A changed short
        # timeout is still the same action, as the runtime call identity rule says.
        key = command.strip()
        foreground[key] = foreground.get(key, 0) + 1
    repeat_count = max(foreground.values(), default=0)
    checks = {
        "envelope_valid": True,
        "identical_foreground_at_most_two": repeat_count <= MAX_IDENTICAL_FOREGROUND,
        "long_command_strategy": bool(background_calls or adequate_timeout_calls or skill_page_loads),
    }
    passed = all(checks.values())
    reason = (None if passed else
              "identical_foreground_repeated" if not checks["identical_foreground_at_most_two"] else
              "no_long_command_strategy")
    return {"passed": passed, "status": "passed" if passed else "failed", "checks": checks, "reason": reason,
            "evidence": {"identical_foreground_repeat_count": repeat_count,
                         "background_calls": background_calls,
                         "adequate_timeout_calls": adequate_timeout_calls,
                         "skill_page_loads": skill_page_loads}}


def main(argv=None):
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    check = sub.add_parser("check")
    check.add_argument("--output", required=True)
    # An empty path is allowed: the shell passes it when the log path is unknown.
    check.add_argument("--headless-log", required=True)
    args = parser.parse_args(argv)
    try:
        output = json.loads(Path(args.output).read_text())
    except (OSError, ValueError):
        output = None
    try:
        headless_log = Path(args.headless_log).read_text() if args.headless_log else None
    except OSError:
        headless_log = None
    report = evaluate_run(output, headless_log)
    print(json.dumps(report))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
