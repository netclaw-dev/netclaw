#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Cli/Skills/SkillCommand.cs"
verify_only=false
if [[ "${1:-}" == "--verify" ]]; then
  verify_only=true
  shift
fi
output_path="${1:-$repo_root/artifacts/stryker/skill-remove}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi
targets="$(mktemp)"
trap 'rm -f "$targets"' EXIT

# Each expression must occur once. Source drift fails before the mutation run.
python3 - "$source_file" >"$targets" <<'PY'
import sys
from pathlib import Path

source = Path(sys.argv[1]).read_text()
targets = [
    (2, 'skill.Source is not "native"'),
    (1, '!string.Equals(daemonApi.Endpoint, localEndpoint, StringComparison.OrdinalIgnoreCase)'),
    (1, '!PathUtility.IsWithinRootWithoutLinks(detail.FilePath, skillsRoot)'),
    (1, '!PathUtility.IsWithinRootWithoutLinks(skillDirectory, skillsRoot)'),
]
for count, expression in targets:
    if source.count(expression) != 1:
        raise SystemExit(f"Expected one skill remove expression: {expression}")
    start = source.index(expression)
    end = start + len(expression)
    prefix = source[:start]
    first_line = prefix.count("\n") + 1
    first_column = len(prefix.rsplit("\n", 1)[-1].encode("utf-16-le")) // 2 + 1
    last_column = first_column + len(expression.encode("utf-16-le")) // 2
    print(len(prefix.encode("utf-16-le")) // 2,
          len(source[:end].encode("utf-16-le")) // 2,
          first_line, first_column, last_column, count)
PY

mutate_args=()
while read -r span_start span_end _; do
  mutate_args+=(--mutate "Skills/SkillCommand.cs{$span_start..$span_end}")
done <"$targets"

if [[ "$verify_only" == false ]]; then
(
  cd "$repo_root/src/Netclaw.Cli.MutationTests"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Cli.csproj \
    "${mutate_args[@]}" \
    --output "$output_path" \
    --skip-version-check
)
fi

python3 - "$source_file" "$targets" "$output_path/reports/mutation-report.json" <<'PY'
import json
import sys
from pathlib import Path

source, targets_file, report_file = sys.argv[1:]
source_text = Path(source).read_text().encode("utf-16-le")
report = json.loads(Path(report_file).read_text())
mutants = report["files"].get(source, {}).get("mutants", [])
selected_ids = set()
for row in Path(targets_file).read_text().splitlines():
    span_start, span_end, line, first, last, expected = map(int, row.split())
    expression = source_text[span_start * 2:span_end * 2].decode("utf-16-le")
    identities = ({("Equality mutation", 'skill.Source is "native"'), ("String mutation", '""')}
                  if expected == 2 else
                  {("LogicalNotExpression to un-LogicalNotExpression mutation", expression[1:])})
    selected = [m for m in mutants
                if m["location"]["start"]["line"] == line
                and m["location"]["end"]["line"] == line
                and m["location"]["start"]["column"] >= first
                and m["location"]["end"]["column"] <= last]
    if (len(selected) != expected or any(m["status"] != "Killed" for m in selected)
            or {(m["mutatorName"], m["replacement"]) for m in selected} != identities):
        details = [(m["mutatorName"], m["status"]) for m in selected]
        raise SystemExit(f"Expected {expected} killed skill remove mutants at line {line}; found {details}")
    selected_ids.update(m["id"] for m in selected)
for path, file in report["files"].items():
    for mutant in file["mutants"]:
        if mutant["status"] not in ("Ignored", "CompileError") and (path != source or mutant["id"] not in selected_ids):
            raise SystemExit(f"Unexpected active mutant in {path}: {mutant['id']}")
print(f"All {len(selected_ids)} required skill remove mutants ran and died.")
PY
