"""Evaluate a private --delivery-status JSON snapshot; never send notifications or mutate queues."""
import argparse
from datetime import datetime, timezone
import json
import math
from pathlib import Path

QUEUES = {"checkpoint", "account_mail", "invitation_mail", "watcher"}
RUNBOOK = "docs/saas/delivery-reliability.md"


def evaluate(snapshot, now=None):
    now = now or datetime.now(timezone.utc)
    if not isinstance(snapshot, dict) or type(snapshot.get("version")) is not int or snapshot.get("version") != 1:
        raise ValueError("Invalid snapshot")
    observed = datetime.fromisoformat(snapshot["observedAtUtc"].replace("Z", "+00:00"))
    if observed.tzinfo is None or observed.utcoffset().total_seconds() != 0:
        raise ValueError("UTC observation required")
    age = (now - observed).total_seconds()
    rows = snapshot["queues"]
    if not isinstance(rows, list) or len(rows) != 4 or {r["queue"] for r in rows} != QUEUES:
        raise ValueError("Complete queue inventory required")
    alerts = []
    for row in rows:
        for key in ("pending", "due", "deadLetter", "expiredLeases", "retrying"):
            if type(row[key]) is not int or row[key] < 0:
                raise ValueError("Invalid count")
        oldest = row["oldestDueAgeSeconds"]
        if type(oldest) not in (int, float) or not math.isfinite(oldest) or oldest < 0:
            raise ValueError("Invalid age")
        if row["due"] > row["pending"] or row["expiredLeases"] > row["due"] or row["retrying"] > row["pending"]:
            raise ValueError("Inconsistent counts")
        for firing, code, severity in (
            (row["deadLetter"] > 0, "delivery.dead_letter", "critical"),
            (row["expiredLeases"] > 0, "delivery.lease_expired", "warning"),
            (row["due"] >= 100, "delivery.backlog", "warning"),
            (row["due"] > 0 and oldest >= 300, "delivery.overdue", "warning"),
            (row["retrying"] >= 25, "delivery.retry_pressure", "warning"),
        ):
            if firing:
                alerts.append({"code": code, "queue": row["queue"], "severity": severity, "runbook": RUNBOOK})
    if age > 180 or age < -60:
        alerts.append({"code": "delivery.observation_stale", "severity": "critical", "runbook": RUNBOOK})
    return {"code": "delivery.alerts" if alerts else "delivery.healthy", "alerts": alerts}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("snapshot", type=Path)
    args = parser.parse_args()
    try:
        if args.snapshot.stat().st_size > 65536:
            raise ValueError("Oversized snapshot")
        result = evaluate(json.loads(args.snapshot.read_text(encoding="utf-8-sig")))
    except (OSError, ValueError, KeyError, TypeError, AttributeError, OverflowError):
        print(json.dumps({"code": "delivery.observation_unavailable", "severity": "critical", "runbook": RUNBOOK}))
        return 1
    print(json.dumps(result))
    return 2 if result["alerts"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
