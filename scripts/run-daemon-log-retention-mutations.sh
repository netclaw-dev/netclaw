#!/usr/bin/env bash
set -euo pipefail

# Protect the cutoff date for daemon log segments. Every expected mutant must die.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Daemon/Configuration/DaemonLogRetention.cs"
output_path="${1:-$repo_root/artifacts/stryker/daemon-log-retention}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

span="$(python3 - "$source_file" <<'PY'
from pathlib import Path
import sys

source = Path(sys.argv[1]).read_text()
begin = '// daemon-log-retention-cutoff:begin'
end = '// daemon-log-retention-cutoff:end'
if source.count(begin) != 1 or source.count(end) != 1:
    raise SystemExit('The retention cutoff markers must each occur once.')
start = source.index(begin)
stop = source.index(end)
if stop <= start or source[start:stop].count('.Where(x => x.Date < cutoff)') != 1:
    raise SystemExit('The retention cutoff span changed.')
print(f'{start}..{stop}')
PY
)"

(
  cd "$repo_root/src/Netclaw.Actors.MutationTests"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Daemon.csproj \
    --mutate "Configuration/DaemonLogRetention.cs{$span}" \
    --output "$output_path" \
    --skip-version-check
)

python3 - "$output_path/reports/mutation-report.json" "$source_file" <<'PY'
import json
from pathlib import Path
import sys

report = json.loads(Path(sys.argv[1]).read_text())
source_path = Path(sys.argv[2])
source = source_path.read_text()
first = source[:source.index('// daemon-log-retention-cutoff:begin')].count('\n') + 1
last = source[:source.index('// daemon-log-retention-cutoff:end')].count('\n') + 1
mutants = [(name, mutant) for name, data in report['files'].items()
           for mutant in data.get('mutants', []) if mutant['status'] != 'Ignored']
def in_span(name, mutant):
    return (Path(name).resolve() == source_path.resolve()
            and first <= mutant['location']['start']['line'] < last)

target = [mutant for name, mutant in mutants if in_span(name, mutant)]
# Stryker can record compilation artifacts outside the requested span.
outside = [mutant for name, mutant in mutants
           if not in_span(name, mutant) and mutant['status'] != 'CompileError']
if (len(target) != 2 or outside
        or any(mutant['status'] != 'Killed' for mutant in target)
        or {mutant['replacement'] for mutant in target} != {'x.Date > cutoff', 'x.Date <= cutoff'}):
    raise SystemExit('Expected exactly two killed mutants in the retention cutoff span.')
print('Daemon log retention: two expected mutants killed.')
PY
