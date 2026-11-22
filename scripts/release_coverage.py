"""Enforce coverage on executable lines and branch origins changed from a Git base."""

import argparse
import json
import re
import subprocess
from dataclasses import dataclass, field
from pathlib import Path


@dataclass
class SourceCoverage:
    lines: dict[int, int] = field(default_factory=dict)
    branches: dict[tuple[int, str], int] = field(default_factory=dict)

    def record_line(self, line: int, hits: int):
        self.lines[line] = self.lines.get(line, 0) + hits

    def record_branch(self, line: int, name: str, hits: int):
        address = (line, name)
        self.branches[address] = self.branches.get(address, 0) + hits


def git(root: Path, *arguments: str) -> str:
    return subprocess.check_output(["git", *arguments], cwd=root, encoding="utf-8")


def production_source(path: str) -> bool:
    if path.startswith("backend/src/"):
        return path.endswith(".cs") and not {"bin", "obj"}.intersection(Path(path).parts)
    return (path.startswith("frontend/src/") and path.endswith((".ts", ".tsx", ".js", ".jsx"))
            and not path.endswith(".d.ts") and not re.search(r"\.(test|spec)\.[jt]sx?$", path))


def is_compile_time_sql(root: Path, path: str) -> bool:
    if path != "backend/src/FreshDays.Migration/TransferSql.cs":
        return False
    # Coverlet has no C# probes for compile-time constants. PostgreSQL runs these
    # statements during the migration integration tests.
    return re.fullmatch(r'\s*namespace FreshDays\.Migration;\s*internal static partial class SqliteTransfer\s*'
                        r'\{(?:\s*private const string \w+\s*=\s*"""(?:(?!""").)*""";)+\s*\}\s*',
                        (root / path).read_text(encoding="utf-8"), re.DOTALL) is not None


def changed_sources(root: Path, base: str) -> dict[str, set[int]]:
    sources: dict[str, set[int]] = {}
    path = None
    patch = git(root, "-c", "core.quotePath=false", "diff", "--no-ext-diff", "--no-color",
                "--unified=0", base, "--", "backend/src", "frontend/src")
    for record in patch.splitlines():
        if record.startswith("diff --git "):
            path = None
        elif path is None and record.startswith("+++ "):
            path = record.removeprefix("+++ b/").rstrip("\t")
        elif record.startswith("@@ ") and path is not None and production_source(path):
            hunk = re.match(r"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", record)
            if hunk is None:
                raise ValueError(f"Unrecognized Git hunk: {record}")
            first, count = int(hunk[1]), int(hunk[2] or 1)
            if count > 0:
                sources.setdefault(path, set()).update(range(first, first + count))
    return {path: lines for path, lines in sources.items() if not is_compile_time_sql(root, path)}


def relative_path(folder: Path, source: str, root: Path) -> str:
    return (folder / source).resolve().relative_to(root.resolve()).as_posix()


def parse_coverlet(report: Path, root: Path) -> dict[str, SourceCoverage]:
    coverage: dict[str, SourceCoverage] = {}
    for documents in json.loads(report.read_text(encoding="utf-8-sig")).values():
        for source, classes in documents.items():
            path = relative_path(root, source, root)
            if not production_source(path):
                continue
            entry = coverage.setdefault(path, SourceCoverage())
            for class_name, methods in classes.items():
                for method, result in methods.items():
                    for line, hits in result["Lines"].items():
                        entry.record_line(int(line), hits)
                    for branch in result["Branches"]:
                        name = (f"{method} offset {branch['Offset']} path {branch['Path']} "
                                f"ordinal {branch['Ordinal']}")
                        entry.record_branch(branch["Line"], f"{class_name}.{name}", branch["Hits"])
    return require_probes(report, coverage)


def parse_lcov(report: Path, root: Path) -> dict[str, SourceCoverage]:
    coverage: dict[str, SourceCoverage] = {}
    entry = None
    for record in report.read_text(encoding="utf-8").splitlines():
        if record.startswith("SF:"):
            path = relative_path(root / "frontend", record[3:], root)
            entry = coverage.setdefault(path, SourceCoverage()) if production_source(path) else None
        elif entry is not None and record.startswith("DA:"):
            line, hits, *_ = record[3:].split(",")
            entry.record_line(int(line), int(hits))
        elif entry is not None and record.startswith("BRDA:"):
            line, block, path, hits = record[5:].split(",")
            entry.record_branch(int(line), f"block {block} path {path}", int(hits) if hits != "-" else 0)
        elif record == "end_of_record":
            entry = None
    return require_probes(report, coverage)


def require_probes(report: Path, coverage: dict[str, SourceCoverage]) -> dict[str, SourceCoverage]:
    if not any(entry.lines or entry.branches for entry in coverage.values()):
        raise ValueError(f"{report}: coverage report contains no production probes")
    return coverage


def coverage_failures(changes: dict[str, set[int]], coverage: dict[str, SourceCoverage]) -> list[str]:
    failures = []
    for path, changed_lines in sorted(changes.items()):
        measured = coverage.get(path)
        if measured is None or not (measured.lines or measured.branches):
            failures.append(f"{path}: source missing from coverage reports")
            continue
        for line, hits in sorted(measured.lines.items()):
            if line in changed_lines and hits == 0:
                failures.append(f"{path}:{line}: line is uncovered")
        for (line, name), hits in sorted(measured.branches.items()):
            if line in changed_lines and hits == 0:
                failures.append(f"{path}:{line}: branch {name} is uncovered")
    return failures


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True)
    parser.add_argument("--coverlet", required=True, type=Path)
    parser.add_argument("--lcov", required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    base = git(root, "merge-base", args.base, "HEAD").strip()
    changes = changed_sources(root, base)
    coverage = parse_coverlet(args.coverlet, root) | parse_lcov(args.lcov, root)
    failures = coverage_failures(changes, coverage)
    print(f"Changed production coverage against {base}: {len(changes)} changed sources, "
          f"{len(failures)} uncovered lines or branch outcomes / missing sources.")
    for failure in failures:
        print(failure)
    return int(bool(failures))


if __name__ == "__main__":
    raise SystemExit(main())
