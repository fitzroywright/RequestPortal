#!/usr/bin/python3 -I
"""Hash-pinned, no-argument read-only deployment verification. No backup, migration or writes."""
import hashlib
import json
import os
from pathlib import Path
import pwd
import re
import socket
import subprocess
import sys

ROOT = Path('__PREFLIGHT_ROOT__')
MANIFEST_SHA = '__PREFLIGHT_MANIFEST_SHA__'


def protected(path):
    info = path.lstat()
    if path.is_symlink() or info.st_uid != 0 or info.st_mode & 0o022:
        raise RuntimeError('Unsafe verification bundle ownership or permissions.')


def main():
    if len(sys.argv) != 1 or os.geteuid() != 0 or socket.gethostname().split('.')[0] != 'kratos':
        raise RuntimeError('Run the installed no-argument verification command on Kratos.')
    protected(ROOT.parent); protected(ROOT)
    manifest = ROOT / 'manifest.json'; protected(manifest)
    if hashlib.sha256(manifest.read_bytes()).hexdigest() != MANIFEST_SHA:
        raise RuntimeError('The approved verification manifest does not match.')
    files = json.loads(manifest.read_text())['files']
    actual = set()
    for path in ROOT.rglob('*'):
        protected(path)
        if path.is_file() and path != manifest:
            actual.add(path.relative_to(ROOT).as_posix())
    if actual != set(files):
        raise RuntimeError('Unexpected verification dependencies.')
    for name, digest in files.items():
        path = Path(name)
        if path.is_absolute() or '..' in path.parts or hashlib.sha256((ROOT / path).read_bytes()).hexdigest() != digest:
            raise RuntimeError('A verification dependency changed.')
    pid = subprocess.check_output(['systemctl','show','aegis-requestportal','-p','MainPID','--value'], text=True).strip()
    if not pid.isdecimal() or int(pid)<2:
        raise RuntimeError('The Request Portal service must be running.')
    values = dict(item.decode().split('=',1) for item in Path('/proc',pid,'environ').read_bytes().split(b'\0') if b'=' in item)
    env = {'PATH':'/usr/bin:/bin','DOTNET_ENVIRONMENT':'Production','ASPNETCORE_ENVIRONMENT':'Production','DOTNET_NOLOGO':'1'}
    env.update({key:value for key,value in values.items() if key.startswith(('CommonSecrets__','KRB5'))})
    names = {'REQUESTPORTAL_OPENBAO_SECRET_ID'}
    names.update(value for key,value in values.items() if key.startswith('CommonSecrets__') and key.endswith('__SecretIdEnvironmentVariable'))
    for name in names:
        if re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*',name) and name in values: env[name]=values[name]
    user = pwd.getpwnam('aegis'); env['HOME']=user.pw_dir
    def drop():
        os.initgroups('aegis',user.pw_gid);os.setgid(user.pw_gid);os.setuid(user.pw_uid);os.umask(0o077)
    result = subprocess.run(['/usr/bin/dotnet',str(ROOT/'RequestPortal.IntegrationAudit.dll'),'dashboard-verification'],
                            cwd='/opt/aegis/requestportal',env=env,preexec_fn=drop,capture_output=True,text=True,timeout=100)
    if result.returncode:
        raise RuntimeError('Read-only dashboard verification failed; no production write was requested.')
    lines = [line for line in result.stdout.splitlines() if line.startswith('{')]
    report = json.loads(lines[-1])
    if report.get('readOnly') is not True or report.get('status') != 'dashboard-verified':
        raise RuntimeError('Dashboard read-only verification was not confirmed.')
    print(json.dumps(report,indent=2))


if __name__ == '__main__':
    try: main()
    except Exception as error:
        print(json.dumps({'status':'blocked','errorType':type(error).__name__,
                          'detail':str(error) if isinstance(error,RuntimeError) else 'Verification unavailable.'}))
        raise SystemExit(1)
