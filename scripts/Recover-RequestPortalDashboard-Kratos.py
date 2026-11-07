#!/usr/bin/python3 -I
"""Fixed Request Portal geography recovery and approved Portal-only deployment."""
import datetime
import hashlib
import fcntl
import importlib.util
import json
import os
from pathlib import Path
import pwd
import re
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time
from urllib.parse import urlsplit

sys.dont_write_bytecode = True
PLAN_SHA = '__PLAN_SHA__'
BACKUP = Path('/var/backups/aegis-requestportal-postgres/20261106T190029Z-80ekfatp/requestportal.dump')
BACKUP_SHA = '0a401f6256537225949318cd4812ea9bb0297e0017e46444607ed7eb56659f2e'
BACKUP_REPORT_SHA = 'd6a6c1aa3072559349a7bd006c7d074f81a422a929063b53cb9c26e2e0476c4b'
DEPLOYER_SHA = 'b88883fbde5a5dee6f98be31832ff346bdc8c0f8297ca74817de90d34a210716'
DIAGNOSTIC = Path('/var/tmp/requestportal-storage-result-88n09po3/report.json')
DIAGNOSTIC_SHA = '30f95b7d2dfb5e5220be5f7cf88c52713e651f793d6f17564b13bac2c639bc0c'


def require(value, message):
    if not value:
        raise RuntimeError(message)


