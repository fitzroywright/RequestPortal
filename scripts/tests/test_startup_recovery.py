import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

SOURCE=Path(__file__).resolve().parents[1]/'Recover-RequestPortalStartup-Kratos.py'
spec=importlib.util.spec_from_file_location('startup_recovery',SOURCE)
recovery=importlib.util.module_from_spec(spec);spec.loader.exec_module(recovery)

class StartupRecoveryTests(unittest.TestCase):
    def test_root_hostname_and_no_argument_guards_precede_all_operations(self):
        cases=[(1,'kratos',['recovery']),(0,'another-server',['recovery']),(0,'kratos',['recovery','extra'])]
        for uid,host,args in cases:
            with patch.object(recovery.os,'geteuid',return_value=uid),patch.object(recovery.socket,'gethostname',return_value=host),patch.object(recovery.sys,'argv',args):
                with self.assertRaises(RuntimeError):recovery.recover(Path('/unused'))
    def test_manifest_rejects_changed_extra_and_symlink_files(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);file=root/'reviewed.dll';file.write_bytes(b'reviewed')
            files={file.name:recovery.digest(file)};recovery.verify_files(root,files)
            file.write_bytes(b'tampered')
            with self.assertRaises(RuntimeError):recovery.verify_files(root,files)
            file.write_bytes(b'reviewed');(root/'extra').write_bytes(b'extra')
            with self.assertRaises(RuntimeError):recovery.verify_files(root,files)
            (root/'extra').unlink();(root/'link').symlink_to(file)
            with self.assertRaises(RuntimeError):recovery.verify_files(root,{**files,'link':recovery.digest(file)})
    def test_command_provider_failure_never_exposes_private_output(self):
        result=subprocess.CompletedProcess([],1,stdout=b'PRIVATE',stderr=b'Password=PRIVATE')
        with patch.object(recovery.subprocess,'run',return_value=result):
            with self.assertRaises(RuntimeError) as error:recovery.command(['fixture'])
        self.assertNotIn('PRIVATE',str(error.exception))
    def test_readonly_audit_failure_suppresses_both_output_streams(self):
        result=subprocess.CompletedProcess([],1,stdout=b'PRIVATE',stderr=b'PRIVATE')
        user=type('User',(),{'pw_dir':'/unused'})()
        with patch.object(recovery.pwd,'getpwnam',return_value=user),patch.object(recovery.subprocess,'run',return_value=result) as run:
            with self.assertRaises(RuntimeError) as error:recovery.run_audit(Path('/reviewed'),'dashboard-verification',{})
        self.assertNotIn('PRIVATE',str(error.exception))
        self.assertEqual('dashboard-verification',run.call_args.args[0][-1])
        self.assertIsNotNone(run.call_args.kwargs['preexec_fn'])

if __name__=='__main__':unittest.main()
