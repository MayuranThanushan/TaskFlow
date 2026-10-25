import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
from security_audit import nuget_findings, npm_findings
from verify_codeql import verify


class AuditGateTests(unittest.TestCase):
    def test_nuget_requires_all_projects_and_rejects_source_errors(self):
        clean = {"version": 1, "projects": [{"path": "src/Api.csproj"}]}
        self.assertEqual(nuget_findings(clean, ["Api.csproj"]), [])
        for report in [{"version": 1, "projects": []}, dict(clean, errors=["source unavailable"])]:
            with self.subTest(report=report), self.assertRaises(ValueError):
                nuget_findings(report, ["Api.csproj"])

    def test_direct_and_transitive_high_findings_block(self):
        for group in ["topLevelPackages", "transitivePackages"]:
            report = {"version": 1, "projects": [{"path": "Api.csproj", "frameworks": [{group: [{"id": "Example", "vulnerabilities": [{"severity": "High"}]}]}]}]}
            self.assertEqual(nuget_findings(report, ["Api.csproj"]), [{"package": "Example", "severity": "high"}])
            report["projects"][0]["frameworks"][0][group][0]["vulnerabilities"][0]["severity"] = "unknown"
            with self.assertRaises(ValueError): nuget_findings(report, ["Api.csproj"])

    def test_npm_high_counts_block_and_execution_errors_never_pass(self):
        report = {"auditReportVersion": 2, "metadata": {"vulnerabilities": {"high": 0, "critical": 0}}}
        self.assertEqual(npm_findings(report), {})
        report["metadata"]["vulnerabilities"]["critical"] = 1
        self.assertEqual(npm_findings(report), {"critical": 1})
        for broken in [{}, {"error": {"code": "ENOAUDIT"}}, dict(report, error={"code": "network"})]:
            with self.subTest(report=broken), self.assertRaises(ValueError): npm_findings(broken)

    def test_npm_rejects_invalid_totals(self):
        for count in ["0", -1, True, None]:
            report = {"auditReportVersion": 2, "metadata": {"vulnerabilities": {"high": count, "critical": 0}}}
            with self.subTest(count=count), self.assertRaises(ValueError): npm_findings(report)

    def test_sarif_requires_successful_execution_and_blocks_high_findings(self):
        run = {"invocations": [{"executionSuccessful": True}], "tool": {"driver": {"rules": [{"id": "test-rule", "properties": {"security-severity": "8.1"}}]}}, "results": []}
        report = {"version": "2.1.0", "runs": [run]}
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(ValueError): verify(folder)
            path = Path(folder) / "result.sarif"
            path.write_text(json.dumps(report), encoding="utf-8"); verify(folder)
            # CodeQL legitimately omits this optional SARIF property.
            optional = copy.deepcopy(report); optional["runs"][0].pop("invocations")
            path.write_text(json.dumps(optional), encoding="utf-8"); verify(folder)
            for change in ["finding", "failed", "empty", "metadata", "no-rules", "no-results"]:
                broken = copy.deepcopy(report)
                if change == "finding": broken["runs"][0]["results"] = [{"ruleId": "test-rule"}]
                if change == "failed": broken["runs"][0]["invocations"][0]["executionSuccessful"] = False
                if change == "empty": broken["runs"] = []
                if change == "metadata": broken["runs"][0]["results"] = [{"ruleId": "missing-rule"}]
                if change == "no-rules": broken["runs"][0]["tool"]["driver"]["rules"] = []
                if change == "no-results": broken["runs"][0].pop("results")
                path.write_text(json.dumps(broken), encoding="utf-8")
                with self.subTest(change=change), self.assertRaises(ValueError): verify(folder)
