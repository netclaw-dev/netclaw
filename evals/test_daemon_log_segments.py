"""Use local CLI writes to check prompt assertions and trial evidence across log segments."""

import os
import unittest
from unittest import mock

import test_trial_files as trial_files


DAY = "daemon-2026-10-15"
BASE = f"{DAY}.log"
NEXT_DAY = "daemon-2026-10-16.log"
DECOYS = (
    f"{DAY}.0.log", f"{DAY}.01.log", f"{DAY}.-1.log", f"{DAY}.+1.log",
    f"{DAY}.2147483648.log", f"{DAY}.99999999999.log", f"{DAY}.1.2.log",
    f"{DAY}..log", f"{DAY}. 1.log", f"{DAY}.١.log", f"{DAY}.extra.log",
    f"{DAY}.1.log.backup", "daemon-2026-10-32.1.log",
    "daemon-2026-1-15.1.log", f"other-{DAY}.1.log",
)

# Each tuple gives the files that receive lines during each CLI call.
ROWS = (
    ("same_file_append", ((BASE,),), False),
    ("within_prompt_size_roll", ((BASE, f"{DAY}.1.log"),), False),
    ("numeric_9_10_11", ((f"{DAY}.8.log", f"{DAY}.9.log", f"{DAY}.10.log", f"{DAY}.11.log"),), False),
    ("midnight", ((BASE, NEXT_DAY),), False),
    ("baseline_lines_excluded", ((f"{DAY}.2.log",),), False),
    ("malformed_decoys_excluded", ((BASE, f"{DAY}.1.log"),), True),
    ("multi_turn_same_file", ((BASE,), (BASE,)), False),
    ("multi_turn_roll", ((BASE, f"{DAY}.1.log"),
                        (f"{DAY}.1.log", f"{DAY}.2.log", NEXT_DAY)), False),
)

CLI_WRITES = r"""
turn="${prompt#turn-}"
write_index=0
while IFS='|' read -r file text; do
    printf '%s\n' "$text" >> "$NETCLAW_HOME/logs/$file"
    write_index=$((write_index + 1))
    touch -d "@$((3000 + turn * 100 + write_index))" "$NETCLAW_HOME/logs/$file"
done < "$FAKE_STATE/$prompt.writes"
"""

# Keep the real trial drivers, baseline snapshots, readers, and evidence copy.
# The metrics seam captures each prompt window before the next prompt starts.
DRIVER_CHECKS = r"""
record_predicate() {
    local label="$1" rc=0
    shift
    "$@" || rc=$?
    printf '%s=%s\n' "$label" "$rc" >> "$PREDICATES_FILE"
}
capture_prompt_window() {
    local turn="$1"
    daemon_log_tail > "$FAKE_STATE/window-$turn.log"
    PREDICATES_FILE="$FAKE_STATE/predicates-$turn.txt"
    : > "$PREDICATES_FILE"
    record_predicate required_regex daemon_log_contains "required_event turn=$turn$"
    record_predicate required_fixed daemon_log_tail_contains_fixed "required_event turn=$turn"
    record_predicate forbidden daemon_log_tail_contains_fixed forbidden_event
    record_predicate no_forbidden daemon_log_tail_not_contains_fixed forbidden_event
    record_predicate absent daemon_log_contains absent_event
    record_predicate no_absent daemon_log_tail_not_contains_fixed absent_event
    record_predicate baseline daemon_log_contains baseline_event
    record_predicate decoy daemon_log_contains decoy_event
    record_predicate prior_turn daemon_log_contains "required_event turn=$((turn - 1))$"
    record_predicate skill daemon_log_skill_loaded_via_skill_tool "synthetic-$turn"
    record_predicate no_other_skill daemon_log_no_unexpected_skill_loaded "synthetic-$turn"
}
store_metrics() {
    if [[ "$DRIVER_KIND" == multi ]]; then capture_prompt_window "$3"; fi
}
assert_demo() {
    local turn
    turn=$(wc -l < "$FAKE_STATE/prompts.txt")
    capture_prompt_window "$turn"
    daemon_log_tail_contains_fixed "required_event turn=$turn" \
        && daemon_log_tail_not_contains_fixed forbidden_event
}
docker() { echo 'Unexpected Docker call' >&2; exit 99; }
"""


