"""Check one assigned candidate without edits to its repository."""

import hashlib
import json
from pathlib import Path
import runpy
import subprocess
import sys


def check(workspace, nonce, writer):
    if writer == "writer-a":
        source = workspace / "source/catalog.py"
        catalog = runpy.run_path(str(source))["Catalog"]()
        prior = ({"id": "opaque-a", "value": nonce}, {"id": "opaque-b"})
        catalog.refresh(prior)
        try:
            catalog.refresh(({"id": "duplicate"}, {"id": "duplicate"}))
        except ValueError:
            pass
        else:
            raise AssertionError("Duplicate identifiers must fail before publication.")
        assert catalog.read() == prior, "A rejected refresh must preserve the prior records."
        ordered = ({"id": "second"}, {"id": "first"})
        catalog.refresh(iter(ordered))
        assert catalog.read() == ordered, "Valid records must retain their exact order."
        case = ({"id": "same"}, {"id": "SAME"})
        catalog.refresh(case)
        assert catalog.read() == case, "Identifier equality must remain case-sensitive."
        checks = {"duplicate_retains_prior": True, "valid_order": True, "exact_case": True}
    elif writer == "writer-b":
        source = workspace / "source/selection.py"
        select = runpy.run_path(str(source))["select"]
        records = ({"id": "same", "value": nonce}, {"id": "SAME", "value": "second"})
        before = json.dumps(records, sort_keys=True)
        assert select(iter(records), "SAME") == records[1], "Selection must match the exact requested identifier."
        assert select(iter(records), "missing") is None, "Missing identifiers must return None."
        assert select(iter(records), "same") == records[0], "Selection must retain the matching record."
        assert json.dumps(records, sort_keys=True) == before, "Selection must preserve caller records and order."
        checks = {"exact_selection": True, "missing_is_none": True, "caller_unchanged": True}
    else:
        raise AssertionError("The writer is outside this fixture.")
    commit = subprocess.check_output(["git", "-C", str(workspace), "rev-parse", "HEAD"], text=True).strip()
    return {"nonce": nonce, "writer": writer, "candidate_commit": commit,
            "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(), "checks": checks}


if __name__ == "__main__":
    print(json.dumps(check(Path(sys.argv[1]), sys.argv[2], sys.argv[3]), sort_keys=True))
