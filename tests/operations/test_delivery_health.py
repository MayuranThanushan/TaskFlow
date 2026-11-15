import copy
from datetime import datetime, timezone, timedelta
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("delivery_health", Path(__file__).resolve().parents[2] / "scripts/check_delivery_health.py")
health = importlib.util.module_from_spec(spec)
spec.loader.exec_module(health)

class DeliveryHealthTests(unittest.TestCase):
    def setUp(self):
        self.now = datetime(2026, 11, 15, 8, tzinfo=timezone.utc)
        self.snapshot = {"version": 1, "observedAtUtc": self.now.isoformat(), "queues": [
            dict(queue=q, pending=0, due=0, deadLetter=0, expiredLeases=0, retrying=0, oldestDueAgeSeconds=0) for q in sorted(health.QUEUES)]}

    def test_empty_fresh_inventory_is_healthy(self):
        self.assertEqual("delivery.healthy", health.evaluate(self.snapshot, self.now)["code"])

    def test_future_quiet_or_digest_work_is_not_overdue(self):
        self.snapshot["queues"][0]["pending"] = 500
        self.assertEqual([], health.evaluate(self.snapshot, self.now)["alerts"])

    def test_fault_injection_produces_actionable_bounded_alerts(self):
        row = self.snapshot["queues"][0]
        row.update(pending=100, due=100, deadLetter=1, expiredLeases=1, retrying=25, oldestDueAgeSeconds=300)
        result = health.evaluate(self.snapshot, self.now)
        self.assertEqual({"delivery.dead_letter", "delivery.lease_expired", "delivery.backlog", "delivery.overdue", "delivery.retry_pressure"}, {a["code"] for a in result["alerts"]})
        self.assertTrue(all(a["queue"] == row["queue"] and a["runbook"] == health.RUNBOOK for a in result["alerts"]))
        row.update(pending=0, due=0, deadLetter=0, expiredLeases=0, retrying=0, oldestDueAgeSeconds=0)
        self.assertEqual([], health.evaluate(self.snapshot, self.now)["alerts"])

    def test_stale_and_future_samples_cannot_report_healthy(self):
        for delta in (timedelta(seconds=181), timedelta(seconds=-61)):
            result = health.evaluate(self.snapshot, self.now + delta)
            self.assertEqual("delivery.observation_stale", result["alerts"][0]["code"])

    def test_partial_unknown_duplicate_and_invalid_samples_fail_closed(self):
        bad = []
        for value in (-1, True, "1"):
            snapshot = copy.deepcopy(self.snapshot); snapshot["queues"][0]["pending"] = value; bad.append(snapshot)
        for value in (float("nan"), float("inf"), -1):
            snapshot = copy.deepcopy(self.snapshot); snapshot["queues"][0]["oldestDueAgeSeconds"] = value; bad.append(snapshot)
        snapshot = copy.deepcopy(self.snapshot); snapshot["queues"].pop(); bad.append(snapshot)
        snapshot = copy.deepcopy(self.snapshot); snapshot["queues"][0]["queue"] = "PRIVATE_CANARY"; bad.append(snapshot)
        snapshot = copy.deepcopy(self.snapshot); snapshot["queues"][0]["due"] = 1; bad.append(snapshot)
        snapshot = copy.deepcopy(self.snapshot); snapshot["observedAtUtc"] = "2026-11-15T08:00:00"; bad.append(snapshot)
        for snapshot in bad:
            with self.assertRaises((ValueError, KeyError, TypeError)):
                health.evaluate(snapshot, self.now)
