#!/usr/bin/env python3
"""Synthetic evidence only. No SSH, service configuration or production databases."""
import importlib.util
import json
import datetime
from pathlib import Path
import sys
import unittest
from unittest.mock import patch
from types import SimpleNamespace

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("integration_audit", Path(__file__).resolve().parents[2] / "tools/RequestPortal.IntegrationAudit/run-readonly.py")
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class AuditTests(unittest.TestCase):
    def test_service_environment_forwards_only_configured_bootstrap_credentials(self):
        raw = b"CommonSecrets__Providers__Primary__Settings__SecretIdEnvironmentVariable=CUSTOM_BOOTSTRAP\0CUSTOM_BOOTSTRAP=SYNTHETIC-PROOF\0REQUESTPORTAL_OPENBAO_SECRET_ID=SYNTHETIC-DEFAULT\0UNRELATED_SECRET=NOT-FORWARDED\0"
        with patch.object(audit.subprocess,"check_output",return_value="123\n"), patch.object(audit.pathlib.Path,"read_bytes",return_value=raw):
            env = audit.service_environment("aegis-requestportal")
        self.assertEqual("SYNTHETIC-PROOF",env["CUSTOM_BOOTSTRAP"])
        self.assertEqual("SYNTHETIC-DEFAULT",env["REQUESTPORTAL_OPENBAO_SECRET_ID"])
        self.assertNotIn("UNRELATED_SECRET",env)

    def test_failed_child_reports_exception_type_without_provider_payload(self):
        result=SimpleNamespace(returncode=1,stdout="TOP-SECRET",stderr='TOP-SECRET\n{"status":"failed","errorType":"PostgresException","message":"TOP-SECRET"}')
        account=SimpleNamespace(pw_dir="/tmp",pw_uid=1,pw_gid=1)
        with patch.object(audit.pwd,"getpwnam",return_value=account),patch.object(audit,"service_environment",return_value={}),patch.object(audit.subprocess,"run",return_value=result):
            with self.assertRaises(RuntimeError) as error:
                audit.run_audit("portal","aegis","aegis-requestportal","/tmp")
        self.assertIn("PostgresException",str(error.exception))
        self.assertNotIn("TOP-SECRET",str(error.exception))

    def source(self, **values):
        row = {"requestId": "11111111-1111-4111-8111-111111111111", "reference": "QI-TEST", "status": "Synchronized",
               "applicationId": "123", "applicationNumber": "KGN/26/GR/0100", "submissionEvents": 1,
               "integrationEvents": 1, "errorClassification": None}
        row["submittedUtc"] = (datetime.datetime.now(datetime.timezone.utc)-datetime.timedelta(hours=1)).isoformat()
        return dict(row, **values)

    def target(self, **values):
        return dict({"requestId": self.source()["requestId"], "portalReference": "QI-TEST", "applicationId": "123",
                     "applicationNumber": "KGN/26/GR/0100", "archived": False}, **values)

    def report(self, source, targets):
        return audit.reconcile({"records": [source]}, {"records": targets})

    def test_verified_sync_requires_matching_immutable_identity_and_generated_reference(self):
        self.assertEqual(1, self.report(self.source(), [self.target()])["verifiedSynchronized"])
        self.assertEqual(0, self.report(self.source(applicationNumber=None), [self.target(applicationNumber=None)])["verifiedSynchronized"])

    def test_commit_without_acknowledgement_is_preserved_for_reconciliation(self):
        result = self.report(self.source(status="Failed", applicationId=None), [self.target()])
        self.assertEqual(0, result["missingVerifiedTarget"])
        self.assertEqual("CommittedNotAcknowledged", result["records"][0]["outcome"])

    def test_missing_source_queue_and_false_synchronized_status_are_distinct(self):
        result = self.report(self.source(status="Pending", integrationEvents=0), [])
        self.assertEqual("NeverQueuedForTransfer", result["records"][0]["outcome"])
        result = self.report(self.source(), [])
        self.assertEqual("SynchronizedWithoutTarget", result["records"][0]["outcome"])
        self.assertEqual(1, result["missingVerifiedTarget"])

    def test_conflicting_and_duplicate_destination_records_block_retries(self):
        for targets in ([self.target(), self.target(applicationId="456")], [self.target(requestId="different")]):
            result = self.report(self.source(), targets)
            self.assertEqual("AmbiguousDestination", result["records"][0]["outcome"])
            self.assertEqual(0, result["missingVerifiedTarget"])

    def test_archived_destination_does_not_count_as_verified_active_record(self):
        self.assertEqual(1, self.report(self.source(), [self.target(archived=True)])["missingVerifiedTarget"])

    def test_intakes_with_missing_source_requests_are_not_silently_excluded(self):
        result = self.report(self.source(sourceRequestExists=False), [])
        self.assertEqual(1,result["inspected"])
        self.assertEqual("MissingSourceRequest",result["records"][0]["outcome"])

    def test_recent_pending_submission_is_distinguished_from_stale_missing_transfer(self):
        recent = self.source(status="Pending",integrationEvents=0,submittedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
        result = self.report(recent,[])
        self.assertEqual("PendingWithinExpectedInterval",result["records"][0]["outcome"])
        self.assertEqual(0,result["recordsRequiringRecoveryReview"])
        self.assertEqual(0,result["missingBeyondExpectedInterval"])

    def test_journal_output_contains_no_exception_payloads_or_secrets(self):
        credential = "99999999-9999-4999-8999-999999999999"
        message = "SqlException TOP-SECRET password=TOP-SECRET applicant@example.com Parish Error Number: 547 secret_id=" + credential
        raw = json.dumps({"MESSAGE": message, "__REALTIME_TIMESTAMP": "1"})
        with patch.object(audit.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout=raw)):
            output = audit.journal_evidence("aegis-requestportal")
        self.assertNotIn("TOP-SECRET", json.dumps(output))
        self.assertNotIn("applicant@example.com", json.dumps(output))
        self.assertNotIn(credential, json.dumps(output))
        self.assertIn("Error Number: 547", output["sanitizedEvents"][0]["types"])


if __name__ == "__main__":
    unittest.main()
