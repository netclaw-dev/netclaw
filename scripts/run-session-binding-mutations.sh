#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Channels/ChannelSessionBindingActor.cs"
test_project="$repo_root/src/Netclaw.Actors.Tests"
output_path="${1:-$repo_root/artifacts/stryker/session-binding}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi
scope_file="$(mktemp)"
trap 'rm -f "$scope_file"' EXIT

spans="$(python3 - "$source_file" "$scope_file" <<'PY'
import json
import sys
from pathlib import Path

source = Path(sys.argv[1]).read_text()
targets = [
    ("trusted-session", "message.SessionId != _sessionId", 1),
    ("output-lifetime", "incarnation == _incarnation && _handle.IsInitialized && generation == _handle.Generation", 4),
]

def location(offset):
    before = source[:offset]
    line = before.count("\n") + 1
    column = len(before.rsplit("\n", 1)[-1].encode("utf-16-le")) // 2 + 1
    return [line, column]

scope = []
for name, expression, expected in targets:
    if source.count(expression) != 1:
        raise SystemExit(f"The {name} boundary is missing or duplicated.")
    start = source.index(expression)
    end = start + len(expression)
    first = len(source[:start].encode("utf-16-le")) // 2
    last = len(source[:end].encode("utf-16-le")) // 2
    scope.append({"name": name, "start": location(start), "end": location(end), "expected": expected})
    print(f"{first}..{last}")
Path(sys.argv[2]).write_text(json.dumps(scope))
PY
)"
mutate_args=()
while IFS= read -r span; do
  mutate_args+=(--mutate "ChannelSessionBindingActor.cs{$span}")
done <<< "$spans"

(
  cd "$test_project"
  # xUnit v3 uses a child process. Preserve Stryker's selector across that boundary.
  STRYKER_MUTANT_ID_CONTROL_VAR=ACTIVE_MUTATION_0 dotnet stryker \
    --config-file stryker-session-binding.json \
    --project Netclaw.Channels.csproj \
    "${mutate_args[@]}" \
    --output "$output_path" \
    --skip-version-check
)

python3 - "$output_path/reports/mutation-report.json" "$scope_file" "$source_file" <<'PY'
import json
import sys
from pathlib import Path

report = json.loads(Path(sys.argv[1]).read_text())
scope = json.loads(Path(sys.argv[2]).read_text())
source = sys.argv[3]
mutants = report.get("files", {}).get(source, {}).get("mutants", [])
expected = []
for target in scope:
    selected = [m for m in mutants
                if target["start"] <= [m["location"]["start"]["line"], m["location"]["start"]["column"]]
                and [m["location"]["end"]["line"], m["location"]["end"]["column"]] <= target["end"]]
    if len(selected) != target["expected"] or any(m["status"] != "Killed" for m in selected):
        raise SystemExit(f"Expected {target['expected']} executed and killed {target['name']} mutants: {selected}")
    if target["name"] == "trusted-session":
        identity = [m for m in selected if m["mutatorName"] == "Equality mutation"
                    and m["replacement"] == "message.SessionId == _sessionId"]
        if len(identity) != 1:
            raise SystemExit("The trusted-session inversion did not execute and die.")
    if target["name"] == "output-lifetime":
        weakened = [m for m in selected if "||" in m["replacement"]]
        if len(weakened) != 2:
            raise SystemExit("The two lifetime conjunction weakenings did not execute and die.")
    expected.extend(selected)
    print(f"{target['name']}: {len(selected)} required mutants killed")

active = [(path, m) for path, data in report.get("files", {}).items()
          for m in data.get("mutants", []) if m["status"] not in ("Ignored", "CompileError")]
if len(active) != len(expected) or any(path != source or m not in expected for path, m in active):
    raise SystemExit("Unexpected active mutants cannot satisfy the session-binding gate.")
PY
