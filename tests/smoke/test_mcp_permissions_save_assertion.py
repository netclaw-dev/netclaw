"""Reject invalid persisted grants without a native process or provider."""

import copy
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ASSERTION = Path(__file__).parent / "assertions" / "mcp-permissions-save.sh"
GRANTS = ["add", "echo", "image-with-metadata", "image-with-notes", "process-info", "record-tasks"]


def saved_config():
    return {"Tools": {"AudienceProfiles": {
        "Personal": {"McpServersMode": "Allowlist", "AllowedMcpServers": []},
        "Team": {"McpServersMode": "Allowlist", "AllowedMcpServers": ["smoke-math"],
                 "McpServerToolGrants": {"smoke-math": GRANTS}},
    }}}


class McpPermissionsSaveAssertionTests(unittest.TestCase):
    def check_config(self, config, expected_exit):
        with tempfile.TemporaryDirectory(prefix="netclaw-mcp-save-oracle-") as directory:
            path = Path(directory) / "config" / "netclaw.json"
            path.parent.mkdir()
            if config is not None:
                path.write_text(json.dumps(config), encoding="utf-8")
            environment = os.environ.copy()
            environment.update(NETCLAW_HOME=directory, NETCLAW_SMOKE_CLI="/unused-native-binary")
            result = subprocess.run(["bash", str(ASSERTION)], env=environment,
                                    capture_output=True, text=True, timeout=10, check=False)
            self.assertEqual(expected_exit, result.returncode, result.stdout + result.stderr)

    def test_complete_canonical_save_passes(self):
        self.check_config(saved_config(), 0)

    def test_unsaved_config_fails(self):
        self.check_config({"Tools": {"AudienceProfiles": {
            "Personal": {"ApprovalPolicy": {}}, "Team": {"ApprovalPolicy": {}}}}}, 1)

    def test_missing_config_fails(self):
        self.check_config(None, 1)

    def test_wrong_audience_fails(self):
        config = saved_config()
        profiles = config["Tools"]["AudienceProfiles"]
        profiles["Personal"], profiles["Team"] = profiles["Team"], profiles["Personal"]
        self.check_config(config, 1)

    def test_missing_foreign_or_duplicate_grants_fail(self):
        for grants in (GRANTS[:-1], GRANTS + ["foreign-tool"], GRANTS + ["echo"]):
            with self.subTest(grants=grants):
                config = copy.deepcopy(saved_config())
                config["Tools"]["AudienceProfiles"]["Team"]["McpServerToolGrants"]["smoke-math"] = grants
                self.check_config(config, 1)

    def test_foreign_server_fails(self):
        config = saved_config()
        config["Tools"]["AudienceProfiles"]["Team"]["AllowedMcpServers"] = ["foreign-server"]
        self.check_config(config, 1)


if __name__ == "__main__":
    unittest.main()
