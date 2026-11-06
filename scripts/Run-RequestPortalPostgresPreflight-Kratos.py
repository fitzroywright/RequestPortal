#!/usr/bin/python3 -I
"""Fixed-scope administrator command. No migrations, queue dispatch or production business writes.
Release preparation pins ROOT and MANIFEST_SHA. No sudo permissions are created by this command.
"""
import datetime
import hashlib
import json
import os
from pathlib import Path
import pwd
import re
import shutil
import socket
import subprocess
import sys
import tempfile

ROOT = Path('__PREFLIGHT_ROOT__')
MANIFEST_SHA = '__PREFLIGHT_MANIFEST_SHA__'
CHECKER_SHA = '__PREFLIGHT_CHECKER_SHA__'
SERVICE = 'aegis-requestportal'
ACCOUNT = 'aegis'
OPERATOR = 'fwright'


def digest(path):
    result = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            result.update(block)
    return result.hexdigest()


def protected(path):
    info = path.lstat()
    if path.is_symlink() or info.st_uid != 0 or info.st_mode & 0o022:
        raise RuntimeError('Preflight code must be root-owned and not writable by other users.')


def validate_bundle():
    protected(ROOT.parent)
    protected(ROOT)
    manifest = ROOT / 'manifest.json'
    protected(manifest)
    if digest(manifest) != MANIFEST_SHA:
        raise RuntimeError('The reviewed preflight manifest does not match.')
    files = json.loads(manifest.read_text())['files']
    actual = set()
    for path in ROOT.rglob('*'):
        protected(path)
        if path.is_file() and path != manifest:
            actual.add(path.relative_to(ROOT).as_posix())
    if actual != set(files):
        raise RuntimeError('The reviewed preflight bundle is incomplete or has extra files.')
    for name, expected in files.items():
        path = Path(name)
        if path.is_absolute() or '..' in path.parts or path.as_posix() != name:
            raise RuntimeError('Unsafe manifest path.')
        if digest(ROOT / path) != expected:
            raise RuntimeError('A reviewed preflight dependency does not match.')


def service_environment():
    pid = subprocess.check_output(['systemctl', 'show', SERVICE, '-p', 'MainPID', '--value'], text=True).strip()
    if not pid.isdecimal() or int(pid) < 2:
        raise RuntimeError('The current production service must be running for configuration-consistent backup.')
    initial = dict(item.decode().split('=', 1) for item in Path('/proc', pid, 'environ').read_bytes().split(b'\0') if b'=' in item)
    env = {'PATH': '/usr/bin:/bin', 'DOTNET_NOLOGO': '1', 'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
           'DOTNET_ENVIRONMENT': 'Production', 'ASPNETCORE_ENVIRONMENT': 'Production'}
    for key, value in initial.items():
        if key.startswith(('CommonSecrets__', 'KRB5')):
            env[key] = value
    names = {'REQUESTPORTAL_OPENBAO_SECRET_ID'}
    names.update(value for key, value in initial.items() if key.startswith('CommonSecrets__') and key.endswith('__SecretIdEnvironmentVariable'))
    for name in names:
        if re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', name) and name in initial:
            env[name] = initial[name]
    return env


def run_reader(arguments, env):
    user = pwd.getpwnam(ACCOUNT)
    env = dict(env, HOME=user.pw_dir)
    def drop():
        os.initgroups(ACCOUNT, user.pw_gid)
        os.setgid(user.pw_gid)
        os.setuid(user.pw_uid)
        os.umask(0o077)
    result = subprocess.run(['/usr/bin/dotnet', str(ROOT / 'RequestPortal.IntegrationAudit.dll'), *arguments],
                            cwd='/opt/aegis/requestportal', env=env, preexec_fn=drop,
                            capture_output=True, text=True, timeout=660)
    # Never forward provider/driver output or raw exception messages.
    if result.returncode:
        raise RuntimeError(arguments[0] + ' failed; no production migration was requested.')
    lines = [line for line in result.stdout.splitlines() if line.startswith('{')]
    report = json.loads(lines[-1])
    if report.get('readOnly') is not True:
        raise RuntimeError('The source read-only guard was not confirmed.')
    return report


