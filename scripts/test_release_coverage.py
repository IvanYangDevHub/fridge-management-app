import json
import subprocess
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

from release_coverage import changed_sources, coverage_failures, is_compile_time_sql, parse_coverlet, parse_lcov


class ReleaseCoverageTests(unittest.TestCase):
    def test_git_diff_gates_added_lines_in_production_sources(self):
        with TemporaryDirectory() as folder:
            root = Path(folder)

            def git(*arguments):
                subprocess.run(["git", *arguments], cwd=root, check=True, stdout=subprocess.DEVNULL)

            git("init", "--quiet")
            code = root / "frontend/src/App.tsx"
            test = code.with_name("App.test.tsx")
            code.parent.mkdir(parents=True)
            code.write_text("line one\nold line\nline three\n", encoding="utf-8")
            test.write_text("original test\n", encoding="utf-8")
            git("add", ".")
            git("-c", "user.name=Coverage Test", "-c", "user.email=coverage@example.test", "commit", "-m", "Baseline")

            code.write_text("line one\nnew line\nanother new line\nline three\n", encoding="utf-8")
            test.write_text("new test\n", encoding="utf-8")
            self.assertEqual(changed_sources(root, "HEAD"), {"frontend/src/App.tsx": {2, 3}})

    def test_sql_exception_applies_only_to_compile_time_constants(self):
        with TemporaryDirectory() as folder:
            root = Path(folder)
            name = "backend/src/FreshDays.Migration/TransferSql.cs"
            path = root / name
            path.parent.mkdir(parents=True)
            constants = ('namespace FreshDays.Migration;\ninternal static partial class SqliteTransfer\n{\n'
                         'private const string Query = """SELECT 1;""";\n')
            path.write_text(constants + '}\n', encoding="utf-8")
            self.assertTrue(is_compile_time_sql(root, name))
            path.write_text(constants + 'private static string Run() => Query;\n}\n', encoding="utf-8")
            self.assertFalse(is_compile_time_sql(root, name))

    def test_reports_with_no_production_probes_fail_closed(self):
        with TemporaryDirectory() as folder:
            root = Path(folder)
            report = root / "empty"
            report.write_text("{}", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "no production probes"):
                parse_coverlet(report, root)
            report.write_text("SF:src/App.tsx\nend_of_record\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "no production probes"):
                parse_lcov(report, root)

    def test_coverlet_measures_branch_origin_and_ignores_unchanged_lines(self):
        with TemporaryDirectory() as folder:
            path = Path(folder) / "coverage.json"
            source = "backend/src/FreshDays/Program.cs"
            path.write_text(json.dumps({"FreshDays.dll": {source: {"Program": {"Run": {
                "Lines": {"1": 0, "4": 1},
                "Branches": [
                    {"Line": 1, "Offset": 2, "Path": 0, "Ordinal": 0, "Hits": 0},
                    {"Line": 4, "Offset": 5, "Path": 0, "Ordinal": 1, "Hits": 1},
                    {"Line": 4, "Offset": 5, "Path": 1, "Ordinal": 2, "Hits": 0},
                ],
            }}}}}), encoding="utf-8")
            coverage = parse_coverlet(path, Path(folder))
            self.assertEqual(coverage_failures({source: {4, 5}}, coverage), [
                f"{source}:4: branch Program.Run offset 5 path 1 ordinal 2 is uncovered",
            ])

    def test_lcov_keeps_untaken_branches_and_missing_reports_fail(self):
        with TemporaryDirectory() as folder:
            path = Path(folder) / "lcov.info"
            path.write_text("SF:src/App.tsx\nDA:2,0\nDA:3,1\nBRDA:3,1,0,-\nend_of_record\n", encoding="utf-8")
            coverage = parse_lcov(path, Path(folder))
            self.assertEqual(coverage_failures({
                "frontend/src/App.tsx": {2, 3},
                "frontend/src/New.tsx": {2},
            }, coverage), [
                "frontend/src/App.tsx:2: line is uncovered",
                "frontend/src/App.tsx:3: branch block 1 path 0 is uncovered",
                "frontend/src/New.tsx: source missing from coverage reports",
            ])


if __name__ == "__main__":
    unittest.main()
