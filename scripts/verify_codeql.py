"""Block high/critical CodeQL security findings; an empty/missing report is an error."""
import json
import math
from pathlib import Path
import sys


def resolve_rule(tool, result):
    """Resolve SARIF descriptors in either the driver or a query-pack extension."""
    reference = result.get("rule", {})
    component = reference.get("toolComponent")
    if component is None:
        owner = tool["driver"]
    else:
        extensions = tool.get("extensions", [])
        index = component.get("index")
        if type(index) is not int or not 0 <= index < len(extensions):
            raise ValueError("Finding has an invalid tool component")
        owner = extensions[index]
        for key in ("name", "guid"):
            if key in component and component[key] != owner.get(key):
                raise ValueError("Finding has inconsistent tool component metadata")
    rules = owner.get("rules", [])
    rule_id = reference.get("id", result.get("ruleId"))
    index = reference.get("index", result.get("ruleIndex"))
    if index is not None:
        if type(index) is not int or not 0 <= index < len(rules):
            raise ValueError("Finding has an invalid rule index")
        rule = rules[index]
        if rule_id is not None and rule["id"] != rule_id:
            raise ValueError("Finding has inconsistent rule metadata")
    else:
        matches = [rule for rule in rules if rule["id"] == rule_id]
        if len(matches) != 1:
            raise ValueError("Finding has no unique rule metadata")
        rule = matches[0]
    if "ruleId" in result and result["ruleId"] != rule["id"]:
        raise ValueError("Finding has inconsistent rule metadata")
    return rule


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
            tool = run["tool"]
            rules = [r for component in [tool["driver"], *tool.get("extensions", [])]
                     for r in component.get("rules", [])]
            if not rules or not isinstance(run.get("results"), list):
                raise ValueError("Analysis is missing rules/results metadata")
            for result in run.get("results", []):
                rule = resolve_rule(tool, result)
                properties = rule.get("properties", {})
                score = properties.get("security-severity")
                if score is None and "security" in properties.get("tags", []):
                    raise ValueError("Security finding has no severity metadata")
                if score is not None:
                    if isinstance(score, bool):
                        raise ValueError("Invalid CodeQL security severity")
                    severity = float(score)
                    if not math.isfinite(severity) or not 0 <= severity <= 10:
                        raise ValueError("Invalid CodeQL security severity")
                    if severity >= 7:
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