class DaemonLogSegmentTests(unittest.TestCase):
    def prepare_logs(self, harness, turns, decoys, forbidden):
        """Create the baseline and the CLI write plan in the private temp directory."""
        logs = harness.home / "logs"
        first = turns[0][0]
        segment = int(first.removeprefix(DAY).removesuffix(".log").lstrip(".") or "0")
        for number in range(segment + 1):
            name = BASE if number == 0 else f"{DAY}.{number}.log"
            path = logs / name
            path.write_text("baseline_event\nforbidden_event turn=baseline\n"
                            "required_event turn=baseline\n")
            os.utime(path, (1_000 + number, 1_000 + number))
        if decoys:
            path = logs / f"{DAY}.00.log"
            path.write_text("baseline_event decoy_event forbidden_event\n")
            os.utime(path, (2_000, 2_000))

        expected = []
        for turn, files in enumerate(turns, 1):
            writes = [(name, f"event=turn-{turn}-part-{part}")
                      for part, name in enumerate(files, 1)]
            writes.extend([
                (files[-1], f"required_event turn={turn}"),
                (files[-1], f"turn_skill_loaded skill=synthetic-{turn} method=skill_load"),
            ])
            if forbidden or (len(turns) > 1 and turn == 1):
                writes.append((files[-1], f"forbidden_event turn={turn}"))
            expected.append([line for _, line in writes])
            if decoys:
                writes.extend((name, "decoy_event forbidden_event") for name in DECOYS)
            (harness.state / f"turn-{turn}.writes").write_text(
                "".join(f"{name}|{line}\n" for name, line in writes))
        return expected

    def check_windows(self, harness, expected, forbidden):
        """Check exact lines and predicate results. Report each failure separately."""
        for turn, lines in enumerate(expected, 1):
            with self.subTest(turn=turn, check="prompt_lines"):
                self.assertEqual(lines, (harness.state / f"window-{turn}.log").read_text().splitlines())
            predicates = dict(line.split("=") for line in
                              (harness.state / f"predicates-{turn}.txt").read_text().splitlines())
            turn_forbidden = forbidden or (len(expected) > 1 and turn == 1)
            wanted = {
                "required_regex": 0, "required_fixed": 0,
                "forbidden": 0 if turn_forbidden else 1, "no_forbidden": 1 if turn_forbidden else 0,
                "absent": 1, "no_absent": 0, "baseline": 1, "decoy": 1,
                "prior_turn": 1, "skill": 0, "no_other_skill": 0,
            }
            for name, code in wanted.items():
                with self.subTest(turn=turn, check=name):
                    self.assertEqual(str(code), predicates[name])
        with self.subTest(check="trial_lines"):
            self.assertEqual([line for lines in expected for line in lines],
                             harness.read_text(harness.raw("daemon-window.log")).splitlines())
        with self.subTest(check="assertion_exit"):
            self.assertEqual(1 if forbidden else 0,
                             harness.read_json(harness.raw("assert.json"))["exit_code"])
        for turn in range(1, len(expected) + 1):
            with self.subTest(turn=turn, check="cli_exit"):
                self.assertEqual(0, harness.read_json(harness.raw(
                    "cli-exit.json", turn=turn if len(expected) > 1 else None))["exit_code"])

    def test_prompt_assertions_and_trial_evidence_follow_segments(self):
        cli = trial_files.FAKE_CLI.replace(
            "printf '%s' \"${FAKE_STDOUT-}\"", CLI_WRITES + "printf '%s' \"${FAKE_STDOUT-}\"", 1)
        driver = trial_files.DRIVER.replace("RUN_ID=run-under-test", DRIVER_CHECKS + "RUN_ID=run-under-test", 1)
        self.assertNotEqual(trial_files.FAKE_CLI, cli)
        self.assertNotEqual(trial_files.DRIVER, driver)
        for name, turns, decoys in ROWS:
            for forbidden in (False, True):
                with self.subTest(row=name, forbidden=forbidden), \
                     mock.patch.object(trial_files, "FAKE_CLI", cli), \
                     mock.patch.object(trial_files, "DRIVER", driver):
                    harness = trial_files.HarnessCase()
                    try:
                        harness.setUp()
                        expected = self.prepare_logs(harness, turns, decoys, forbidden)
                        done = harness.run_harness(
                            "multi" if len(turns) > 1 else "text",
                            [f"turn-{turn}" for turn in range(1, len(turns) + 1)], seed=7)
                        self.assertEqual(0, done.returncode, done.stderr)
                        self.check_windows(harness, expected, forbidden)
                    finally:
                        harness.doCleanups()


if __name__ == "__main__":
    unittest.main()
