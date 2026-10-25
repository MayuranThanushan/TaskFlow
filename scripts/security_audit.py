"""Run dependency audits and fail closed on missing reports or high/critical findings."""
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
HIGH = {"high", "critical"}


def nuget_findings(report, expected_projects):
    if report.get("version") != 1 or report.get("errors") or report.get("problems"):
        raise ValueError("NuGet audit failed or has an unsupported format")
    if not expected_projects:
        raise ValueError("No expected .NET projects were discovered")
    projects = report.get("projects", [])
    actual = {p.get("path", "").replace("\\", "/").rsplit("/", 1)[-1] for p in projects}
    if actual != set(expected_projects):
        raise ValueError("NuGet audit did not cover every expected project")
    findings = []
    for project in projects:
        for framework in project.get("frameworks", []):
            for group in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(group, []):
                    for issue in package.get("vulnerabilities", []):
                        severity = issue.get("severity", "").lower()
                        if severity not in {"low", "moderate", "high", "critical"}:
                            raise ValueError("Unknown NuGet vulnerability severity")
                        if severity in HIGH:
                            findings.append({"package": package["id"], "severity": severity})
    return findings


def npm_findings(report):
    if report.get("auditReportVersion") != 2 or report.get("error"):
        raise ValueError("npm audit failed or has an unsupported format")
    counts = report.get("metadata", {}).get("vulnerabilities")
    if not isinstance(counts, dict) or not {"high", "critical"}.issubset(counts):
        raise ValueError("npm audit is missing vulnerability totals")
    if any(type(counts[level]) is not int or counts[level] < 0 for level in HIGH):
        raise ValueError("npm vulnerability totals are invalid")
    return {level: counts[level] for level in sorted(HIGH) if counts[level] > 0}


def run_audits():
    output = ROOT / "TestResults/security"
    output.mkdir(parents=True, exist_ok=True)
    npm = "npm.cmd" if os.name == "nt" else "npm"
    commands = [
        ("nuget", ["dotnet", "list", "TaskFlow.slnx", "package", "--vulnerable", "--include-transitive", "--format", "json", "--output-version", "1"]),
        ("frontend", [npm, "audit", "--prefix", "TaskFlow-FE", "--json"]),
        ("browser-tools", [npm, "audit", "--prefix", "tests/browser", "--json"]),
    ]
    outcomes = []
    for name, command in commands:
        try:
            result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", timeout=240)
            (output / (name + "-audit.json")).write_text(result.stdout, encoding="utf-8")
            report = json.loads(result.stdout)
            if name == "nuget":
                if result.returncode != 0 or result.stderr.strip() or report.get("logs"):
                    raise ValueError("NuGet audit reported an execution/source error")
                findings = nuget_findings(report, [p.name for p in (ROOT / "TaskFlow-BE").glob("*/*.csproj")])
            else:
                if result.returncode not in (0, 1):
                    raise ValueError("npm audit execution failed")
                findings = npm_findings(report)
                total = report["metadata"]["vulnerabilities"].get("total")
                if result.returncode == 1 and not isinstance(total, int):
                    raise ValueError("npm audit failed without vulnerability totals")
            outcomes.append({"source": name, "passed": not findings, "blockingFindings": findings})
        except (OSError, ValueError, KeyError, TypeError, subprocess.TimeoutExpired) as error:
            outcomes.append({"source": name, "passed": False, "error": str(error)})
    (output / "audit-summary.json").write_text(json.dumps(outcomes, indent=2), encoding="utf-8")
    print(json.dumps(outcomes, indent=2))
    return all(item["passed"] for item in outcomes)


if __name__ == "__main__":
    sys.exit(0 if run_audits() else 1)
