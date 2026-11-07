import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
SOURCE=Path(__file__).resolve().parents[1]/'Read-RequestPortalDashboardStorage-Kratos.py'
spec=importlib.util.spec_from_file_location('storage_diagnostic',SOURCE)
diag=importlib.util.module_from_spec(spec);spec.loader.exec_module(diag)
class StorageDiagnosticTests(unittest.TestCase):
    def test_wrong_account_server_and_arguments_stop_before_collection(self):
        for uid,host,args in [(1,'kratos',['diag']),(0,'elsewhere',['diag']),(0,'kratos',['diag','extra'])]:
            with patch.object(diag.os,'geteuid',return_value=uid),patch.object(diag.socket,'gethostname',return_value=host),patch.object(diag.sys,'argv',args):
                with self.assertRaises(RuntimeError):diag.main()
    def test_manifest_rejects_tampering_and_additional_files(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);file=root/'reviewed';file.write_text('reviewed');files={file.name:diag.sha(file)}
            diag.verify(root,files);file.write_text('changed')
            with self.assertRaises(RuntimeError):diag.verify(root,files)
            file.write_text('reviewed');(root/'extra').write_text('extra')
            with self.assertRaises(RuntimeError):diag.verify(root,files)
    def test_failed_transport_does_not_expose_private_output(self):
        result=subprocess.CompletedProcess([],1,stdout=b'PRIVATE',stderr=b'PRIVATE')
        with patch.object(diag.subprocess,'run',return_value=result):
            with self.assertRaises(RuntimeError) as error:diag.request('/health/dashboard')
        self.assertNotIn('PRIVATE',str(error.exception))
    def test_unready_response_is_collected_as_evidence_without_claiming_success(self):
        result=subprocess.CompletedProcess([],0,stdout=b'{"ready":false}\n503',stderr=b'')
        with patch.object(diag.subprocess,'run',return_value=result):
            self.assertEqual({'httpStatus':503,'body':{'ready':False}},diag.request('/health/dashboard'))
if __name__=='__main__':unittest.main()
