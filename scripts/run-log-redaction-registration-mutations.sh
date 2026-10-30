#!/usr/bin/env bash
set -euo pipefail

# Protect the one registration that turns on daemon log redaction. If either statement is
# removed, every log record reaches the sinks unredacted. Every expected mutant must die.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Daemon/Configuration/LoggingRegistrationExtensions.cs"
output_path="${1:-$repo_root/artifacts/stryker/log-redaction-registration}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

span="$(python3 - "$source_file" <<'PY'
from pathlib import Path
import sys

source = Path(sys.argv[1]).read_text()
begin = '// log-redaction-registration:begin'
end = '// log-redaction-registration:end'
if source.count(begin) != 1 or source.count(end) != 1:
    raise SystemExit('The log redaction registration markers must each occur once.')
start = source.index(begin)
stop = source.index(end)
body = source[start:stop]
if (stop <= start
        or body.count('builder.Services.AddNetclawLogRedaction();') != 1
        or body.count('services.Replace(') != 1):
    raise SystemExit('The log redaction registration span changed.')
print(f'{start}..{stop}')
PY
)"

(
  cd "$repo_root/src/Netclaw.Actors.MutationTests"
  dotnet stryker \
    --config-file stryker-config.json \
    --project Netclaw.Daemon.csproj \
    --mutate "Configuration/LoggingRegistrationExtensions.cs{$span}" \
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
first = source[:source.index('// log-redaction-registration:begin')].count('\n') + 1
last = source[:source.index('// log-redaction-registration:end')].count('\n') + 1
mutants = [(name, mutant) for name, data in report['files'].items()
           for mutant in data.get('mutants', []) if mutant['status'] != 'Ignored']
def in_span(name, mutant):
    return (Path(name).resolve() == source_path.resolve()
            and first <= mutant['location']['start']['line'] < last)

target = [mutant for name, mutant in mutants if in_span(name, mutant)]
# Stryker can record compilation artifacts outside the requested span.
outside = [mutant for name, mutant in mutants
           if not in_span(name, mutant) and mutant['status'] != 'CompileError']
lines = source.split('\n')
expected_lines = {index + 1 for index, line in enumerate(lines)
                  if line.strip() in ('builder.Services.AddNetclawLogRedaction();',)
                  or line.strip().startswith('services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(')}
for mutant in target:
    print(f"{mutant['status']}: line {mutant['location']['start']['line']}: {mutant['mutatorName']}")
# Each expected mutant removes one of the two statements that turn redaction on.
if (len(target) != 2 or outside
        or len(expected_lines) != 2
        or any(mutant['status'] != 'Killed' for mutant in target)
        or any(mutant['mutatorName'] != 'Statement mutation' for mutant in target)
        or {mutant['location']['start']['line'] for mutant in target} != expected_lines):
    raise SystemExit('Expected exactly two killed statement mutants, one for each registration statement.')
print('Log redaction registration: two expected mutants killed.')
PY