def main():
    if len(sys.argv) != 1 or os.geteuid() != 0 or socket.gethostname().split('.')[0] != 'kratos':
        raise RuntimeError('Run the installed no-argument administrator command on Kratos.')
    os.umask(0o077)
    validate_bundle()
    env = service_environment()
    preflight = run_reader(['database-preflight'], env)
    major = preflight['source']['major']
    if type(major) is not int or not 14 <= major <= 18:
        raise RuntimeError('Unsupported PostgreSQL client major version.')
    client = Path('/usr/lib/postgresql', str(major), 'bin', 'pg_dump')
    if not client.is_file():
        result = subprocess.run(['/usr/bin/apt-get', 'install', '-y', '--no-install-recommends', 'postgresql-client-' + str(major)],
                                env={'PATH': '/usr/sbin:/usr/bin:/sbin:/bin', 'DEBIAN_FRONTEND': 'noninteractive'}, capture_output=True, timeout=300)
        if result.returncode or not client.is_file():
            raise RuntimeError('The matching PostgreSQL client could not be installed from existing trusted repositories.')
    user = pwd.getpwnam(ACCOUNT)
    backup_root = Path('/var/backups/aegis-requestportal-postgres')
    backup_root.mkdir(mode=0o710, exist_ok=True)
    protected(backup_root)
    os.chown(backup_root, 0, user.pw_gid)
    os.chmod(backup_root, 0o710)
    work = Path(tempfile.mkdtemp(prefix=datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ-'), dir=backup_root))
    os.chown(work, 0, user.pw_gid)
    os.chmod(work, 0o710)
    child = work / 'work'
    child.mkdir(mode=0o700)
    os.chown(child, user.pw_uid, user.pw_gid)
    try:
        backup = run_reader(['database-backup', str(child / 'requestportal.dump')], env)
        if backup['source'] != preflight['source'] or digest(child / 'requestportal.dump') != backup['archiveSha256']:
            raise RuntimeError('Backup target or archive checksum changed during preflight.')
        # Root retains the authoritative backup; only a private operator copy is exported for isolated restore.
        shutil.move(child / 'requestportal.dump', work / 'requestportal.dump')
        os.chown(work / 'requestportal.dump', 0, 0)
        os.chmod(work / 'requestportal.dump', 0o600)
        quality = run_reader(['input-validation'], env)
        report = {'status': 'backup-created-awaiting-isolated-restore', 'productionBusinessWrites': False,
                  'productionMigrationApplied': False, 'backup': backup, 'inputQuality': quality}
        (work / 'report.json').write_text(json.dumps(report, indent=2) + '\n')
        os.chmod(work / 'report.json', 0o600)
        checker = Path('/usr/local/sbin/aegis-requestportal-dashboard-check')
        protected(checker)
        if digest(checker) != CHECKER_SHA:
            raise RuntimeError('The reviewed read-only verification command does not match.')
        # The administrator explicitly authorizes this one hash-pinned, no-argument read-only command.
        policy = OPERATOR + ' ALL=(root) NOSETENV: NOPASSWD: sha256:' + CHECKER_SHA + ' ' + str(checker) + ' ""\n'
        policy_path = Path('/etc/sudoers.d/91-requestportal-dashboard-check')
        if policy_path.exists():
            protected(policy_path)
            if policy_path.read_text() != policy:
                raise RuntimeError('An existing verification grant differs; it was preserved.')
        else:
            fd, candidate = tempfile.mkstemp(prefix='.requestportal-check-',dir='/etc/sudoers.d')
            try:
                with os.fdopen(fd,'w') as stream: stream.write(policy)
                os.chmod(candidate,0o440)
                checked = subprocess.run(['/usr/sbin/visudo','-c','-f',candidate],capture_output=True)
                if checked.returncode: raise RuntimeError('The scoped read-only grant failed sudoers validation.')
                os.link(candidate,policy_path)
            finally: Path(candidate).unlink(missing_ok=True)
        operator = pwd.getpwnam(OPERATOR)
        export = Path(tempfile.mkdtemp(prefix='requestportal-backup-export-', dir='/var/tmp'))
        for name in ('requestportal.dump', 'report.json'):
            shutil.copyfile(work / name, export / name)
            os.chown(export / name, operator.pw_uid, operator.pw_gid)
            os.chmod(export / name, 0o600)
        os.chown(export, operator.pw_uid, operator.pw_gid)
        os.chmod(export, 0o700)
        print(json.dumps({'status': report['status'], 'sourceDatabase': backup['source']['database'],
                          'protectedBackup': str(work), 'privateExport': str(export),
                          'archiveSha256': backup['archiveSha256'], 'reportSha256': digest(work / 'report.json'),
                          'readOnlyDashboardCheckEnabled': True, 'nextStep': 'Verify an isolated restore before deployment.'}))
    finally:
        os.chown(child, 0, 0)
        os.chmod(child, 0o700)
        os.chown(work, 0, 0)
        os.chmod(work, 0o700)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(json.dumps({'status': 'blocked', 'errorType': type(error).__name__,
                          'detail': str(error) if isinstance(error, RuntimeError) else 'Preflight failed; no production migration was requested.'}))
        raise SystemExit(1)
