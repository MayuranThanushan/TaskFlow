"""Block high/critical CodeQL security findings; an empty/missing report is an error."""
import json
from pathlib import Path
import sys


def verify(directory):
    files = list(Path(directory).glob("*.sarif"))
    if not files:
        raise ValueError("No CodeQL SARIF report was produced")
    runs = 0
    for path in files:
        report = json.loads(path.read_text(encoding="utf-8"))
        if report.get("version") != "2.1.0":
            raise ValueError("Unsupported SARIF version")
        for run in report.get("runs", []):
            runs += 1
            # CodeQL may omit optional SARIF invocations. The preceding analyze
            # action must succeed; honor explicit failure metadata when present.
            if any(not i.get("executionSuccessful") for i in run.get("invocations", [])):
                raise ValueError("CodeQL analysis did not complete successfully")
            driver = run["tool"]["driver"]
            rules = {r["id"]: r for r in driver.get("rules", [])}
            if not rules or not isinstance(run.get("results"), list):
                raise ValueError("Analysis is missing rules/results metadata")
            for result in run.get("results", []):
                rule = rules.get(result.get("ruleId"))
                if rule is None:
                    raise ValueError("Finding has no rule metadata")
                score = rule.get("properties", {}).get("security-severity")
                if score is not None and float(score) >= 7:
                    raise ValueError("High/critical CodeQL finding: " + rule["id"])
    if not runs:
        raise ValueError("CodeQL report contained no analysis runs")
    print(f"Verified {runs} CodeQL analysis run(s), no high/critical findings")


if __name__ == "__main__":
    try:
        verify(sys.argv[1])
    except (OSError, ValueError, KeyError, IndexError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
