import json
from pathlib import Path
import unittest
ROOT = Path(__file__).resolve().parents[2]
class LocalFeatureDefaults(unittest.TestCase):
    def test_development_company_routes_work_without_preview_shell_flags(self):
        dev=json.loads((ROOT/'TaskFlow-BE/TaskFlow.Api/appsettings.Development.json').read_text())
        for key in ['WorkspaceReads','WorkspaceWrites','WorkspaceLifecycle']: self.assertTrue(dev[key]['Enabled'])
    def test_production_defaults_still_require_explicit_company_rollout(self):
        base=json.loads((ROOT/'TaskFlow-BE/TaskFlow.Api/appsettings.json').read_text())
        for key in ['WorkspaceReads','WorkspaceWrites','WorkspaceLifecycle']: self.assertFalse(base[key]['Enabled'])
