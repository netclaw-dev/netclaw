#!/usr/bin/env python3
"""Run the complete Main inventory through fixed, separate fixture contexts."""
import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import signal
import subprocess
import sys
import uuid

from trial_verdict import case_status

BOUNDED_CASE = 'approval_natural_directory_change'
COORDINATION_CASES = {
    'coordination_retained_child_inspection',
    'coordination_independent_synthesis',
    'coordination_artifact_review',
}
ROOT = Path(__file__).resolve().parent.parent


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def inventory(source, timeout):
    body = source.split('run_all() {', 1)[1].split('\n}', 1)[0]
    category = None
    cases = []
    for line in body.splitlines():
        text = line.strip()
        if text.startswith('print_category '):
            category = shlex.split(text)[1]
        if not text.startswith(('run_case ', 'run_multi_turn_case ')):
            if re.match(r'run_[a-z_]+(?:\s|$)', text):
                raise ValueError('An unknown run_* declaration needs a Main inventory contract.')
            continue
        words = shlex.split(text.removesuffix('\\').strip())
        command = words.pop(0)
        while words and words[0].startswith('--'):
            words.pop(0)
        if not words or not re.fullmatch('[a-z][a-z0-9_]+', words[0]) or category is None:
            raise ValueError('A Main case declaration has no canonical name or category.')
        name = words[0]
        effective_timeout = max(timeout, 120) if category == 'Subagents' else timeout
        if name == 'long_command_background_job':
            effective_timeout = max(timeout, 480)
        cases.append({'name': name, 'category': category,
                      'kind': command, 'context': 'bounded' if name == BOUNDED_CASE else 'broad',
                      'timeout_seconds': 300 if name in COORDINATION_CASES else effective_timeout})
    names = [case['name'] for case in cases]
    if len(set(names)) != len(names):
        raise ValueError('Main case declarations must have unique names.')
    required = COORDINATION_CASES | {BOUNDED_CASE, 'identity_name', 'skill_operations_diagnostics', 'long_command_background_job'}
    if not required.issubset(names):
        raise ValueError('A critical Main case declaration is absent.')
    assertions = set(re.findall(r'^assert_([a-z][a-z0-9_]*)\(\)\s*\{', source, re.MULTILINE))
    missing = set(names) - assertions
    if missing:
        raise ValueError('A declared Main case has no assertion: ' + ', '.join(sorted(missing)))
    # The stock cap remains authoritative. A source change must update this contract too.
    if not re.search(r'PROMPT_TIMEOUT=300\s+run_case --json coordination_retained_child_inspection', body):
        raise ValueError('The stock coordination timeout changed; review the Main inventory contract.')
    for expression in ('long_command_timeout > 480 ? long_command_timeout : 480',
                       'previous_timeout > 120 ? previous_timeout : 120'):
        if expression not in body:
            raise ValueError('A stock timeout floor changed; review the Main inventory contract.')
    return cases


def input_snapshot(assets, fixtures, environment, needs_observer):
    paths = set(fixtures.values())
    paths.add(Path(environment['NETCLAW_BIN']).resolve())
    if environment.get('NETCLAW_EVAL_APPROVALS_FILE'):
        paths.add(Path(environment['NETCLAW_EVAL_APPROVALS_FILE']).resolve())
    if environment.get('NETCLAW_EVAL_DATA_PROTECTION_KEYS'):
        paths.update(Path(environment['NETCLAW_EVAL_DATA_PROTECTION_KEYS']).glob('key-*.xml'))
    for directory in (ROOT / 'evals', assets / 'evals/fixtures',
                      assets / 'src/Netclaw.Cli/Resources/identity',
                      assets / 'feeds/skills/.system/files'):
        if not directory.is_dir():
            raise ValueError(f'A required input directory is absent: {directory}')
        if directory == ROOT / 'evals':
            paths.update(path for path in directory.iterdir() if path.suffix in ('.py', '.sh'))
        else:
            paths.update(path for path in directory.rglob('*') if path.is_file())
    observer = ROOT / 'evals/coordination-observer'
    paths.update(observer.glob('*.cs'))
    paths.update(observer.glob('*.csproj'))
    if needs_observer:
        compiled = observer / 'bin/Debug/net10.0'
        if not (compiled / 'coordination-observer.dll').is_file():
            raise ValueError('The parent did not prepare the coordination observer.')
        paths.update(path for path in compiled.rglob('*') if path.is_file())
    return {str(path.resolve()): sha256(path) for path in sorted(paths)}


