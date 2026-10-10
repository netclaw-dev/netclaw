"""Check fixed coordination artifacts and actual observer DTO evidence.

These checks prove access and headless file output for one known fixture.
They do not prove child lifecycle attribution or unrestricted prose claims.
"""

import argparse
import hashlib
import json
from pathlib import Path
import re

from child_run_evals import actual_file


LIMITS = [
    "The source revision is a SHA-256 fixture identity, not a Git commit.",
    "Identifier and structure checks do not prove action order or general semantics. Independent content review remains required.",
    "A read receipt proves access, not comprehension.",
    "Only complete direct file reads are supported. Pagination or spill needs composition proof.",
    "Child ownership and terminal attribution require the separate existing lifecycle gates.",
    "File output proves the headless surface, not remote chat upload or user receipt.",
    "Blocked evidence never closes the independent model claim review gate.",
]
PLACEHOLDERS = {
    "<objective>", "<revision>", "<scope>", "<source paths or citations>",
    "<accepted constraints>", "<path:line or URL; revision>", "<observed fact>",
    "<proof limit>", "<finding>", "<decision owner>", "<IDs>",
    "<confirmed, inferred, or unknown>", "<specific failure>", "<check>",
    "<behavior>", "<unresolved question>", "<owner>", "<source>", "<paths>",
    "<command or signal, revision, result, and limits>", "<effects>",
    "<explicit state>", "<Concrete current problem and accepted outcome.>",
    "<path>", "<action and owner>", "<observable pass/fail check>",
    "<dependency>", "<existing boundary or precise mitigation>",
    "<paired valid/invalid check>", "<unproven claim>", "<unresolved choice>",
    "<effect>", "<path and format>", "<acceptance evidence>",
    "<attachment receipt or explicit blocked state>", "<separate states>",
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "evidence: duplicate JSON key")
        result[key] = value
    return result


def load_json(text):
    return json.loads(text, object_pairs_hook=unique_object)


def report(checks, error=None):
    return {"passed": error is None, "checks": checks,
            "errors": [] if error is None else [str(error)], "limits": list(LIMITS)}


def normalized(value):
    return " ".join(value.replace("`", "").split()).casefold()


def sections(text, names):
    require(isinstance(text, str), "artifact: text is required")
    require(not any(marker in text for marker in PLACEHOLDERS), "artifact: required placeholder remains")
    result = {}
    current = None
    for line in text.splitlines():
        if line.startswith("## "):
            current = normalized(line[3:])
            require(current not in result, "artifact: repeated section")
            result[current] = []
        elif current is not None:
            result[current].append(line)
    for name in names:
        require(normalized(name) in result and any(line.strip() for line in result[normalized(name)]),
                "artifact: required section is absent or empty: " + name)
    return {key: "\n".join(value) for key, value in result.items()}


def field(section, label):
    matches = re.findall(r"^\s*-\s*" + re.escape(label) + r":\s*(.+)$", section, re.MULTILINE | re.IGNORECASE)
    require(len(matches) == 1 and matches[0].strip(), "artifact: required field: " + label)
    return matches[0].strip()


def table(section, columns):
    lines = [line.strip() for line in section.splitlines() if line.strip().startswith("|")]
    require(len(lines) >= 3, "artifact: complete table is required")
    cells = lambda line: [part.strip() for part in line.strip("|").split("|")]
    require([normalized(x) for x in cells(lines[0])] == [normalized(x) for x in columns],
            "artifact: table columns differ")
    require(all(re.fullmatch(r":?-{3,}:?", cell) for cell in cells(lines[1])),
            "artifact: table separator differs")
    result = []
    for line in lines[2:]:
        row = cells(line)
        require(len(row) == len(columns) and all(row), "artifact: table has an empty or unsupported cell")
        result.append(dict(zip(columns, row)))
    return result


def ids(text, prefix):
    return set(re.findall(r"\b" + prefix + r"\d+\b", text))


