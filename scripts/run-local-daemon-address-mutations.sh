#!/usr/bin/env bash
set -euo pipefail

# Protect the strict local address decision. Require its exact Boolean mutant to die.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Cli/Daemon/DaemonApi.cs"
output_path="${1:-$repo_root/artifacts/stryker/local-daemon-address}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

selection="$(python3 - "$source_file" <<'PY'
import json
from pathlib import Path
import sys

source = Path(sys.argv[1]).read_text()
begin = '// local-control-strict-config:begin'
end = '// local-control-strict-config:end'
if source.count(begin) != 1 or source.count(end) != 1:
    raise SystemExit('The strict local config markers must each occur once.')
start = source.index(begin) + len(begin)
stop = source.index(end)
decision = ('internal static string ResolveLocalControlEndpoint(NetclawPaths paths) '
            '=> ResolveDaemonConfigEndpoint(paths, lenient: false);')
if stop <= start or ' '.join(source[start:stop].split()) != decision:
    raise SystemExit('The strict local config decision changed.')
literal = source.index('false', start, stop)
line = source[:literal].count('\n') + 1
column = len(source[source.rfind('\n', 0, literal) + 1:literal].encode('utf-16-le')) // 2 + 1
# Roslyn spans use UTF-16 offsets. Select only the Boolean literal.
offset = len(source[:literal].encode('utf-16-le')) // 2
print(json.dumps({'span': f'{offset}..{offset + 5}', 'location': {
    'start': {'line': line, 'column': column},
    'end': {'line': line, 'column': column + 5}}}))
PY
)"
span="$(jq -r '.span' <<<"$selection")"

(
  cd "$repo_root/src/Netclaw.Actors.MutationTests"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Cli.csproj \
    --mutate "Daemon/DaemonApi.cs{$span}" \
    --break-at 0 \
    --output "$output_path" \
    --skip-version-check
)

python3 - "$output_path/reports/mutation-report.json" "$source_file" "$selection" <<'PY'
import json
from pathlib import Path
import sys

report_path = Path(sys.argv[1])
report = json.loads(report_path.read_text())
source_path = Path(sys.argv[2]).resolve()
selection = json.loads(sys.argv[3])
source = source_path.read_text()

def target_file(name):
    return Path(name).resolve() == source_path

def position(value):
    return value['line'], value['column']

def in_target(name, mutant):
    return target_file(name) and mutant['location'] == selection['location']

def overlaps_target(name, mutant):
    location = mutant['location']
    expected = selection['location']
    return (target_file(name)
            and position(location['start']) < position(expected['end'])
            and position(location['end']) > position(expected['start']))

mutants = [(name, mutant) for name, data in report['files'].items()
           for mutant in data.get('mutants', [])]
# Retain every compile error before any exclusion, including target compile errors.
compile_errors = [{'file': name, **mutant} for name, mutant in mutants
                  if mutant['status'] == 'CompileError']
receipt = report_path.with_name('local-daemon-address-compile-errors.json')
receipt.write_text(json.dumps(compile_errors, indent=2) + '\n')
target = [mutant for name, mutant in mutants if in_target(name, mutant)]
target_compile_errors = [mutant for name, mutant in mutants
                         if mutant['status'] == 'CompileError' and overlaps_target(name, mutant)]
outside = [mutant for name, mutant in mutants if not in_target(name, mutant)
           and mutant['status'] not in ('Ignored', 'CompileError')]
sources = [data['source'] for name, data in report['files'].items() if target_file(name)]
if (sources != [source] or len(target) != 1 or outside or target_compile_errors
        or target[0]['replacement'] != 'true' or target[0]['status'] != 'Killed'):
    raise SystemExit('Expected exactly one killed false-to-true mutant at the strict local config literal.')
print(f'Local daemon address: one expected mutant killed; compile artifacts recorded={len(compile_errors)}.')
PY
