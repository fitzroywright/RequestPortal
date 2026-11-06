import hashlib
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[1] / 'Inspect-RequestPortalStorage-Kratos.py'
spec = importlib.util.spec_from_file_location('storage_inspection', SCRIPT)
inspection = importlib.util.module_from_spec(spec)
spec.loader.exec_module(inspection)


class StorageInspectionTests(unittest.TestCase):
    def test_exact_pinned_bundle_verifies_and_modified_file_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            file = root / 'tool.dll'
            file.write_bytes(b'validated')
            files = {'tool.dll': hashlib.sha256(file.read_bytes()).hexdigest()}
            inspection.verify(root, files)
            file.write_bytes(b'modified')
            with self.assertRaisesRegex(RuntimeError, 'checksum'):
                inspection.verify(root, files)

    def test_extra_files_and_symbolic_links_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / 'extra').write_bytes(b'not allowed')
            with self.assertRaisesRegex(RuntimeError, 'file list'):
                inspection.verify(root, {})
            (root / 'link').symlink_to(root / 'extra')
            with self.assertRaisesRegex(RuntimeError, 'links'):
                inspection.verify(root, {})

    def test_non_root_execution_is_rejected_before_side_effects(self):
        with patch.object(inspection.os, 'geteuid', return_value=123), \
             patch.object(inspection.sys, 'argv', ['inspect.py']), \
             patch.object(inspection.tempfile, 'mkdtemp') as create:
            with self.assertRaisesRegex(RuntimeError, 'root on Kratos'):
                inspection.inspect(Path('/untrusted'))
            create.assert_not_called()

if __name__ == '__main__':
    unittest.main()
