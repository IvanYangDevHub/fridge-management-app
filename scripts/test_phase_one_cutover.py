from contextlib import closing
import hashlib
import importlib.util
import json
from pathlib import Path
import runpy
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import MagicMock, patch

SCRIPT = Path(__file__).with_name("phase-one-cutover.py")
spec = importlib.util.spec_from_file_location("cutover", SCRIPT)
cutover = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cutover)

REPORT = {
    "dryRun": False,
    "householdId": "00000000-0000-0000-0000-000000000001",
    "groups": {
        group: {"source": 1, "destination": 1, "contentMatches": True}
        for group in ("inventory", "shopping", "subscribers", "notifications")
    }
}


class CutoverTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name)
        self.legacy = self.root / "old"
        self.target = self.root / "new"
        self.state = self.root / "private-evidence"
        self.source = self.legacy / "data" / "freshdays.db"
        self.source.parent.mkdir(parents=True)
        self.target.mkdir()
        with closing(sqlite3.connect(self.source)) as db, db:
            db.execute("create table test (value text)")
            db.execute("insert into test values ('legacy meal')")
        self.calls = []
        self.config_hash = "initial"
        self.transfer_outputs = iter([json.dumps(REPORT | {"dryRun": True}), json.dumps(REPORT)])
        self.running = ""
        self.legacy_container = "container-id"
        runner = patch.object(cutover.subprocess, "run", side_effect=self.docker)
        runner.start()
        self.addCleanup(runner.stop)

    def docker(self, args, **kwargs):
        command = args[4:]
        self.calls.append(command)
        output = ""
        if command[:2] == ["config", "--hash"]:
            output = f"backend {self.config_hash}"
        if command == ["ps", "--all", "--quiet", "backend"]:
            output = self.legacy_container
        if command == ["ps", "--status", "running", "--quiet", "web", "backend"]:
            output = self.running
        if command[0] == "run":
            output = next(self.transfer_outputs)
            if isinstance(output, Exception):
                raise output
        return subprocess.CompletedProcess(args, 0, output, "")

    def run_cli(self, action, **options):
        config = {
            "project": "freshdays",
            "legacy-dir": self.legacy,
            "postgres-dir": self.target,
            "state": self.state,
        } | options
        return cutover.main([action, *(f"--{key}={value}" for key, value in config.items())])

    def test_stops_writers_and_verifies_the_same_snapshot_before_opening(self):
        self.assertEqual(self.run_cli("verify"), 0)
        snapshot = self.state / "snapshot" / "freshdays.db"
        with closing(sqlite3.connect(snapshot)) as db:
            self.assertEqual(db.execute("select value from test").fetchone(), ("legacy meal",))
        digest = hashlib.sha256(snapshot.read_bytes()).hexdigest()
        self.assertEqual((self.state / "snapshot" / "freshdays.db.sha256").read_text().strip(), digest)
        self.assertEqual(self.calls[2], ["stop", "web", "backend"])
        self.assertEqual(self.calls[-2][-1], "--dry-run")
        self.assertEqual(self.calls[-1][-1], "/backup/freshdays.db")
        self.assertFalse(any(call[0] == "up" for call in self.calls))
        self.assertEqual(json.loads((self.state / "transfer.json").read_text()), REPORT)
        self.assertEqual(self.run_cli("open"), 0)
        self.assertEqual(self.calls[-1], ["up", "-d", "--no-build", "--pull", "never", "--wait", "backend", "web"])



    def test_failed_verification_prevents_open_and_restores_snapshot(self):
        self.transfer_outputs = iter([
            json.dumps(REPORT | {"dryRun": True}),
            subprocess.CalledProcessError(1, ["docker"], stderr="verification failed"),
        ])
        self.assertEqual(self.run_cli("verify"), 1)
        self.assertEqual(self.run_cli("open"), 1)
        self.assertFalse(any(call[0] == "up" for call in self.calls))
        with closing(sqlite3.connect(self.source)) as db, db:
            db.execute("update test set value = 'rehearsal-only corruption'")
        self.assertEqual(self.run_cli("restore"), 0)
        self.assertEqual(self.calls[-1], ["start", "backend", "web"])
        with closing(sqlite3.connect(self.source)) as db:
            self.assertEqual(db.execute("select value from test").fetchone(), ("legacy meal",))
        self.assertEqual(self.run_cli("open"), 1)
        self.assertEqual(self.run_cli("restore"), 1)


    def test_requires_a_committed_report_for_the_legacy_household(self):
        self.transfer_outputs = iter([json.dumps(REPORT | {"dryRun": True})] * 2)
        self.assertEqual(self.run_cli("verify"), 1)
        self.assertEqual(self.run_cli("open"), 1)
        self.assertFalse(any(call[0] == "up" for call in self.calls))

    def test_refuses_to_open_after_legacy_services_resume(self):
        self.assertEqual(self.run_cli("verify"), 0)
        self.running = "old-backend"
        self.assertEqual(self.run_cli("open"), 1)
        self.assertEqual(self.run_cli("verify"), 1)
        self.assertFalse(any(call[0] == "up" for call in self.calls))

    def test_postgres_may_accept_writes_as_soon_as_open_attempt_starts(self):
        self.assertEqual(self.run_cli("verify"), 0)
        self.assertEqual(self.run_cli("open"), 0)
        self.running = "new-backend"
        self.assertEqual(self.run_cli("restore"), 1)
        self.assertEqual(self.run_cli("verify"), 1)
        self.assertEqual(self.run_cli("open"), 0)
        self.assertFalse(any(call[0] == "start" for call in self.calls))

    def test_requires_a_plan_and_identical_checkouts(self):
        self.assertEqual(self.run_cli("restore"), 1)
        self.assertEqual(self.run_cli("verify"), 0)
        self.assertEqual(self.run_cli("open", project="another"), 1)

    def test_refuses_to_cut_over_without_the_legacy_container(self):
        self.legacy_container = ""
        self.assertEqual(self.run_cli("verify"), 1)
        self.assertFalse((self.state / "snapshot").exists())

    def test_stops_if_snapshot_integrity_check_fails(self):
        copied = MagicMock()
        copied.execute.return_value.fetchall.return_value = [("damaged",)]
        with patch.object(cutover.sqlite3, "connect", side_effect=[MagicMock(), copied]):
            self.assertEqual(self.run_cli("verify"), 1)
        self.assertFalse((self.state / "snapshot").exists())

    def test_retry_reuses_the_snapshot_and_rejects_a_changed_target(self):
        self.assertEqual(self.run_cli("verify"), 0)
        self.transfer_outputs = iter([json.dumps(REPORT | {"dryRun": True}), json.dumps(REPORT)])
        self.assertEqual(self.run_cli("verify"), 0)
        self.config_hash = "different"
        self.assertEqual(self.run_cli("open"), 1)

    def test_rejects_damaged_snapshot(self):
        self.assertEqual(self.run_cli("verify"), 0)
        (self.state / "snapshot" / "freshdays.db").write_bytes(b"invalid")
        self.assertEqual(self.run_cli("open"), 1)

    def test_rejects_incomplete_and_mismatched_reports(self):
        groups = REPORT["groups"]
        for group_results in ({}, groups | {"inventory": groups["inventory"] | {"destination": 2}}):
            self.transfer_outputs = iter([json.dumps(REPORT | {"dryRun": True, "groups": group_results})])
            self.assertEqual(self.run_cli("verify"), 1)
            self.assertEqual(self.run_cli("open"), 1)

    def test_direct_execution_returns_failure_status(self):
        argv = [str(SCRIPT), "open", "--project=freshdays", f"--legacy-dir={self.legacy}",
                f"--postgres-dir={self.target}", f"--state={self.state}"]
        with patch.object(sys, "argv", argv), self.assertRaises(SystemExit) as error:
            runpy.run_path(str(SCRIPT), run_name="__main__")
        self.assertEqual(error.exception.code, 1)
