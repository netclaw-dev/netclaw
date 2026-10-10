"""Run the neutral catalog acceptance checks on one actual checkout."""

import hashlib
import json
from pathlib import Path
import runpy
import subprocess
import sys


def check(workspace, nonce):
    source = workspace / "source/catalog.py"
    catalog = runpy.run_path(str(source))["Catalog"]()
    prior = ({"id": "opaque-a", "value": nonce}, {"id": "opaque-b", "value": "retained"})
    catalog.refresh(prior)
    duplicate = ({"id": "duplicate", "value": "first"}, {"id": "duplicate", "value": "second"})
    try:
        catalog.refresh(duplicate)
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
    commit = subprocess.check_output(["git", "-C", str(workspace), "rev-parse", "HEAD"], text=True).strip()
    return {"nonce": nonce, "candidate_commit": commit,
            "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
            "checks": {"duplicate_retains_prior": True, "valid_order": True, "exact_case": True}}


if __name__ == "__main__":
    print(json.dumps(check(Path(sys.argv[1]), sys.argv[2]), sort_keys=True))
