"""Freeze, transfer, and reopen the single-household Compose deployment."""

import argparse
from contextlib import closing
import hashlib
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
from tempfile import TemporaryDirectory


GROUPS = ("inventory", "shopping", "subscribers", "notifications")


def compose(directory, project, *args):
    result = subprocess.run(
        ["docker", "compose", "--project-name", project, *args],
        cwd=directory, check=True, capture_output=True, text=True, encoding="utf-8",
    )
    return result.stdout.strip()


def sha256(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def copy_sqlite(source, destination):
    with (
        closing(sqlite3.connect(f"{source.as_uri()}?mode=ro", uri=True)) as original,
        closing(sqlite3.connect(destination)) as copy,
    ):
        original.backup(copy)
        if copy.execute("PRAGMA integrity_check").fetchall() != [("ok",)]:
            raise ValueError("SQLite integrity check failed.")


def validate_report(raw, dry_run):
    report = json.loads(raw)
    if report["dryRun"] is not dry_run or report["householdId"] != "00000000-0000-0000-0000-000000000001":
        raise ValueError("The transfer report does not match the requested mode and legacy household.")
    groups = report["groups"]
    if set(groups) != set(GROUPS):
        raise ValueError("Transfer report must cover all four groups.")
    for group, result in groups.items():
        if result["contentMatches"] is not True or result["source"] != result["destination"]:
            raise ValueError(f"Transfer verification failed for {group}.")


def require_stopped(directory, project):
    if compose(directory, project, "ps", "--status", "running", "--quiet", "web", "backend"):
        raise ValueError("The legacy services must stay stopped while using the cutover snapshot.")


def run(args, state):
    legacy = args.legacy_dir.resolve()
    postgres = args.postgres_dir.resolve()
    source = legacy / "data" / "freshdays.db"
    snapshot = state / "snapshot"
    backup = snapshot / "freshdays.db"
    checksum = snapshot / "freshdays.db.sha256"
    plan = state / "plan.json"
    verified = state / "verified.json"
    open_marker = state / "postgres-opened"
    restored = state / "sqlite-restored"
    identity = {"project": args.project, "legacy": str(legacy), "postgres": str(postgres)}

    if plan.exists():
        if json.loads(plan.read_text(encoding="utf-8")) != identity:
            raise ValueError("The Compose project and checkouts must match the original cutover plan.")
    else:
        if args.action != "verify":
            raise ValueError("Start with the verify command.")
        plan.write_text(json.dumps(identity), encoding="utf-8")

    if restored.exists():
        raise ValueError("SQLite was restored. Use a new state directory for a new cutover.")
    if args.action != "open" and open_marker.exists():
        raise ValueError("PostgreSQL may have accepted writes. Keep SQLite offline and recover PostgreSQL.")

    if args.action == "verify":
        verified.unlink(missing_ok=True)
        target_config = compose(postgres, args.project, "config", "--hash", "backend")
        if not compose(legacy, args.project, "ps", "--all", "--quiet", "backend"):
            raise ValueError("The legacy backend container was not found in the specified project.")
        if snapshot.exists():
            require_stopped(legacy, args.project)
        compose(legacy, args.project, "stop", "web", "backend")
        require_stopped(legacy, args.project)
        if not snapshot.exists():
            with TemporaryDirectory(dir=state) as temp:
                staged = Path(temp) / "snapshot"
                staged.mkdir()
                staged_backup = staged / backup.name
                copy_sqlite(source, staged_backup)
                (staged / checksum.name).write_text(sha256(staged_backup) + "\n", encoding="utf-8")
                staged.rename(snapshot)

    digest = sha256(backup)
    if digest != checksum.read_text(encoding="utf-8").strip():
        raise ValueError("The SQLite backup checksum does not match.")

    if args.action == "restore":
        require_stopped(legacy, args.project)
        copy_sqlite(backup, source)
        restored.touch()
        compose(legacy, args.project, "start", "backend", "web")
    elif args.action == "verify":
        transfer = (
            "run", "--rm", "--no-deps", "-T", "--pull", "never",
            "--entrypoint", "dotnet", "--volume", f"{snapshot}:/backup:ro",
            "backend", "/app/FreshDays.Migration.dll", "/backup/freshdays.db",
        )
        for filename, flags in (("dry-run.json", ("--dry-run",)), ("transfer.json", ())):
            report_path = state / filename
            try:
                report = compose(postgres, args.project, *transfer, *flags)
            except subprocess.CalledProcessError as error:
                report_path.write_text(error.stdout or "", encoding="utf-8")
                raise
            report_path.write_text(report + "\n", encoding="utf-8")
            validate_report(report, bool(flags))
        verified.write_text(json.dumps({"config": target_config, "backup": digest}), encoding="utf-8")
    else:
        if not verified.exists():
            raise ValueError("The committed transfer has not been verified.")
        target_config = compose(postgres, args.project, "config", "--hash", "backend")
        if json.loads(verified.read_text(encoding="utf-8")) != {"config": target_config, "backup": digest}:
            raise ValueError("The target configuration or snapshot differs from the verified transfer.")
        validate_report((state / "dry-run.json").read_text(encoding="utf-8"), True)
        validate_report((state / "transfer.json").read_text(encoding="utf-8"), False)
        if not open_marker.exists():
            require_stopped(legacy, args.project)
            open_marker.touch()
        compose(postgres, args.project, "up", "-d", "--no-build", "--pull", "never", "--wait", "backend", "web")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("verify", "open", "restore"))
    parser.add_argument("--project", required=True, help="Existing Docker Compose project name")
    parser.add_argument("--legacy-dir", required=True, type=Path, help="Legacy checkout with its original data mount")
    parser.add_argument("--postgres-dir", required=True, type=Path, help="PostgreSQL release checkout with .env")
    parser.add_argument("--state", required=True, type=Path, help="Persistent private cutover evidence directory")
    args = parser.parse_args(argv)
    try:
        state = args.state.resolve()
        state.mkdir(mode=0o700, parents=True, exist_ok=True)
        lock = state / ".lock"
        lock.mkdir()
        try:
            run(args, state)
        finally:
            lock.rmdir()
    except subprocess.CalledProcessError as error:
        print(error.stdout or "", error.stderr or "", file=sys.stderr)
        return 1
    except (OSError, ValueError, KeyError, TypeError, sqlite3.Error) as error:
        print(error, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
