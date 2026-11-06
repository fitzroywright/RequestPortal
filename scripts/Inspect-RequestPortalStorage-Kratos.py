#!/usr/bin/python3 -I
"""Pinned read-only NAS inspection and SharePoint inventory. No source writes or cutover."""
import hashlib
import importlib.util
from importlib.machinery import SourceFileLoader
import json
import os
from pathlib import Path
import pwd
import shutil
import socket
import subprocess
import sys
import tempfile

MANIFEST_SHA = '__MANIFEST_SHA__'
PREFLIGHT_SHA = 'dd91aa2c3e0956efe3138cad7fc8d9e0cca77dbd67c745f414eefab57db469d2'
sys.dont_write_bytecode = True

def digest(path):
    result = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            result.update(block)
    return result.hexdigest()

def require(condition, message):
    if not condition:
        raise RuntimeError(message)

def verify(root, files):
    require(not root.is_symlink(), 'Bundle links are prohibited.')
    actual = set()
    for path in root.rglob('*'):
        require(not path.is_symlink(), 'Bundle links are prohibited.')
        if path.is_file():
            actual.add(path.relative_to(root).as_posix())
    require(actual == set(files), 'Inventory bundle file list changed.')
    for name, sha in files.items():
        relative = Path(name)
        require(not relative.is_absolute() and '..' not in relative.parts, 'Unsafe bundle path.')
        require(digest(root / relative) == sha, 'Inventory artifact checksum mismatch.')

def environment():
    helper = Path('/usr/local/sbin/aegis-requestportal-postgres-preflight')
    require(not helper.is_symlink() and helper.stat().st_uid == 0 and
            not helper.stat().st_mode & 0o022 and digest(helper) == PREFLIGHT_SHA,
            'The approved root-owned environment helper changed.')
    spec = importlib.util.spec_from_loader('approved_preflight', SourceFileLoader('approved_preflight', str(helper)))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.validate_bundle()
    return module.service_environment()

def inspect(source):
    require(len(sys.argv) == 1 and os.geteuid() == 0 and socket.gethostname().split('.')[0] == 'kratos',
            'Run this fixed no-argument inspection interactively as root on Kratos.')
    os.umask(0o077)
    manifest = source / 'manifest.json'
    require(digest(manifest) == MANIFEST_SHA, 'The inventory manifest changed.')
    plan = json.loads(manifest.read_text())
    files = plan['files']
    verify(source / 'bundle', files)
    account, operator = pwd.getpwnam('aegis'), pwd.getpwnam('fwright')
    work = Path(tempfile.mkdtemp(prefix='requestportal-storage-inspection-', dir='/var/tmp'))
    bundle = work / 'bundle'
    shutil.copytree(source / 'bundle', bundle)
    verify(bundle, files)
    for path in [work, bundle, *bundle.rglob('*')]:
        os.chown(path, 0, account.pw_gid)
        os.chmod(path, 0o750 if path.is_dir() else 0o640)
    env = environment()
    root = Path('/var/lib/requestportal-storage-migration')
    root.mkdir(mode=0o711, exist_ok=True)
    require(not root.is_symlink() and root.stat().st_uid == 0 and not root.stat().st_mode & 0o022,
            'The private inventory state root must be root owned and protected.')
    state = Path(tempfile.mkdtemp(prefix='inventory-', dir=root))
    os.chown(state, account.pw_uid, account.pw_gid)
    result = {'status': 'inspection-in-progress', 'stateDirectory': str(state),
              'sourceWrites': False, 'storageChanges': False, 'deploymentAttempted': False}
    try:
        installed = digest(Path('/opt/aegis/requestportal/RequestPortal.Web.dll'))
        result['applicationSha256'] = installed
        result['installedCommit'] = plan.get('previousCommit') if installed == plan.get('previousApplicationSha256') else None
        for path, key in [('/health', 'httpsHealth'), ('/health/dashboard', 'dashboardReadinessHttp')]:
            response = subprocess.run(['/usr/bin/curl', '--silent', '--show-error', '--max-time', '20',
                                       '--output', '/dev/null', '--write-out', '%{http_code}',
                                       'https://requestportal.ffpja.org' + path], capture_output=True, timeout=25)
            result[key] = int(response.stdout) if response.returncode == 0 and response.stdout.isdigit() else None
        layout = subprocess.run(['/usr/bin/python3', '-I', str(bundle / 'layout.py')],
                                capture_output=True, timeout=120)
        require(layout.returncode == 0, 'Read-only NAS inspection failed; no migration was attempted.')
        layout_summary = json.loads(layout.stdout.decode().splitlines()[-1])
        require(layout_summary.get('status') == 'read-only-layout-evidence-collected',
                'Read-only NAS inspection did not complete.')
        layout_report = Path(layout_summary['privateExport']) / 'report.json'
        require(digest(layout_report) == layout_summary['reportSha256'], 'NAS report checksum mismatch.')
        result['layout'] = json.loads(layout_report.read_text())
        def drop():
            os.initgroups(account.pw_name, account.pw_gid)
            os.setgid(account.pw_gid)
            os.setuid(account.pw_uid)
            os.umask(0o077)
        # Code stays root owned. Inventory executes as the existing application identity.
        with (work / 'inventory-private.log').open('wb') as output:
            process = subprocess.run(['/usr/bin/dotnet', str(bundle / 'tool/RequestPortal.StorageMigration.dll'),
                                      'inventory', '--state', str(state)],
                                     cwd='/opt/aegis/requestportal', env=env, preexec_fn=drop,
                                     stdout=output, stderr=output, timeout=43200)
        require(process.returncode == 0, 'SharePoint inventory did not complete; private diagnostics were retained.')
        report = json.loads((state / 'last-report.json').read_text())
        require(report.get('phase') == 'inventory' and report.get('sourceWrites') is False and
                report.get('sourceDeletes') == 0 and report.get('providerChanged') is False,
                'The inventory did not confirm its read-only source scope.')
        result['inventory'] = report
        result['status'] = 'read-only-storage-inventory-collected'
    except Exception as error:
        result['status'] = 'blocked'
        result['errorType'] = type(error).__name__
        result['detail'] = str(error) if isinstance(error, RuntimeError) else 'Inspection failed; private diagnostics retained.'
    finally:
        os.chmod(work, 0o700)
        export = Path(tempfile.mkdtemp(prefix='requestportal-storage-evidence-', dir='/var/tmp'))
        report_path = export / 'report.json'
        report_path.write_text(json.dumps(result, indent=2) + '\n')
        for name in ['inventory.json', 'last-report.json', 'reference-reconciliation.json']:
            if (state / name).is_file():
                shutil.copyfile(state / name, export / name)
        for path in export.iterdir():
            os.chown(path, operator.pw_uid, operator.pw_gid)
            os.chmod(path, 0o600)
        os.chown(export, operator.pw_uid, operator.pw_gid)
        os.chmod(export, 0o700)
        print(json.dumps({'status': result['status'], 'privateExport': str(export),
                          'reportSha256': digest(report_path), 'productionWrites': False}), flush=True)
    return 0 if result['status'] == 'read-only-storage-inventory-collected' else 1

if __name__ == '__main__':
    try:
        raise SystemExit(inspect(Path(__file__).resolve().parent))
    except Exception as error:
        print(json.dumps({'status': 'blocked', 'errorType': type(error).__name__,
                          'detail': str(error) if isinstance(error, RuntimeError) else 'Inspection failed; private details suppressed.'}))
        raise SystemExit(1)