def validate_fixture(path, context):
    path = Path(path).resolve()
    data = json.loads(path.read_text())
    personal = data['Tools']['AudienceProfiles']['Personal']
    expected_mode = 'All' if context == 'broad' else 'Roots'
    if any(personal[access]['Mode'] != expected_mode for access in ('ReadFiles', 'WriteFiles')):
        raise ValueError(f'The {context} fixture must use {expected_mode} for reads and writes.')
    if personal['ApprovalPolicy']['ToolOverrides'].get('shell_execute') != 'Auto':
        raise ValueError(f'The {context} fixture needs explicit shell_execute=Auto.')
    if context == 'bounded' and any(
            set(personal[access]['Roots']) != {'{session_dir}', '{workspaces_dir}'}
            for access in ('ReadFiles', 'WriteFiles')):
        raise ValueError('The bounded fixture must use only session and workspace roots.')
    return path


def fixture_paths(environment, assets):
    if environment.get('NETCLAW_EVAL_CONFIG_FILE'):
        raise ValueError('This Main selection needs both contexts. Use MAIN_BROAD_CONFIG_FILE and MAIN_BOUNDED_CONFIG_FILE, or select one case.')
    return {context: validate_fixture(environment.get(f'NETCLAW_EVAL_MAIN_{context.upper()}_CONFIG_FILE')
                                      or assets / 'evals/fixtures/config' / filename, context)
            for context, filename in [('broad', 'netclaw.json'), ('bounded', 'netclaw-bounded.json')]}


def child_run_id(log):
    headers = re.findall(r'^Run ID:    ([0-9a-f-]{36})$', log, re.MULTILINE)
    archives = re.findall(r'^Archived: (.+)$', log, re.MULTILINE)
    if len(headers) > 1 or len(archives) > 1:
        raise ValueError('The child repeats an identity record.')
    identities = [str(uuid.UUID(value)) for value in headers]
    for value in archives:
        path = Path(value).resolve()
        if path.parent != (ROOT / 'evals/runs').resolve():
            raise ValueError('The child archive path is outside its run root.')
        identities.append(str(uuid.UUID(path.name)))
    if not identities:
        raise ValueError('The child did not report a run ID or archive identity.')
    if len(set(identities)) != 1:
        raise ValueError('The child header and archive contain conflicting run IDs.')
    return identities[0]