def sha(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(chunk)
    return value.hexdigest()


def verify(root, files):
    require(not root.is_symlink() and not any(p.is_symlink() for p in root.rglob('*')), 'Bundle links are prohibited.')
    actual = {p.relative_to(root).as_posix() for p in root.rglob('*') if p.is_file()}
    require(actual == set(files), 'The validated bundle file list changed.')
    for name, expected in files.items():
        relative = Path(name)
        require(not relative.is_absolute() and '..' not in relative.parts and sha(root / relative) == expected,
                'Recovery artifact checksum mismatch.')


def validate_restore(evidence):
    require(evidence.get('status') == 'isolated-restore-verified' and evidence.get('synthetic') is False and
            evidence.get('archiveSha256') == BACKUP_SHA and evidence.get('reportSha256') == BACKUP_REPORT_SHA and
            evidence.get('verifiedTables') == 57 and evidence.get('migrationsVerified') is True,
            'The existing isolated restore evidence is not valid.')


def verify_backup(evidence):
    validate_restore(evidence)
    require(not BACKUP.is_symlink() and BACKUP.stat().st_uid == 0 and not BACKUP.stat().st_mode & 0o077 and
            sha(BACKUP) == BACKUP_SHA and sha(BACKUP.parent / 'report.json') == BACKUP_REPORT_SHA,
            'The protected backup or its verified report changed.')


def service_properties():
    result = subprocess.run(['/usr/bin/systemctl', 'show', 'aegis-requestportal', '-p', 'ActiveState', '-p', 'SubState',
                             '-p', 'MainPID', '-p', 'ControlPID', '-p', 'User', '-p', 'Group'],
                            capture_output=True, text=True, timeout=20)
    require(result.returncode == 0, 'Request Portal service state is unavailable.')
    return dict(line.split('=', 1) for line in result.stdout.splitlines() if '=' in line)


def running(properties):
    return properties.get('ActiveState') == 'active' and properties.get('SubState') == 'running' and int(properties.get('MainPID', '0')) > 1


def assert_same_release(binary, expected_sha, expected_pid):
    current = service_properties()
    require(running(current) and current.get('MainPID') == expected_pid and sha(binary) == expected_sha,
            'The production release or service PID changed during geography preparation; deployment was not attempted.')

def kestrel_health(base, url):
    # Match the installed Aegis deployer's Request Portal host-filtering semantics.
    raw = base.command(['/usr/bin/curl', '--fail', '--silent', '--show-error', '--max-time', '15',
                        '-H', 'Host: requestportal.ffpja.org', url])
    result = json.loads(raw)
    require(result.get('status') == 'Healthy' and result.get('application') == 'RequestPortal',
            'Kestrel did not report healthy Request Portal dependencies.')
    return result


def interrupt(signum, frame):
    raise RuntimeError('Recovery was interrupted; restoring the previous application state where required.')


def recover(source):
    require(len(sys.argv) == 1 and os.geteuid() == 0 and socket.gethostname().split('.')[0] == 'kratos',
            'Run this fixed no-argument recovery command interactively as root on Kratos.')
    os.umask(0o077)
    lease = os.open('/run/lock/aegis-requestportal-dashboard-recovery.lock', os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    require(os.fstat(lease).st_uid == 0 and not os.fstat(lease).st_mode & 0o077, 'The recovery lock is not protected.')
    try: fcntl.flock(lease, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError: raise RuntimeError('A Request Portal dashboard recovery is already running.') from None
    plan_path = source / 'plan.json'
    require(sha(plan_path) == PLAN_SHA, 'The validated recovery plan changed.')
    plan = json.loads(plan_path.read_text())
    require(re.fullmatch(r'[0-9a-f]{40}', plan['sourceCommit']) is not None, 'Invalid validated main commit.')
    verify(source / 'bundle', plan['files'])
    require(not DIAGNOSTIC.is_symlink() and sha(DIAGNOSTIC) == DIAGNOSTIC_SHA, 'The completed diagnostic evidence changed.')
    diagnostic = json.loads(DIAGNOSTIC.read_text())
    failure = diagnostic['storageDiagnostic']['probe']['failures']
    require(any(x.get('operation') == 'metadata' and x.get('storageCode') == 'STORAGE-SHAREPOINT-METADATA-001' and
                x.get('graphCode') == 'activityLimitReached' for x in failure), 'The confirmed failure evidence does not match this recovery.')
    verify_backup(plan['restoreEvidence'])
    deployer = Path('/usr/local/sbin/aegis-deploy')
    require(not deployer.is_symlink() and deployer.stat().st_uid == 0 and not deployer.stat().st_mode & 0o022 and sha(deployer) == DEPLOYER_SHA,
            'The installed approved deployer changed.')
    binary = Path('/opt/aegis/requestportal/RequestPortal.Web.dll')
    installed = sha(binary)
    require(installed in {plan['previousApplicationSha256'], plan['applicationSha256']}, 'The installed release changed; no recovery writes were attempted.')
    initial = service_properties()
    require(running(initial) and initial.get('User') == 'aegis' and initial.get('Group') in ['', 'aegis'], 'The expected production service identity/state changed.')
    operator, account = pwd.getpwnam('fwright'), pwd.getpwnam('aegis')
    work = Path(tempfile.mkdtemp(prefix='requestportal-location-recovery-', dir='/var/tmp'))
    bundle = work / 'bundle'
    shutil.copytree(source / 'bundle', bundle)
    verify(bundle, plan['files'])
    for path in [work, bundle, *bundle.rglob('*')]:
        os.chown(path, 0, account.pw_gid)
        os.chmod(path, 0o750 if path.is_dir() else 0o640)
    spec = importlib.util.spec_from_file_location('startup_helpers', bundle / 'startup_helpers.py')
    base = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(base)
    module = base.load_preflight()
    env = module.service_environment()
    process_env = dict(entry.decode().split('=', 1) for entry in Path('/proc', initial['MainPID'], 'environ').read_bytes().split(b'\0') if b'=' in entry)
    env.update({key: value for key, value in process_env.items() if key.startswith(('Storage__', 'ManagementDashboard__'))})
    env['HOME'] = account.pw_dir
    binding = process_env.get('ASPNETCORE_URLS', 'http://127.0.0.1:5401').split(';')[0]
    listener = urlsplit(binding)
    require(listener.scheme == 'http' and listener.hostname in ['127.0.0.1', 'localhost', '0.0.0.0', '::', '::1', '*', '+'] and listener.port is not None,
            'The expected Kestrel loopback binding could not be verified.')
    loopback = 'http://' + ('[::1]' if listener.hostname == '::1' else '127.0.0.1') + ':' + str(listener.port) + '/health'
    state = work / 'state'; state.mkdir(mode=0o700); os.chown(state, account.pw_uid, account.pw_gid)
    temporary = work / 'temp'; temporary.mkdir(mode=0o700); os.chown(temporary, account.pw_uid, account.pw_gid)
    env['TMPDIR'] = str(temporary)
    location_file = state / 'locations.json'
    report = {'status': 'recovery-in-progress', 'sourceCommit': plan['sourceCommit'], 'previousCommit': plan['previousCommit'],
              'rootCause': {'operation': 'SharePoint.GetMetadataAsync', 'storageCode': 'STORAGE-SHAREPOINT-METADATA-001',
                            'httpStatus': 429, 'graphCode': 'activityLimitReached'},
              'diagnosticSha256': DIAGNOSTIC_SHA, 'backupSha256': BACKUP_SHA, 'restoreVerified': True,
              'newDatabaseBackups': 0, 'providerChanged': False, 'ffpjmModified': False, 'bridgeDeployed': False,
              'privateRecoveryWork': str(work), 'deploymentAttempted': False}
    paused = stopped = deployed = False
    saved_release = None

    def drop():
        os.initgroups('aegis', account.pw_gid); os.setgid(account.pw_gid); os.setuid(account.pw_uid); os.umask(0o077)

    def audit(mode, argument=None):
        report['stage'] = mode
        print(json.dumps({'status': 'recovery-progress', 'stage': mode}), flush=True)
        args = ['/usr/bin/dotnet', str(bundle / 'audit/RequestPortal.IntegrationAudit.dll'), mode]
        if argument is not None:
            args.append(str(argument))
        result = None
        with (work / (mode + '.private.log')).open('ab') as log:
            with subprocess.Popen(args, cwd='/opt/aegis/requestportal', env=env, preexec_fn=drop,
                                  stdout=subprocess.PIPE, stderr=subprocess.STDOUT) as process:
                watchdog = threading.Timer(7260 if mode == 'location-export' else 150, process.kill)
                watchdog.daemon = True; watchdog.start()
                try:
                    for raw in process.stdout:
                        log.write(raw)
                        try: value = json.loads(raw)
                        except ValueError: continue
                        if not isinstance(value, dict): continue
                        if value.get('status') == 'location-export-progress':
                            print(json.dumps({k: value[k] for k in ['status', 'metadataRequests', 'initialRequestCount'] if k in value}), flush=True)
                        else:
                            result = value
                    code = process.wait(timeout=30)
                except BaseException:
                    process.kill(); process.wait(timeout=30); raise
                finally: watchdog.cancel()
        if code != 0:
            report['auditFailure'] = result if isinstance(result, dict) and result.get('status') == 'failed' else {'exitCode': code}
            raise RuntimeError('A focused recovery step failed; protected evidence identifies its stage and code.')
        expected = {'deployment-catalogue-verification': 'catalogue-verified', 'dashboard-verification': 'dashboard-verified',
                    'location-pause': 'dashboard-refresh-paused', 'location-resume': 'dashboard-refresh-schedule-reconciled',
                    'location-export': 'location-export-verified', 'location-schema': 'location-schema-verified', 'location-import': 'location-import-verified'}
        require(result is not None and result.get('status') == expected[mode], 'The recovery step did not confirm its expected result.')
        return result

    try:
        report['applicationHealthBefore'] = base.http('/health')
        report['stage'] = 'previous-kestrel-health'
        report['kestrelHealthBefore'] = kestrel_health(base, loopback)
        report['catalogueBefore'] = audit('deployment-catalogue-verification')
        if installed == plan['applicationSha256']:
            report['status'] = 'already-installed-no-redeployment'
            report['dashboardReadiness'] = base.http('/health/dashboard')
            require(report['dashboardReadiness'].get('ready'), 'The candidate is already installed but not ready; no repeat deployment was attempted.')
            report['dashboard'] = audit('dashboard-verification')
            report['installedCommit'] = plan['sourceCommit']
            return
        report['pause'] = audit('location-pause', location_file); paused = True
        report['geographyPreparation'] = audit('location-export', location_file)
        assert_same_release(binary, installed, initial['MainPID'])
        base.http('/health')
        stopped = True
        base.command(['/usr/bin/systemctl', 'stop', 'aegis-requestportal'])
        require(service_properties().get('ActiveState') == 'inactive', 'Request Portal writers did not stop.')
        report['finalGeography'] = audit('location-export', location_file)
        require(report['finalGeography'].get('writersStopped') is True, 'The final geography export did not confirm stopped writers.')
        report['geographyEvidenceSha256'] = sha(location_file)
        report['schema'] = audit('location-schema', location_file)
        report['geographyImport'] = audit('location-import', location_file)
        require(report['geographyImport'].get('sourceEvidenceSha256') == report['geographyEvidenceSha256'], 'Imported geography evidence hash differs.')
        report['stage'] = 'aegis-requestportal-deployment'; report['deploymentAttempted'] = True
        since = datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%d %H:%M:%S UTC')
        backup_root = Path('/opt/aegis/backups') / work.name
        with (work / 'deployment.private.log').open('wb') as log:
            process = subprocess.run(['/usr/local/sbin/aegis-deploy', '--source', str(bundle / 'payload'), '--apps', 'requestportal',
                                      '--backup-root', str(backup_root)], stdout=log, stderr=log, timeout=300)
        require(process.returncode == 0, 'The approved deployer failed and attempted its normal application rollback.')
        deployed = True; stopped = False
        releases = list(backup_root.iterdir()); require(len(releases) == 1, 'The application rollback artifact is ambiguous.')
        saved_release = releases[0]
        require(sha(saved_release / 'requestportal/RequestPortal.Web.dll') == installed, 'Application rollback artifact hash differs from the previous release.')
        report['applicationBackup'] = str(saved_release)
        require(sha(binary) == plan['applicationSha256'], 'The installed application differs from validated main.')
        current = service_properties()
        require(running(current) and current['MainPID'] != initial['MainPID'], 'The new Request Portal process is not active/running.')
        report['applicationHealth'] = base.http('/health')
        report['stage'] = 'deployed-kestrel-health'
        kestrel_health(base, loopback)
        report['kestrelHealth'] = 'Healthy'
        versions = []
        for attempt in range(100):
            try:
                ready = base.http('/health/dashboard')
                if ready.get('ready') and ready.get('snapshotVersion', 0) > 0:
                    report['dashboardReadiness'] = ready
                    if not versions or ready['snapshotVersion'] != versions[-1]: versions.append(ready['snapshotVersion'])
                    if len(versions) >= 3: break
            except RuntimeError: pass
            time.sleep(2)
        require(len(versions) >= 3, 'Repeated ready aggregate publications were not observed.')
        report['observedSnapshotVersions'] = versions
        for attempt in range(10):
            try:
                report['dashboard'] = audit('dashboard-verification'); break
            except RuntimeError:
                if attempt == 9: raise
                time.sleep(2)
        journal = base.command(['/usr/bin/journalctl', '-u', 'aegis-requestportal', '--since', since, '-n', '3000', '--no-pager', '-o', 'json'])
        messages = []
        for line in journal.splitlines():
            item = json.loads(line)
            if isinstance(item.get('MESSAGE'), str): messages.append(item['MESSAGE'])
        worker_errors = sum('Management Dashboard refresh failed (' in message for message in messages)
        startup_errors = sum(any(term in message for term in ['RP-STARTUP-DB-001', 'Unhandled exception.', 'Hosting failed to start']) for message in messages)
        report['workerErrorsAfterDeployment'] = worker_errors; report['startupErrorsAfterDeployment'] = startup_errors
        require(worker_errors == 0 and startup_errors == 0, 'Startup or worker errors remain after deployment.')
        require(any('Now listening on:' in message for message in messages), 'Kestrel startup was not observed.')
        report['applicationHealth'] = base.http('/health'); report['dashboardReadiness'] = base.http('/health/dashboard')
        require(report['dashboardReadiness'].get('ready') is True, 'Final dashboard readiness did not pass.')
        report['installedCommit'] = plan['sourceCommit']; report['status'] = 'deployed-and-dashboard-verified'
    except Exception as error:
        report['status'] = 'blocked'; report['errorType'] = type(error).__name__
        report['failedStage'] = report.get('stage')
        report['detail'] = str(error) if isinstance(error, RuntimeError) else 'Recovery failed; protected evidence is retained.'
        if deployed and saved_release is not None:
            try: base.http('/health')
            except Exception:
                if sha(binary) == plan['applicationSha256']:
                    with (work / 'rollback.private.log').open('wb') as log:
                        rollback = subprocess.run(['/usr/local/sbin/aegis-deploy', '--source', str(saved_release), '--apps', 'requestportal'],
                                                  stdout=log, stderr=log, timeout=300)
                    report['applicationRollbackSucceeded'] = rollback.returncode == 0 and sha(binary) == installed
    finally:
        if report['status'] != 'deployed-and-dashboard-verified' and report.get('status') != 'already-installed-no-redeployment':
            report['status'] = 'blocked'
        if paused:
            try: report['pauseCleanup'] = audit('location-resume', location_file)
            except Exception: report['pauseCleanupFailed'] = True
        if stopped and sha(binary) == installed:
            try:
                base.command(['/usr/bin/systemctl', 'start', 'aegis-requestportal'])
                report['previousApplicationRestored'] = base.http('/health').get('status') == 'Healthy'
            except Exception: report['previousApplicationRestored'] = False
        report['installedApplicationSha256'] = sha(binary)
        if report['installedApplicationSha256'] == plan['applicationSha256']: report['installedCommit'] = plan['sourceCommit']
        elif report['installedApplicationSha256'] == plan['previousApplicationSha256']: report['installedCommit'] = plan['previousCommit']
        os.chmod(work, 0o700)
        export = Path(tempfile.mkdtemp(prefix='requestportal-dashboard-result-', dir='/var/tmp'))
        for name in ['locations.json', 'locations.json.failure.json']:
            if (state / name).is_file(): shutil.copyfile(state / name, export / name)
        (export / 'report.json').write_text(json.dumps(report, indent=2) + '\n')
        for path in export.iterdir(): os.chown(path, operator.pw_uid, operator.pw_gid); os.chmod(path, 0o600)
        os.chown(export, operator.pw_uid, operator.pw_gid); os.chmod(export, 0o700)
        print(json.dumps({'status': report['status'], 'privateExport': str(export), 'reportSha256': sha(export / 'report.json'),
                          'installedCommit': report.get('installedCommit'), 'newDatabaseBackups': 0}), flush=True)
        os.close(lease)


if __name__ == '__main__':
    signal.signal(signal.SIGTERM, interrupt); signal.signal(signal.SIGHUP, interrupt); signal.signal(signal.SIGINT, interrupt)
    try: recover(Path(__file__).resolve().parent)
    except Exception as error:
        print(json.dumps({'status': 'blocked', 'errorType': type(error).__name__,
                          'detail': str(error) if isinstance(error, RuntimeError) else 'Recovery failed; private details suppressed.'}))
        raise SystemExit(1)
