"""Verify that the seeded reminder never looks overdue, whatever the run day."""

import json
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("run-evals.sh")
DAY = 86400
# 2026-10-18 was a Sunday.
SUNDAY_0300 = 1792292400


def run_bash(body, *args):
    completed = subprocess.run(
        ["bash", "-c", 'source "$1"; shift; ' + body, "bash", str(SCRIPT), *args],
        capture_output=True, text=True)
    return completed


class SeededReminderTests(unittest.TestCase):
    def last_fire(self, now):
        completed = run_bash("last_sunday_0300_utc_epoch \"$1\"", str(now))
        self.assertEqual(completed.returncode, 0, completed.stderr)
        return int(completed.stdout)

    def test_the_anchor_is_a_sunday_at_0300_utc(self):
        completed = subprocess.run(
            ["date", "-u", "-d", f"@{SUNDAY_0300}", "+%a %H:%M:%S"], capture_output=True, text=True)
        self.assertEqual(completed.stdout.strip(), "Sun 03:00:00")

    def test_a_sunday_after_0300_uses_the_same_day(self):
        self.assertEqual(self.last_fire(SUNDAY_0300 + 8 * 3600 + 240), SUNDAY_0300)

    def test_the_fire_second_itself_counts(self):
        self.assertEqual(self.last_fire(SUNDAY_0300), SUNDAY_0300)

    def test_a_sunday_before_0300_uses_the_week_before(self):
        self.assertEqual(self.last_fire(SUNDAY_0300 - 1), SUNDAY_0300 - 7 * DAY)
        self.assertEqual(self.last_fire(SUNDAY_0300 - 3 * 3600), SUNDAY_0300 - 7 * DAY)

    def test_a_weekday_uses_the_latest_sunday(self):
        for offset_days in range(1, 7):
            with self.subTest(offset_days=offset_days):
                self.assertEqual(self.last_fire(SUNDAY_0300 + offset_days * DAY + 1), SUNDAY_0300)

    def test_the_fire_is_never_in_the_future_or_older_than_a_week(self):
        # One shell call for every hour of two weeks: sourcing the script is slow.
        start = SUNDAY_0300 - 7 * DAY + 17
        completed = run_bash(
            'for ((h = 0; h < 14 * 24; h++)); do now=$(( $1 + h * 3600 )); '
            'echo "$now $(last_sunday_0300_utc_epoch "$now")"; done', str(start))
        self.assertEqual(completed.returncode, 0, completed.stderr)
        rows = [tuple(map(int, line.split())) for line in completed.stdout.splitlines()]
        self.assertEqual(len(rows), 14 * 24)
        for now, fire in rows:
            with self.subTest(now=now):
                self.assertLessEqual(fire, now)
                self.assertLess(now - fire, 7 * DAY)
                self.assertEqual(fire % DAY, 3 * 3600)

    def test_the_seed_writes_a_definition_and_one_successful_history_record(self):
        with tempfile.TemporaryDirectory() as home:
            reminders = Path(home) / "data" / "schedules" / "reminders"
            reminders.mkdir(parents=True)
            completed = run_bash(
                'EVAL_HOME="$1"; seed_disk_cleanup_reminder', home)
            self.assertEqual(completed.returncode, 0, completed.stderr)

            definition = json.loads((reminders / "disk-cleanup-weekly.json").read_text())
            self.assertEqual(definition["schedule"]["cronExpression"], "0 3 * * 0")

            lines = (reminders / "disk-cleanup-weekly.history.jsonl").read_text().splitlines()
            self.assertEqual(len(lines), 1)
            record = json.loads(lines[0])
            self.assertTrue(record["success"])
            self.assertFalse(record["toolDenied"])
            self.assertIsNone(record["errorMessage"])
            self.assertRegex(record["firedAt"], r"^\d{4}-\d\d-\d\dT03:00:00Z$")
            self.assertTrue(record["sessionId"])


if __name__ == "__main__":
    unittest.main()
