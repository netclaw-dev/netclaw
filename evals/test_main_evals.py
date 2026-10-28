"""Drive the actual default Bash entrypoint with a local child-process boundary."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent.parent
IMAGE = 'sha256:' + 'a' * 64


class MainEntrypointTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / 'evals/fixtures/config').mkdir(parents=True)
        (self.root / 'src/Netclaw.Cli/Resources/identity').mkdir(parents=True)
        (self.root / 'src/Netclaw.Cli/Resources/identity/SOUL.template.md').write_text('fixture')
        (self.root / 'feeds/skills/.system/files').mkdir(parents=True)
        for filename in ('run-evals.sh', 'main_evals.py', 'trial_verdict.py'):
            shutil.copyfile(ROOT / 'evals' / filename, self.root / 'evals' / filename)
        for filename in ('netclaw.json', 'netclaw-bounded.json'):
            shutil.copyfile(ROOT / 'evals/fixtures/config' / filename,
                            self.root / 'evals/fixtures/config' / filename)
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        self.calls = self.root / 'calls.jsonl'
        self.tool_calls = self.root / 'tools.jsonl'
        self.executable('docker', '''
import json, os, sys
with open(os.environ['TEST_TOOLS'], 'a') as log: log.write(json.dumps(['docker', *sys.argv[1:]])+'\\n')
if sys.argv[1:3] != ['image', 'inspect']: raise SystemExit('No container or provider operation is allowed')
print('sha256:'+'a'*64)
''')
        self.executable('dotnet', '''
import json, os, sys
from pathlib import Path
with open(os.environ['TEST_TOOLS'], 'a') as log: log.write(json.dumps(['dotnet', *sys.argv[1:]])+'\\n')
if sys.argv[1] != 'build': raise SystemExit('No model observer is allowed')
compiled=Path(sys.argv[2]).parent/'bin/Debug/net10.0'
compiled.mkdir(parents=True,exist_ok=True)
(compiled/'coordination-observer.dll').write_text('fixture-observer')
''')
        self.executable('netclaw', "import sys\nassert sys.argv[1:] == ['--version']\nprint('fixture-version')\n")
        self.executable('bash', '''
import hashlib, json, os
from pathlib import Path
import sys, uuid
name=os.environ['NETCLAW_EVAL_CASE']
assert name, 'Only exact-case children reach this boundary'
assert os.environ['NETCLAW_EVAL_MAIN_PREPARED']=='1'
assert os.environ['NETCLAW_EVAL_NO_BUILD']=='1'
config=Path(os.environ['NETCLAW_EVAL_CONFIG_FILE'])
call={'name':name,'config':str(config),'image':os.environ['NETCLAW_IMAGE'],
      'runs':os.environ['NETCLAW_EVAL_RUNS'],'seed':os.environ['NETCLAW_EVAL_SEED'],
      'threshold':os.environ['NETCLAW_EVAL_THRESHOLD'],'timeout':os.environ['NETCLAW_EVAL_TIMEOUT'],
      'provider':os.environ['NETCLAW_EVAL_PROVIDER_TYPE'],'model':os.environ['NETCLAW_EVAL_MODEL_ID'],
      'key_hash':hashlib.sha256(os.environ['NETCLAW_EVAL_PROVIDER_API_KEY'].encode()).hexdigest()}
with open(os.environ['TEST_CALLS'],'a') as log: log.write(json.dumps(call)+'\\n')
if name==os.environ.get('TEST_ABORT_CASE'): raise SystemExit(2)
run_id=str(uuid.uuid4())
root=Path(sys.argv[1]).resolve().parent/'runs'/run_id/'trials'/name
fails=int(os.environ.get('TEST_FAILURE_TRIALS','0')) if name=='identity_name' else 0
for number in range(1,int(call['runs'])+1):
    trial=root/f'{number:02d}'
    trial.mkdir(parents=True)
    verdict={'status':'fail' if number<=fails else 'pass','reason':None,'error':None}
    if number==1 and os.environ.get('TEST_INCONCLUSIVE_REASON'):
        verdict={'status':'inconclusive','reason':os.environ['TEST_INCONCLUSIVE_REASON'],'error':None}
    if not (number==1 and os.environ.get('TEST_MISSING_VERDICT')):
        (trial/'verdict.json').write_text(json.dumps(verdict))
if os.environ.get('TEST_DRIFT')=='1' or (os.environ.get('TEST_DRIFT')=='last' and name=='cli_control_daemon_status'):
    config.write_text(config.read_text()+' ')
print('Run ID:    '+run_id)
raise SystemExit(1 if fails>1 else 0)
''')
        self.environment = {key: value for key, value in os.environ.items() if not key.startswith('NETCLAW')}
        self.environment.update({
            'PATH': str(self.bin) + os.pathsep + os.environ['PATH'],
            'NETCLAW_BIN': str(self.bin / 'netclaw'), 'NETCLAW_EVAL_NO_BUILD': '1',
            'NETCLAW_IMAGE': 'mutable-tag-for-test',
            'NETCLAW_EVAL_PROVIDER_TYPE': 'fixture-provider',
            'NETCLAW_EVAL_PROVIDER_ENDPOINT': 'http://forbidden.invalid:9',
            'NETCLAW_EVAL_PROVIDER_API_KEY': 'private-test-credential',
            'NETCLAW_EVAL_MODEL_ID': 'fixture-model', 'NETCLAW_EVAL_RUNS': '5',
            'NETCLAW_EVAL_SEED': '20261027', 'NETCLAW_EVAL_THRESHOLD': '0.8',
            'NETCLAW_EVAL_TIMEOUT': '600',
            'TEST_CALLS': str(self.calls), 'TEST_TOOLS': str(self.tool_calls),
        })

    def executable(self, name, source):
        path = self.bin / name
        path.write_text('#!' + sys.executable + '\n' + source)
        path.chmod(0o755)

    def run_default(self):
        return subprocess.run(['/bin/bash', str(self.root / 'evals/run-evals.sh')],
                              env=self.environment, capture_output=True, text=True, timeout=30)

    def receipt(self):
        paths = list((self.root / 'evals/main-runs').glob('*/main.json'))
        self.assertEqual(1, len(paths))
        return json.loads(paths[0].read_text())

    def records(self, path):
        return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

    def test_default_entrypoint_runs_every_case_once_with_fixed_contexts_and_target(self):
        result = self.run_default()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        calls = self.records(self.calls)
        self.assertEqual(113, len(calls))
        self.assertEqual(113, len({call['name'] for call in calls}))
        self.assertEqual(1, sum(call['name'] == 'approval_natural_directory_change' for call in calls))
        for call in calls:
            expected = 'netclaw-bounded.json' if call['name'] == 'approval_natural_directory_change' else 'netclaw.json'
            self.assertEqual(expected, Path(call['config']).name)
            self.assertEqual((IMAGE, '5', '20261027', '0.8', '600'),
                             tuple(call[key] for key in ('image', 'runs', 'seed', 'threshold', 'timeout')))
            self.assertEqual('fixture-provider', call['provider'])
            self.assertEqual('fixture-model', call['model'])
            self.assertEqual(hashlib.sha256(b'private-test-credential').hexdigest(), call['key_hash'])
        receipt = self.receipt()
        self.assertEqual(('pass', 113, 565), tuple(receipt[key] for key in ('status', 'completed_cases', 'completed_trials')))
        self.assertEqual(113, len({child['run_id'] for child in receipt['children']}))
        self.assertEqual(3, sum(case['timeout_seconds'] == 300 for case in receipt['cases']))
        self.assertEqual(110, sum(case['timeout_seconds'] == 600 for case in receipt['cases']))
        tools = self.records(self.tool_calls)
        self.assertEqual(1, sum(tool[0] == 'dotnet' for tool in tools))
        self.assertEqual(1, sum(tool[0] == 'docker' for tool in tools))
        for path in (self.root / 'evals/main-runs').rglob('*'):
            if path.is_file():
                self.assertNotIn('private-test-credential', path.read_text())

    def test_exit2_stops_before_another_child(self):
        self.environment['TEST_ABORT_CASE'] = 'identity_name'
        result = self.run_default()
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertEqual(['identity_name'], [call['name'] for call in self.records(self.calls)])
        self.assertEqual('stopped', self.receipt()['status'])

    def test_conclusive_case_failure_retains_all_samples_and_later_cases(self):
        self.environment['TEST_FAILURE_TRIALS'] = '2'
        result = self.run_default()
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        receipt = self.receipt()
        self.assertEqual(('fail', 113, 565), tuple(receipt[key] for key in ('status', 'completed_cases', 'completed_trials')))
        self.assertEqual(2, receipt['children'][0]['result']['fails'])
        self.assertEqual(113, len(self.records(self.calls)))

    def test_missing_or_duplicate_case_fails_before_a_build_or_child(self):
        path = self.root / 'evals/run-evals.sh'
        original = path.read_text()
        for changed in (original.replace('    run_case identity_name ', '    omitted_case identity_name ', 1),
                        original.replace('    run_case identity_name ', '    run_case identity_version ', 1)):
            with self.subTest(shape='missing' if 'omitted_case' in changed else 'duplicate'):
                path.write_text(changed)
                result = self.run_default()
                self.assertEqual(2, result.returncode, result.stdout + result.stderr)
                self.assertFalse(self.calls.exists())
                self.assertFalse(self.tool_calls.exists())

    def test_one_global_fixture_is_rejected_before_a_build_or_child(self):
        self.environment['NETCLAW_EVAL_CONFIG_FILE'] = str(self.root / 'evals/fixtures/config/netclaw.json')
        result = self.run_default()
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertIn('both contexts', result.stderr)
        self.assertFalse(self.tool_calls.exists())

    def test_custom_asset_root_is_used_without_a_repository_fallback(self):
        assets = self.root / 'assets'
        shutil.copytree(self.root / 'evals/fixtures', assets / 'evals/fixtures')
        identity = assets / 'src/Netclaw.Cli/Resources/identity'
        identity.mkdir(parents=True)
        (identity / 'SOUL.template.md').write_text('custom')
        (assets / 'feeds/skills/.system/files').mkdir(parents=True)
        self.environment['NETCLAW_EVAL_ASSET_ROOT'] = str(assets)
        result = self.run_default()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertTrue(all(Path(call['config']).is_relative_to(assets) for call in self.records(self.calls)))

    def test_missing_bounded_asset_fails_before_a_build_or_child(self):
        (self.root / 'evals/fixtures/config/netclaw-bounded.json').unlink()
        result = self.run_default()
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertFalse(self.tool_calls.exists())

    def test_contradictory_explicit_context_fails_before_a_build_or_child(self):
        self.environment['NETCLAW_EVAL_MAIN_BOUNDED_CONFIG_FILE'] = str(self.root / 'evals/fixtures/config/netclaw.json')
        result = self.run_default()
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertIn('Roots', result.stderr)
        self.assertFalse(self.tool_calls.exists())

    def test_infrastructure_or_missing_evidence_stops_even_with_four_passes(self):
        for reason in ('infra:collector_unavailable', 'learned_memory_recalled', 'timeout'):
            with self.subTest(reason=reason):
                self.environment['TEST_INCONCLUSIVE_REASON'] = reason
                result = self.run_default()
                self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        calls = self.records(self.calls)
        self.assertEqual(['identity_name'] * 3, [call['name'] for call in calls])

    def test_input_drift_stops_before_another_case(self):
        self.environment['TEST_DRIFT'] = '1'
        result = self.run_default()
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertEqual(['identity_name'], [call['name'] for call in self.records(self.calls)])
        self.assertIn('input changed', self.receipt()['error'])

    def test_drift_during_the_last_case_cannot_produce_full_acceptance(self):
        self.environment['TEST_DRIFT'] = 'last'
        result = self.run_default()
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        receipt = self.receipt()
        self.assertEqual('stopped', receipt['status'])
        self.assertEqual(113, len(receipt['children']))
        self.assertEqual(565, receipt['stored_slots'])

    def test_missing_verdict_stops_and_reports_actual_slots(self):
        self.environment['TEST_MISSING_VERDICT'] = '1'
        result = self.run_default()
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        receipt = self.receipt()
        self.assertEqual((5, 4, 4), tuple(receipt[key] for key in ('stored_slots', 'verdict_trials', 'conclusive_trials')))
        self.assertEqual(1, len(self.records(self.calls)))

    def test_unknown_run_declaration_fails_before_a_build_or_child(self):
        path = self.root / 'evals/run-evals.sh'
        path.write_text(path.read_text().replace('    run_case identity_name ', '    run_future_case identity_name ', 1))
        result = self.run_default()
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertIn('unknown run_', result.stderr)
        self.assertFalse(self.tool_calls.exists())

    def test_default_plan_records_stock_timeout_floors(self):
        self.environment['NETCLAW_EVAL_TIMEOUT'] = '60'
        result = subprocess.run([sys.executable, str(self.root / 'evals/main_evals.py'), '--plan'],
                                env=self.environment, capture_output=True, text=True, timeout=10)
        self.assertEqual(0, result.returncode, result.stderr)
        cases = json.loads(result.stdout)['cases']
        self.assertEqual(480, next(case['timeout_seconds'] for case in cases if case['name']=='long_command_background_job'))
        self.assertTrue(all(case['timeout_seconds'] in (120, 300) for case in cases if case['category']=='Subagents'))

    def test_explicit_case_keeps_its_custom_fixture_and_single_context_path(self):
        self.environment['NETCLAW_EVAL_CASE'] = 'identity_name'
        custom = self.root / 'custom-single.json'
        custom.write_text('{}')
        self.environment['NETCLAW_EVAL_CONFIG_FILE'] = str(custom)
        result = self.run_single_context()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(str(custom), result.stdout.split('SELECTED:')[-1].strip())
        self.assertFalse((self.root / 'evals/main-runs').exists())

    def test_category_and_special_filters_keep_the_existing_single_context_route(self):
        for category, case, module, marker in (
                ('Identity & Self-Awareness', '', None, None),
                ('Tool Cycles', '', 'cycle_evals.sh', 'start_cycle_fixture'),
                ('', 'tool_cycle_probe', 'cycle_evals.sh', 'start_cycle_fixture'),
                ('Coordination Spill Hint', '', 'coordination_spill_evals.sh', 'start_coordination_spill_fixture'),
                ('', 'coordination_productive_long_child', 'coordination_spill_evals.sh', 'start_coordination_progress_fixture')):
            with self.subTest(category=category, case=case):
                if module:
                    (self.root / 'evals' / module).write_text(f'{marker}() {{ echo SPECIAL:{marker}; }}\n')
                self.environment['NETCLAW_EVAL_CATEGORY'] = category
                self.environment['NETCLAW_EVAL_CASE'] = case
                result = self.run_single_context()
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                if marker:
                    self.assertIn('SPECIAL:' + marker, result.stdout)
                self.assertFalse((self.root / 'evals/main-runs').exists())

    def test_mixed_category_uses_its_exact_subset_and_both_contexts(self):
        self.environment['NETCLAW_EVAL_CATEGORY'] = 'aPpRoVaL pOlIcY v2'
        result = self.run_default()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        receipt = self.receipt()
        self.assertEqual((18, 90), tuple(receipt[key] for key in ('completed_cases', 'completed_trials')))
        calls = self.records(self.calls)
        self.assertEqual(18, len(calls))
        self.assertEqual(1, sum(Path(call['config']).name=='netclaw-bounded.json' for call in calls))
        self.assertEqual(0, sum(tool[0]=='dotnet' for tool in self.records(self.tool_calls)))

    def test_exact_negative_selects_bounded_and_rejects_a_contradictory_override(self):
        self.environment['NETCLAW_EVAL_CASE'] = 'approval_natural_directory_change'
        result = self.run_single_context()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertTrue(result.stdout.split('SELECTED:')[-1].strip().endswith('/netclaw-bounded.json'))
        self.tool_calls.unlink(missing_ok=True)
        self.environment['NETCLAW_EVAL_CONFIG_FILE'] = str(self.root / 'evals/fixtures/config/netclaw.json')
        result = self.run_single_context()
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertIn('Roots', result.stderr)
        self.assertFalse(self.tool_calls.exists())

    def run_single_context(self):
        script = 'source "$1"; start_eval_daemon() { echo "SELECTED:${NETCLAW_EVAL_CONFIG_FILE:-default}"; exit 0; }; main'
        return subprocess.run(['/bin/bash', '-c', script, 'fixture', str(self.root / 'evals/run-evals.sh')],
                              env=self.environment, capture_output=True, text=True, timeout=10)

    def test_actual_checkpoint_stops_before_next_trial_and_collects_only_after_containment(self):
        order = self.root / 'order.txt'
        classifier = self.root / 'classifier.py'
        classifier.write_text('''
import json, sys
from pathlib import Path
verdict={'status':'inconclusive','reason':'infra:fixture_failure','error':None}
(Path(sys.argv[-1])/'verdict.json').write_text(json.dumps(verdict))
print(json.dumps(verdict))
''')
        self.executable('docker', '''
import os, sys
from pathlib import Path
with Path(os.environ['TEST_ORDER']).open('a') as log: log.write(sys.argv[1]+'\\n')
assert sys.argv[-1]=='a'*64
if sys.argv[1]=='stop': raise SystemExit(int(os.environ.get('TEST_STOP_FAILURE','0')))
assert sys.argv[1]=='inspect'
print('false')
''')
        raw = self.root / 'trial/raw'
        raw.mkdir(parents=True)
        original = {
            'session.json': b'{"state":"before-stop"}\n',
            'cli-session.log': b'pre-stop CLI evidence\n',
            'actor-log.json': b'{"state":"before-stop"}\n',
            'session.log': b'pre-stop actor evidence\n',
        }
        for name, contents in original.items():
            (raw / name).write_bytes(contents)
        environment = self.environment.copy()
        environment['TEST_ORDER'] = str(order)
        script = '''
source "$1"
TRIAL_VERDICT_PY="$2"
TRIAL_RAW="$3"
EVAL_CONTAINER_ID=$(printf 'a%.0s' {1..64})
EVAL_CONTAINMENT_STATUS=""
NETCLAW_EVAL_MAIN_PREPARED=1
store_result() { echo stored >> "$TEST_ORDER"; }
finish_trial() {
    echo collector >> "$TEST_ORDER"
    for name in session.json cli-session.log actor-log.json session.log; do
        printf 'post-stop:%s' "$name" > "$TRIAL_RAW/$name"
    done
}
archive_eval_run() { echo archive >> "$TEST_ORDER"; }
trap cleanup_eval_env EXIT
for trial in 1 2; do
    record_trial fixture "$trial" prompt fail
    echo next-trial-setup >> "$TEST_ORDER"
done
echo next-case >> "$TEST_ORDER"
'''
        for stop_failure in ('0', '1'):
            with self.subTest(stop_failure=stop_failure):
                order.unlink(missing_ok=True)
                if (raw / 'post-containment').exists():
                    shutil.rmtree(raw / 'post-containment')
                environment['TEST_STOP_FAILURE'] = stop_failure
                result = subprocess.run(['/bin/bash', '-c', script, 'fixture',
                                         str(self.root / 'evals/run-evals.sh'), str(classifier), str(raw)],
                                        env=environment, capture_output=True, text=True, timeout=10)
                self.assertEqual(2, result.returncode, result.stdout + result.stderr)
                expected = ['stored', 'stop', 'inspect', 'collector', 'archive'] if stop_failure=='0' else ['stored', 'stop']
                self.assertEqual(expected, order.read_text().splitlines())
                verdict = json.loads((raw.parent / 'verdict.json').read_text())
                self.assertEqual('infra:fixture_failure', verdict['reason'])
                for name, contents in original.items():
                    self.assertEqual(contents, (raw / name).read_bytes())
                    if stop_failure == '0':
                        self.assertEqual('post-stop:' + name, (raw / 'post-containment' / name).read_text())
                if stop_failure == '1':
                    self.assertFalse((raw / 'post-containment').exists())
                expected_verdict = json.dumps({'status':'inconclusive','reason':'infra:fixture_failure','error':None})
                self.assertEqual(expected_verdict, (raw.parent / 'verdict.json').read_text())
        order.unlink()
        environment['TEST_STOP_FAILURE'] = '0'
        direct = script.replace('NETCLAW_EVAL_MAIN_PREPARED=1', 'NETCLAW_EVAL_MAIN_PREPARED=0')
        result = subprocess.run(['/bin/bash', '-c', direct, 'fixture',
                                 str(self.root / 'evals/run-evals.sh'), str(classifier), str(raw)],
                                env=environment, capture_output=True, text=True, timeout=10)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn('next-case', order.read_text().splitlines())
        self.assertEqual(2, order.read_text().splitlines().count('stored'))


if __name__ == '__main__':
    unittest.main()
