import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

SCRIPTS=Path(__file__).resolve().parents[1]
def module(name):
    spec=importlib.util.spec_from_file_location(name,SCRIPTS/(name+'.py'))
    value=importlib.util.module_from_spec(spec);spec.loader.exec_module(value);return value
runner=module('Run-RequestPortalPostgresPreflight-Kratos')
verify=module('Verify-RequestPortalPostgresBackup')

class PreflightTests(unittest.TestCase):
    def test_postgres13_uses_available_newer_client_and_never_an_older_client(self):
        for source in range(13, 19):
            self.assertEqual(max(17, source), runner.backup_client_major(source))
            self.assertGreaterEqual(runner.backup_client_major(source), source)
    def test_client_selection_rejects_unknown_or_untyped_server_versions(self):
        for source in (12, 19, '13', True, None):
            with self.assertRaises(RuntimeError): runner.backup_client_major(source)
    def test_manifest_rejects_tampered_and_extra_dependencies(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);binary=root/'RequestPortal.IntegrationAudit.dll';binary.write_bytes(b'reviewed')
            manifest=root/'manifest.json';manifest.write_text(json.dumps({'files':{binary.name:runner.digest(binary)}}))
            with patch.object(runner,'ROOT',root),patch.object(runner,'MANIFEST_SHA',runner.digest(manifest)),patch.object(runner,'protected'):
                runner.validate_bundle();binary.write_bytes(b'changed')
                with self.assertRaises(RuntimeError):runner.validate_bundle()
                binary.write_bytes(b'reviewed');(root/'unexpected.py').write_text('x')
                with self.assertRaises(RuntimeError):runner.validate_bundle()
    def test_environment_is_allowlisted_and_does_not_copy_arbitrary_secret_variables(self):
        raw=b'CommonSecrets__WorkloadName=RequestPortal\0REQUESTPORTAL_OPENBAO_SECRET_ID=BOOTSTRAP\0DATABASE_PASSWORD=PRIVATE\0LD_PRELOAD=PRIVATE\0'
        with patch.object(runner.subprocess,'check_output',return_value='123\n'),patch.object(runner.Path,'read_bytes',return_value=raw):
            env=runner.service_environment()
        self.assertEqual('RequestPortal',env['CommonSecrets__WorkloadName'])
        self.assertNotIn('DATABASE_PASSWORD',env);self.assertNotIn('LD_PRELOAD',env)
    def test_reader_failure_never_forwards_provider_output(self):
        user=type('User',(),{'pw_dir':'/unused','pw_gid':1,'pw_uid':1})()
        result=subprocess.CompletedProcess([],1,stdout='PRIVATE',stderr='PRIVATE')
        with patch.object(runner.pwd,'getpwnam',return_value=user),patch.object(runner.subprocess,'run',return_value=result):
            with self.assertRaises(RuntimeError) as error:runner.run_reader(['database-preflight'],{})
        self.assertNotIn('PRIVATE',str(error.exception))
    def test_reader_exposes_safe_stage_and_code_without_raw_driver_messages(self):
        user=type('User',(),{'pw_dir':'/unused','pw_gid':1,'pw_uid':1})()
        result=subprocess.CompletedProcess([],1,stdout='PRIVATE',stderr='PRIVATE\n'+json.dumps({'status':'failed','errorType':'PostgresException','stage':'migration-privileges','code':'SQL_42501','message':'PRIVATE'}))
        with patch.object(runner.pwd,'getpwnam',return_value=user),patch.object(runner.subprocess,'run',return_value=result):
            with self.assertRaises(RuntimeError) as error:runner.run_reader(['database-preflight'],{})
        self.assertIn('SQL_42501',str(error.exception));self.assertIn('migration-privileges',str(error.exception));self.assertNotIn('PRIVATE',str(error.exception))
    def test_privilege_guard_exposes_only_explicit_structural_observations(self):
        user=type('User',(),{'pw_dir':'/unused','pw_gid':1,'pw_uid':1})()
        result=subprocess.CompletedProcess([],1,stdout='',stderr=json.dumps({'status':'failed','errorType':'InvalidOperationException','stage':'migration-privileges','code':'PG_PRIVILEGES_MISSING','details':{'schemaCreate':False}}))
        with patch.object(runner.pwd,'getpwnam',return_value=user),patch.object(runner.subprocess,'run',return_value=result):
            with self.assertRaises(RuntimeError) as error:runner.run_reader(['database-preflight'],{})
        self.assertIn('schemaCreate',str(error.exception))
    def test_identifier_quoting_cannot_turn_metadata_into_sql(self):
        self.assertEqual('"x""; DROP DATABASE production;--"',verify.quote('x"; DROP DATABASE production;--'))
    def test_verifier_rejects_report_mismatch_before_starting_postgres(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);root.chmod(0o700)
            for name in ['report.json','requestportal.dump']:(root/name).write_text('{}');(root/name).chmod(0o600)
            with patch.object(verify.subprocess,'run') as run:
                with self.assertRaises(RuntimeError):verify.verify(root,'wrong')
            run.assert_not_called()

if __name__=='__main__':unittest.main()
