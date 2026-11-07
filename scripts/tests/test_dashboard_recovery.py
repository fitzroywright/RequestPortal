import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

SOURCE = Path(__file__).resolve().parents[1] / 'Recover-RequestPortalDashboard-Kratos.py'
spec = importlib.util.spec_from_file_location('dashboard_recovery', SOURCE)
recovery = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recovery)


class DashboardRecoveryTests(unittest.TestCase):
    def test_kestrel_probe_passes_the_production_host_filter_and_preserves_fail_on_http_errors(self):
        observed = []
        def command(args):
            observed.append(args)
            if '-H' not in args or args[args.index('-H') + 1] != 'Host: requestportal.ffpja.org':
                raise RuntimeError('HTTP 400: invalid host')
            self.assertIn('--fail', args)
            return b'{"status":"Healthy","application":"RequestPortal"}'
        result = recovery.kestrel_health(SimpleNamespace(command=command), 'http://127.0.0.1:5401/health')
        self.assertEqual('Healthy', result['status'])
        self.assertEqual(1, len(observed))

    def test_kestrel_probe_still_rejects_unhealthy_dependencies_or_another_application(self):
        for body in [b'{"status":"Unhealthy","application":"RequestPortal"}', b'{"status":"Healthy","application":"Other"}']:
            with self.assertRaises(RuntimeError):
                recovery.kestrel_health(SimpleNamespace(command=lambda args: body), 'http://127.0.0.1:5401/health')

    def test_account_host_and_arguments_are_checked_before_any_lock_or_write(self):
        for uid, host, args in [(1, 'kratos', ['recover']), (0, 'elsewhere', ['recover']), (0, 'kratos', ['recover', 'extra'])]:
            with patch.object(recovery.os, 'geteuid', return_value=uid), \
                 patch.object(recovery.socket, 'gethostname', return_value=host), \
                 patch.object(recovery.sys, 'argv', args), patch.object(recovery.os, 'open') as opened:
                with self.assertRaises(RuntimeError): recovery.recover(Path('/untrusted'))
                opened.assert_not_called()

    def test_reviewed_bundle_rejects_modified_extra_and_linked_artifacts(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); artifact = root / 'reviewed.dll'; artifact.write_bytes(b'reviewed')
            files = {artifact.name: recovery.sha(artifact)}
            recovery.verify(root, files)
            artifact.write_bytes(b'changed')
            with self.assertRaises(RuntimeError): recovery.verify(root, files)
            artifact.write_bytes(b'reviewed'); (root / 'extra').write_bytes(b'extra')
            with self.assertRaises(RuntimeError): recovery.verify(root, files)
            (root / 'extra').unlink(); (root / 'link').symlink_to(artifact)
            with self.assertRaises(RuntimeError): recovery.verify(root, files)

    def test_missing_or_invalid_restore_evidence_blocks_before_backup_read(self):
        good = {'status': 'isolated-restore-verified', 'synthetic': False, 'archiveSha256': recovery.BACKUP_SHA,
                'reportSha256': recovery.BACKUP_REPORT_SHA, 'verifiedTables': 57, 'migrationsVerified': True}
        recovery.validate_restore(good)
        for changes in [{'synthetic': True}, {'archiveSha256': 'a' * 64}, {'reportSha256': 'b' * 64},
                        {'verifiedTables': 0}, {'migrationsVerified': False}]:
            with patch.object(recovery, 'sha') as digest:
                with self.assertRaises(RuntimeError): recovery.verify_backup({**good, **changes})
                digest.assert_not_called()

    def test_different_release_or_restarted_process_blocks_the_writer_stop(self):
        for pid, digest in [('other', 'old'), ('original', 'changed')]:
            properties = {'ActiveState': 'active', 'SubState': 'running', 'MainPID': '42' if pid == 'other' else '41'}
            with patch.object(recovery, 'service_properties', return_value=properties), patch.object(recovery, 'sha', return_value=digest):
                with self.assertRaisesRegex(RuntimeError, 'changed during geography preparation'):
                    recovery.assert_same_release(Path('/app.dll'), 'old', '41')


if __name__ == '__main__': unittest.main()