def child_receipt(log, name, runs, threshold):
    run_id = child_run_id(log)
    directory = ROOT / 'evals/runs' / run_id / 'trials'
    if not directory.is_dir():
        return {'run_id': run_id, 'trials': [], 'result': None,
                'error': 'The child archive contains no trial evidence.'}
    if {path.name for path in directory.iterdir()} != {name}:
        raise ValueError('The child archive contains a different case inventory.')
    trials = sorted((directory / name).iterdir())
    expected = [f'{number:02d}' for number in range(1, runs + 1)]
    if [trial.name for trial in trials] != expected[:len(trials)]:
        raise ValueError('The child archive does not contain the declared trial prefix.')
    verdicts = []
    for trial in trials:
        path = trial / 'verdict.json'
        verdicts.append(json.loads(path.read_text()) if path.is_file() else {'status': 'missing'})
    counts = Counter(verdict['status'] for verdict in verdicts)
    if set(counts) - {'pass', 'fail', 'inconclusive', 'missing'}:
        raise ValueError('The child archive contains an unknown verdict.')
    return {'run_id': run_id, 'trials': verdicts,
            'result': case_status(counts['pass'], counts['fail'], counts['inconclusive'], threshold)
            if len(trials) == runs and not counts['missing'] else None}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--plan', action='store_true')
    parser.add_argument('--route', action='store_true')
    args = parser.parse_args()
    environment = os.environ.copy()
    runs = int(environment.get('NETCLAW_EVAL_RUNS', '5'))
    threshold = environment.get('NETCLAW_EVAL_THRESHOLD', '0.80')
    timeout = int(environment.get('NETCLAW_EVAL_TIMEOUT', '60'))
    cases = inventory((ROOT / 'evals/run-evals.sh').read_text(), timeout)
    assets = Path(environment.get('NETCLAW_EVAL_ASSET_ROOT', ROOT))
    case_filter = environment.get('NETCLAW_EVAL_CASE', '')
    category_filter = environment.get('NETCLAW_EVAL_CATEGORY', '')
    if category_filter:
        cases = [case for case in cases if category_filter.casefold() in case['category'].casefold()]
    if case_filter:
        cases = [case for case in cases if case['name'] == case_filter]
    mixed = len({case['context'] for case in cases}) > 1
    if args.route:
        if not case_filter and (not category_filter or mixed):
            fixture_paths(environment, assets)
            needs_observer = any(case['name'] in ('coordination_independent_synthesis', 'coordination_artifact_review') for case in cases)
            print('main-observer' if needs_observer else 'main')
        elif case_filter == BOUNDED_CASE:
            path = environment.get('NETCLAW_EVAL_CONFIG_FILE') or environment.get('NETCLAW_EVAL_MAIN_BOUNDED_CONFIG_FILE')
            validate_fixture(path or assets / 'evals/fixtures/config/netclaw-bounded.json', 'bounded')
            print('bounded')
        else:
            print('single')
        return 0
    fixtures = fixture_paths(environment, assets)
    settings = {key: environment.get(key) for key in (
        'NETCLAW_EVAL_PROVIDER_TYPE', 'NETCLAW_EVAL_MODEL_ID', 'NETCLAW_EVAL_FALLBACK_MODEL_ID',
        'NETCLAW_EVAL_COMPACTION_MODEL_ID', 'NETCLAW_EVAL_CONTEXT_WINDOW', 'NETCLAW_EVAL_DISABLE_THINKING',
        'NETCLAW_EVAL_SEED', 'NETCLAW_EVAL_RUNS', 'NETCLAW_EVAL_THRESHOLD', 'NETCLAW_EVAL_TIMEOUT')}
    state = {'status': 'planned', 'planned_cases': len(cases), 'planned_trials': len(cases) * runs,
             'settings': settings, 'image_id': environment.get('NETCLAW_IMAGE'),
             'assets': str(Path(environment.get('NETCLAW_EVAL_ASSET_ROOT', ROOT)).resolve()),
             'fixtures': {key: {'path': str(path), 'sha256': sha256(path)} for key, path in fixtures.items()},
             'cases': cases, 'children': []}
    if args.plan:
        print(json.dumps(state, indent=2))
        return 0
    if environment.get('NETCLAW_EVAL_MAIN_PREPARED') != '1' or environment.get('NETCLAW_EVAL_NO_BUILD') != '1':
        raise ValueError('Invoke the full Main suite through run-evals.sh so it prepares the target once.')
    if not re.fullmatch(r'sha256:[0-9a-f]{64}', environment.get('NETCLAW_IMAGE', '')):
        raise ValueError('Full Main requires an immutable local image ID.')
    if not environment.get('NETCLAW_EVAL_SEED', '').isdigit() or runs < 1:
        raise ValueError('Full Main requires one resolved seed and a positive trial count.')
    output = ROOT / 'evals/main-runs' / str(uuid.uuid4())
    output.mkdir(parents=True, mode=0o700)
    receipt = output / 'main.json'
    state['status'] = 'active'
    needs_observer = any(case['name'] in ('coordination_independent_synthesis', 'coordination_artifact_review') for case in cases)
    state['input_sha256'] = input_snapshot(Path(state['assets']), fixtures, environment, needs_observer)

    def save():
        verdicts = [trial for child in state['children'] for trial in child.get('trials', [])]
        state['stored_slots'] = len(verdicts)
        state['verdict_trials'] = sum(trial['status'] != 'missing' for trial in verdicts)
        state['conclusive_trials'] = sum(trial['status'] in ('pass', 'fail') for trial in verdicts)
        state['completed_cases'] = sum(child.get('result') is not None for child in state['children'])
        state['completed_trials'] = state['verdict_trials']
        receipt.write_text(json.dumps(state, indent=2) + '\n')

    def inputs_unchanged():
        try:
            unchanged = input_snapshot(Path(state['assets']), fixtures, environment, needs_observer) == state['input_sha256']
        except (ValueError, OSError) as error:
            state['error'] = str(error)
            unchanged = False
        if not unchanged:
            state['status'] = 'stopped'
            state.setdefault('error', 'A frozen source, asset, fixture, CLI, or observer input changed.')
            save()
        return unchanged

    save()
    print(f'Main receipt: {receipt}', flush=True)
    for number, case in enumerate(cases, 1):
        if not inputs_unchanged():
            return 2
        child_environment = environment.copy()
        child_environment['NETCLAW_EVAL_CASE'] = case['name']
        child_environment['NETCLAW_EVAL_CATEGORY'] = ''
        child_environment['NETCLAW_EVAL_CONFIG_FILE'] = str(fixtures[case['context']])
        log_path = output / f"{number:03d}-{case['name']}.log"
        print(f"Main case {number}/{len(cases)}: {case['name']} ({case['context']})", flush=True)
        interrupted = []
        with log_path.open('x') as log:
            process = subprocess.Popen(['bash', str(ROOT / 'evals/run-evals.sh')],
                                       env=child_environment, stdout=log, stderr=subprocess.STDOUT,
                                       start_new_session=True)

            def forward_signal(number, _frame):
                interrupted.append(number)
                if process.poll() is None:
                    os.killpg(process.pid, number)

            handlers = {number: signal.signal(number, forward_signal) for number in (signal.SIGINT, signal.SIGTERM)}
            try:
                exit_code = process.wait()
            finally:
                for number, handler in handlers.items():
                    signal.signal(number, handler)
        child = {'name': case['name'], 'context': case['context'], 'exit_code': exit_code,
                 'log': str(log_path)}
        state['children'].append(child)
        save()
        try:
            log = log_path.read_text()
            child['run_id'] = child_run_id(log)
            child.update(child_receipt(log, case['name'], runs, threshold))
        except (ValueError, OSError, KeyError, json.JSONDecodeError) as error:
            child['error'] = str(error)
            state['status'] = 'stopped'
            save()
            return 2
        run_ids = [item.get('run_id') for item in state['children'] if item.get('run_id')]
        if len(run_ids) != len(set(run_ids)):
            child['error'] = 'A child run ID repeats an earlier run.'
            state['status'] = 'stopped'
            save()
            return 2
        if not inputs_unchanged():
            return 2
        # Exit2 includes unconfirmed containment. Never dispatch another child after it.
        if interrupted or exit_code not in (0, 1, 3) or child['result'] is None:
            state['status'] = 'stopped'
            save()
            return 2
        if any(trial['status'] == 'inconclusive' or str(trial.get('reason', '')).startswith('infra:')
               for trial in child['trials']):
            child['error'] = 'An incomplete or infrastructure trial stops the Main protocol.'
            state['status'] = 'stopped'
            save()
            return 2
        expected_exit = {'pass': 0, 'fail': 1, 'inconclusive': 3}[child['result']['status']]
        if exit_code != expected_exit:
            child['error'] = 'The child exit code and preserved verdicts disagree.'
            state['status'] = 'stopped'
            save()
            return 2
        save()
    totals = Counter(child['result']['status'] for child in state['children'])
    state['status'] = 'fail' if totals['fail'] else 'inconclusive' if totals['inconclusive'] else 'pass'
    state['completed_cases'] = len(state['children'])
    state['completed_trials'] = sum(len(child['trials']) for child in state['children'])
    save()
    print(f"Main result: {state['status']}; {state['completed_cases']} cases; {state['completed_trials']} trials", flush=True)
    return {'pass': 0, 'fail': 1, 'inconclusive': 3}[state['status']]


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (ValueError, OSError, KeyError, IndexError, json.JSONDecodeError) as error:
        print(f'ERROR: {error}', file=sys.stderr)
        sys.exit(2)