def check_artifacts(fixture_root, findings_path, plan_path):
    truth = load_json((fixture_root / "truth.json").read_text())
    require(truth["schema"] == "netclaw-coordination-artifact-truth-v1", "artifact: unknown truth schema")
    source_files = truth["source_files"]
    actual = {}
    for name in source_files:
        path = (fixture_root / name).resolve()
        require(path.is_relative_to(fixture_root.resolve()), "artifact: source path escapes the fixture")
        actual[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    require(actual == source_files, "artifact: source fixture bytes changed")
    revision = "sha256:" + hashlib.sha256("".join(
        name + "\0" + actual[name] + "\n" for name in sorted(actual)).encode()).hexdigest()
    require(revision == truth["revision"], "artifact: source revision differs")
    findings = findings_path.read_bytes().decode("utf-8")
    plan = plan_path.read_bytes().decode("utf-8")
    fs = sections(findings, ["Scope", "Evidence", "Findings", "Risks And Preserved Behavior",
                             "Unknowns And Open Decisions", "Artifact And Check Receipts"])
    ps = sections(plan, ["Problem And Accepted Outcome", "Sources And Scope", "Actions And Acceptance",
                        "Risks And De-risk Steps", "Open Decisions", "Delivery And Rollout"])
    for label in ["Accepted objective", "Source revision", "Permitted workspace and actions", "Inputs", "Constraints"]:
        field(fs["scope"], label)
    for label in ["Source revision", "Reviewed findings artifact", "Finding IDs", "Included work", "Excluded work", "Preserved behavior"]:
        field(ps["sources and scope"], label)
    require(field(fs["scope"], "Source revision") == revision
            and field(ps["sources and scope"], "Source revision") == revision, "artifact: stale source revision")
    for prefix, key in [("E", "evidence"), ("F", "findings"), ("A", "actions"),
                        ("C", "acceptance"), ("R", "risks"), ("Q", "questions")]:
        require(ids(findings + "\n" + plan, prefix) <= set(truth[key]), "artifact: forged " + prefix + " identity")
    rows = table(fs["evidence"], ["ID", "Source and revision", "Observation", "Limits"])
    require(len(rows) == len(truth["evidence"]) and {x["ID"] for x in rows} == set(truth["evidence"]),
            "artifact: evidence identities are missing or repeated")
    for row in rows:
        expected = truth["evidence"][row["ID"]]
        location = f'{expected["source"]}:{expected["start_line"]}-{expected["end_line"]}'
        require(location in row["Source and revision"] and revision in row["Source and revision"],
                "artifact: evidence source differs")
        lines = (fixture_root / expected["source"]).read_text().splitlines()[expected["start_line"] - 1:expected["end_line"]]
        require(all(excerpt in "\n".join(lines) and excerpt in row["Observation"] for excerpt in expected["excerpts"]),
                "artifact: evidence lacks the actual source excerpts")
    rows = table(fs["findings"], ["ID", "Finding", "Component owner", "Evidence IDs", "Status"])
    require(len(rows) == len(truth["findings"]) and {x["ID"] for x in rows} == set(truth["findings"]),
            "artifact: finding identities are missing or repeated")
    for row in rows:
        expected = truth["findings"][row["ID"]]
        require(row["Component owner"] == expected["owner"] and ids(row["Evidence IDs"], "E") == set(expected["evidence"])
                and normalized(row["Status"]) == expected["status"], "artifact: finding attribution differs")
    require(ids(field(ps["sources and scope"], "Finding IDs"), "F") == set(truth["findings"]),
            "artifact: plan finding references differ")
    rows = table(ps["actions and acceptance"], ["Step", "Action and owner", "Finding IDs", "Acceptance evidence", "Dependencies"])
    require(len(rows) == len(truth["actions"]) and {x["Step"] for x in rows} == set(truth["actions"]),
            "artifact: concrete actions are missing or repeated")
    for row in rows:
        expected = truth["actions"][row["Step"]]
        require(expected["owner"] in row["Action and owner"]
                and all(normalized(term) in normalized(row["Action and owner"])
                        for term in expected["required_identifiers"])
                and ids(row["Finding IDs"], "F") == set(expected["findings"])
                and ids(row["Acceptance evidence"], "C") == set(expected["acceptance"]),
                "artifact: action scope or acceptance references differ")
        for identifier in expected["acceptance"]:
            require(all(normalized(term) in normalized(row["Acceptance evidence"])
                        for term in truth["acceptance"][identifier]["required_identifiers"]),
                    "artifact: acceptance lacks the fixed behavior check")
    risks = table(ps["risks and de-risk steps"], ["Risk", "Why the proposed change is safe", "Required check", "Remaining limit"])
    require(set().union(*(ids(row["Risk"], "R") for row in risks)) == set(truth["risks"]), "artifact: required risk is absent")
    for identifier, expected in truth["risks"].items():
        matching = [row for row in risks if identifier in ids(row["Risk"], "R")]
        require(len(matching) == 1 and ids(matching[0]["Required check"], "C") == set(expected["checks"]),
                "artifact: risk lacks its paired checks")
    for identifier, expected in truth["questions"].items():
        require(identifier in fs["unknowns and open decisions"] and expected["owner"] in fs["unknowns and open decisions"]
                and identifier in ps["open decisions"] and expected["owner"] in ps["open decisions"],
                "artifact: open decision or owner is absent")
    for label in ["Complete artifact paths", "Checks", "Confirmed effects", "Unknown effects or incomplete work"]:
        field(fs["artifact and check receipts"], label)
    for label in ["Artifact requested by the user", "Parent review", "Delivery receipt", "Implementation, local checks, CI, release, and deployment"]:
        field(ps["delivery and rollout"], label)
    return findings, plan


def verify_artifacts(fixture_root, findings_path, plan_path):
    try:
        check_artifacts(Path(fixture_root), Path(findings_path), Path(plan_path))
        return report({"artifacts": True})
    except (OSError, ValueError, TypeError, KeyError, IndexError, AttributeError) as error:
        return report({"artifacts": False}, error)


def occurrences(events, session_id):
    pending, calls, files = {}, [], []
    previous_ns = 0
    for index, event in enumerate(events, 1):
        require(isinstance(event, dict) and type(event.get("sequence")) is int and event["sequence"] == index,
                "evidence: event sequence differs")
        observed = event.get("observed_ns")
        require(type(observed) is int and observed > 0 and observed >= previous_ns, "evidence: observer order differs")
        previous_ns = observed
        dto = event["output"]
        require(dto["SessionId"] == session_id, "evidence: foreign session")
        kind = dto["Type"]
        if kind == "tool_call":
            identifier, name = dto["CallId"], dto["ToolName"]
            require(isinstance(identifier, str) and identifier and identifier not in pending
                    and isinstance(name, str) and name, "evidence: invalid unresolved call identity")
            arguments = load_json(dto["ArgumentsJson"])
            require(isinstance(arguments, dict), "evidence: tool arguments must be an object")
            call = {"id": identifier, "name": name, "arguments": arguments, "call_sequence": index}
            pending[identifier] = call
            calls.append(call)
        elif kind == "tool_result":
            identifier = dto["CallId"]
            require(identifier in pending, "evidence: result lacks its actual call occurrence")
            call = pending.pop(identifier)
            require(call["name"] == dto["ToolName"] and isinstance(dto.get("Result"), str), "evidence: result attribution differs")
            call.update(result=dto["Result"], failure=dto.get("ToolFailureCode"), result_sequence=index)
        elif kind == "file":
            files.append((index, dto))
    require(not pending, "evidence: unresolved tool calls remain")
    return calls, files


def verify(contract, observer_receipt, events, eval_home, fixture_root):
    checks = {}
    try:
        session, nonce = contract["session_id"], contract["prompt_nonce"]
        require(isinstance(session, str) and session and isinstance(nonce, str) and nonce
                and observer_receipt["session_id"] == session and observer_receipt["prompt_nonce"] == nonce,
                "evidence: stale or foreign observer receipt")
        require(contract["delivery"] in {"delivered", "blocked"}, "evidence: unknown delivery expectation")
        findings_path, plan_path = contract["findings_path"], contract["plan_path"]
        require(findings_path != plan_path, "evidence: artifact paths overlap")
        findings_file, plan_file = actual_file(eval_home, findings_path), actual_file(eval_home, plan_path)
        findings, plan = check_artifacts(Path(fixture_root), findings_file, plan_file)
        checks["artifacts"] = True
        calls, files = occurrences(events, session)
        starts = [call for call in calls if call["name"] == "spawn_agent" and call["failure"] is None
                  and str(call["arguments"].get("Agent", "")).lower() == "task-worker"
                  and all(path in (call["arguments"].get("Task", "") + "\n" + (call["arguments"].get("Context") or ""))
                          for path in (findings_path, plan_path))]
        require(len(starts) == 1, "missing-proof: one actual plan assignment is required")
        accepted = load_json(starts[0]["result"])
        require(isinstance(accepted, dict) and accepted.get("state") in {"Accepted", "Running"}
                and isinstance(accepted.get("run_id"), str) and accepted["run_id"], "missing-proof: plan assignment lacks acceptance")
        def full_read(path, content, before):
            return any(call["name"] == "file_read" and call["arguments"].get("Path") == path
                       and call["arguments"].get("StartLine") in (None, 0) and call["arguments"].get("Limit") in (None, 0)
                       and call["failure"] is None and call["result"] == content
                       and call["call_sequence"] < call["result_sequence"] < before
                       for call in calls)
        require(full_read(findings_path, findings, starts[0]["call_sequence"]),
                "missing-proof: complete findings read must precede the plan assignment")
        attaches = [call for call in calls if call["name"] == "attach_file" and call["arguments"].get("Path") == plan_path]
        require(len(attaches) == 1, "delivery: one actual plan attachment attempt is required")
        attach = attaches[0]
        require(full_read(plan_path, plan, attach["call_sequence"]),
                "missing-proof: complete plan review must precede attachment")
        checks["parent_access_order"] = True
        if contract["delivery"] == "blocked":
            denied = attach["failure"] == "access_denied" and bool(attach["result"])
            missing = attach["result"] == "Error: File not found: " + plan_path
            require((denied or missing) and not files, "delivery: blocked action or absent File output lacks proof")
            checks["blocked_action_no_file"] = True
        else:
            require(attach["failure"] is None, "delivery: attachment was denied")
            matches = []
            for sequence, dto in files:
                if sequence <= attach["result_sequence"]:
                    continue
                name, mime, path = dto.get("FileName"), dto.get("MimeType"), dto.get("FilePath")
                require(all(isinstance(x, str) and x for x in (name, mime, path)), "delivery: incomplete File output")
                receipt = f"File attached: {name} ({mime}) at {path}"
                if attach["result"] not in (receipt, receipt + " (copied into current session)"):
                    continue
                delivered = actual_file(eval_home, path)
                require(delivered.read_bytes() == plan_file.read_bytes(), "delivery: emitted file bytes differ from the reviewed plan")
                matches.append(dto)
            require(len(matches) == 1, "delivery: successful receipt lacks exactly one matching File output")
            checks["headless_file_delivery"] = True
        return report(checks)
    except (OSError, ValueError, TypeError, KeyError, IndexError, AttributeError, AssertionError) as error:
        return report(checks, error)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ("fixture-root", "eval-home", "contract", "receipt", "events"):
        parser.add_argument("--" + option, required=True, type=Path)
    args = parser.parse_args()
    try:
        result = verify(load_json(args.contract.read_text()), load_json(args.receipt.read_text()),
                        [load_json(line) for line in args.events.read_text().splitlines() if line.strip()],
                        args.eval_home, args.fixture_root)
    except (OSError, ValueError, TypeError) as error:
        result = report({}, error)
    print(json.dumps(result, indent=2))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
