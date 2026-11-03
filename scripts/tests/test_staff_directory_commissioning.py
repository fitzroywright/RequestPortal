import contextlib
import hashlib
import importlib.util
import io
import json
import pathlib
import tempfile
import types
import unittest
from unittest.mock import patch

SOURCE = pathlib.Path(__file__).resolve().parents[1] / "Commission-StaffDirectory-Kratos.py"
spec = importlib.util.spec_from_file_location("commission", SOURCE)
commission = importlib.util.module_from_spec(spec)
spec.loader.exec_module(commission)


class ConfigurationTests(unittest.TestCase):
    def test_only_exact_approved_non_secret_settings_are_accepted(self):
        text = "".join(k + "=" + v + "\n" for k, v in commission.EXPECTED.items())
        self.assertEqual(commission.EXPECTED, commission.validate_desired(text))
        for altered in [text + "Authentication__StaffDirectory__ClientSecret=not-a-real-secret\n",
                        text + next(iter(commission.EXPECTED)) + "=false\n",
                        text.replace(commission.CLIENT, "unapproved-application"), text.split("\n", 1)[1]]:
            with self.subTest(altered=altered):
                with self.assertRaises(commission.CommissioningError):
                    commission.validate_desired(altered)

    def test_unrelated_configuration_credentials_comments_and_line_endings_are_preserved(self):
        unrelated = '# comment\r\nUNCHANGED="fixture value with spaces;punctuation"\r\nOTHER=original\n'
        original = unrelated + "Authentication__StaffDirectory__Enabled=false\n"
        actual = commission.patch_environment(original, commission.EXPECTED)
        self.assertTrue(actual.startswith(unrelated))
        self.assertNotIn("Authentication__StaffDirectory__Enabled=false", actual)
        self.assertEqual(1, actual.count("Authentication__StaffDirectory__Enabled="))

    def test_unit_environment_serialization_does_not_execute_shell_content(self):
        value = 'fixture $() `code` "quoted" \\ ending'
        quoted = commission.quote_environment(value)
        self.assertTrue(quoted.startswith('"') and quoted.endswith('"'))
        self.assertIn('$()', quoted)
        for invalid in ("line\nline", "line\rline", "value\x00value"):
            with self.assertRaises(commission.CommissioningError):
                commission.quote_environment(invalid)


class ProcedureTests(unittest.TestCase):
    def run_procedure(self, health_failure=False, mismatch=False):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            app = root / "app"; app.mkdir()
            assembly = app / "RequestPortal.Web.dll"; assembly.write_bytes(b"reviewed-compatibility-build")
            env = root / "requestportal.env"
            original = '# preserve\nUNRELATED="fixture-only"\n'
            env.write_text(original)
            installed = root / "commission"; installed.write_text("reviewed command")
            pathlib.Path(str(installed) + ".env").write_text("".join(k + "=" + v + "\n" for k, v in commission.EXPECTED.items()))
            pathlib.Path(str(installed) + ".json").write_text(json.dumps({"sourceCommit": "a" * 40,
                "assemblySha256": "mismatch" if mismatch else hashlib.sha256(assembly.read_bytes()).hexdigest()}))
            audit = root / "audit"
            real_stat = pathlib.Path.stat

            def fake_stat(path, *args, **kwargs):
                value = real_stat(path, *args, **kwargs)
                if path == audit:
                    return types.SimpleNamespace(st_uid=0, st_mode=0o40700)
                return value

            properties = {"WorkingDirectory": str(app), "ExecStart": "/usr/bin/dotnet " + str(assembly),
                          "EnvironmentFiles": str(env) + " (ignore_errors=no)", "User": "aegis", "Group": "aegis",
                          "Environment": "ASPNETCORE_ENVIRONMENT=Production", "LoadCredential": "",
                          "LoadCredentialEncrypted": "", "SetCredential": ""}
            operations = []

            def fake_native(arguments, timeout=30):
                operations.append(arguments)
                if arguments[0] == "/usr/bin/systemd-run":
                    mode = arguments[-1]
                    if mode == "verify-staff-directory":
                        snapshot = pathlib.Path(next(a.split("=", 2)[2] for a in arguments if a.startswith("--property=EnvironmentFile="))).read_text()
                        for key, value in commission.EXPECTED.items():
                            self.assertIn(key + "=" + value, snapshot)
                        self.assertIn("Authentication__StaffOidc__AllowedTenantIds__0=" + commission.TENANT, snapshot)
                    return json.dumps({"status": "ok", "compatibility": "issuer-sub-and-tenant-object-read-only-v1",
                        "staffEnabled": True, "staffClientId": commission.CLIENT, "staffProviderId": "requestportal-staff",
                        "issuer": commission.ISSUER, "authority": commission.ISSUER, "allowedTenantIds": [],
                        "staffBindingCount": 4, "staffBindingFingerprint": "unchanged-four-immutable-bindings",
                        "searchMatches": 1, "individualLookupVerified": True})
                self.assertEqual(["/usr/bin/systemctl", "restart", "aegis-requestportal"], arguments)
                self.assertTrue(any(a[-1] == "verify-staff-directory" for a in operations[:-1]))
                return ""

            health = unittest.mock.Mock(side_effect=[commission.CommissioningError("fixture health failure"), None] if health_failure else None)
            output = io.StringIO()
            with patch.object(commission, "__file__", str(installed)), patch.object(commission, "APPLICATION", app), \
                 patch.object(commission, "ASSEMBLY", assembly), patch.object(commission, "ENVIRONMENT", env), \
                 patch.object(commission, "AUDIT_ROOT", audit), patch.object(commission, "protected_file"), \
                 patch.object(commission.os, "geteuid", return_value=0), patch.object(commission.socket, "gethostname", return_value="kratos"), \
                 patch.object(commission, "property_value", side_effect=lambda name: properties[name]), \
                 patch.object(commission, "native", side_effect=fake_native), patch.object(commission, "verify_health", health), \
                 patch.object(commission.sys, "argv", [str(installed), "--apply"]), \
                 patch.object(pathlib.Path, "stat", fake_stat), contextlib.redirect_stdout(output):
                if health_failure or mismatch:
                    with self.assertRaises(commission.CommissioningError):
                        commission.main()
                else:
                    commission.main()
            if mismatch:
                self.assertEqual(original, env.read_text()); self.assertEqual([], operations)
                return
            evidence = json.loads(next(audit.glob("*/result.json")).read_text())
            self.assertFalse(evidence["identityRecordsModifiedByProcedure"])
            self.assertEqual(original, pathlib.Path(evidence["backup"]).read_text())
            self.assertEqual(0, pathlib.Path(evidence["backup"]).stat().st_mode & 0o077)
            if health_failure:
                self.assertEqual(original, env.read_text()); self.assertEqual("rolled-back", evidence["status"])
                self.assertEqual(2, sum(args[0] == "/usr/bin/systemctl" for args in operations))
            else:
                self.assertTrue(env.read_text().startswith(original)); self.assertEqual("commissioned", evidence["status"])
                self.assertEqual(1, sum(args[0] == "/usr/bin/systemctl" for args in operations))
            self.assertNotIn("fixture-only", output.getvalue())

    def test_success_backs_up_then_preflights_applies_and_restarts_only_portal(self):
        self.run_procedure()

    def test_failed_health_restores_exact_configuration_and_restarts_portal(self):
        self.run_procedure(health_failure=True)

    def test_unreviewed_binary_is_rejected_before_any_privileged_operation(self):
        self.run_procedure(mismatch=True)


if __name__ == "__main__":
    unittest.main()
