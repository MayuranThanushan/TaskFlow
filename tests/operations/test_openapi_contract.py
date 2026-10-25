import copy
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
from export_openapi import check_snapshot, validate


class OpenApiDriftTests(unittest.TestCase):
    def setUp(self):
        self.document = json.loads((ROOT / "docs/contracts/openapi.json").read_text(encoding="utf-8"))

    def test_checked_in_inventory_and_server_normalization(self):
        validate(self.document)
        live = copy.deepcopy(self.document)
        live["servers"] = [{"url": "http://localhost:5140"}]
        check_snapshot(self.document, live)

    def test_route_response_security_and_query_changes_are_rejected(self):
        for change in ["route", "response", "security", "query"]:
            live = copy.deepcopy(self.document)
            if change == "route": live["paths"].pop("/api/v1/dashboard")
            if change == "response": live["components"]["schemas"]["ProfileDto"]["properties"]["id"]["type"] = "integer"
            if change == "security": live["paths"]["/api/v1/auth/logout"]["post"]["security"] = []
            if change == "query": live["paths"]["/api/v1/projects"]["get"]["parameters"][0]["name"] = "wrong"
            with self.subTest(change=change), self.assertRaises(ValueError): check_snapshot(self.document, live)
