#!/usr/bin/python3 -I
"""Reviewed one-shot recovery: fixed read-only gates, existing Portal-only deployer, safe verification.
Render the plan pin after publishing validated main. Requires interactive administrator authentication.
No sudo grant installation, database backup creation, database rollback or Bridge operation.
"""
import datetime
import hashlib
import importlib.util
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
import time

PLAN_SHA = '__PLAN_SHA__'
PREFLIGHT_SHA = 'dd91aa2c3e0956efe3138cad7fc8d9e0cca77dbd67c745f414eefab57db469d2'
sys.dont_write_bytecode = True
DEPLOYER_SHA = 'b88883fbde5a5dee6f98be31832ff346bdc8c0f8297ca74817de90d34a210716'
BACKUP = Path('/var/backups/aegis-requestportal-postgres/20261106T190029Z-80ekfatp/requestportal.dump')
BACKUP_SHA = '0a401f6256537225949318cd4812ea9bb0297e0017e46444607ed7eb56659f2e'

def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024*1024), b''): value.update(block)
    return value.hexdigest()

def require(condition, message):
    if not condition: raise RuntimeError(message)

def verify_files(root, files):
    actual = {p.relative_to(root).as_posix() for p in root.rglob('*') if p.is_file()}
    require(actual == set(files), 'Recovery bundle files differ from the validated manifest.')
    for name, expected in files.items():
        part = Path(name)
        require(not part.is_absolute() and '..' not in part.parts, 'Unsafe bundle path.')
        path = root / part
        require(not path.is_symlink() and digest(path) == expected, 'Recovery artifact checksum mismatch.')
    require(not any(p.is_symlink() for p in root.rglob('*')), 'Recovery bundle contains a symbolic link.')

def command(args, **kwargs):
    result = subprocess.run(args, capture_output=True, timeout=120, **kwargs)
    require(result.returncode == 0, 'A required recovery command failed; inspect the protected evidence.')
    return result.stdout

def http(path):
    body = command(['/usr/bin/curl','--fail','--silent','--show-error','--max-time','15',
                    'https://requestportal.ffpja.org' + path])
    return json.loads(body)

def load_preflight():
    from importlib.machinery import SourceFileLoader
    helper = Path('/usr/local/sbin/aegis-requestportal-postgres-preflight')
    require(helper.stat().st_uid == 0 and not helper.stat().st_mode & 0o022 and digest(helper) == PREFLIGHT_SHA,
            'The approved root-owned preflight helper changed.')
    spec = importlib.util.spec_from_loader('reviewed_preflight', SourceFileLoader('reviewed_preflight',str(helper)))
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    module.validate_bundle()
    return module

def run_audit(bundle, mode, environment):
    user = pwd.getpwnam('aegis')
    env = dict(environment); env['HOME'] = user.pw_dir
    def drop():
        os.initgroups('aegis',user.pw_gid); os.setgid(user.pw_gid); os.setuid(user.pw_uid); os.umask(0o077)
    result = subprocess.run(['/usr/bin/dotnet',str(bundle/'audit/RequestPortal.IntegrationAudit.dll'),mode],
                            cwd='/opt/aegis/requestportal',env=env,preexec_fn=drop,capture_output=True,timeout=110)
    require(result.returncode == 0, 'The read-only production verification did not pass; provider output is suppressed.')
    rows = [line for line in result.stdout.decode().splitlines() if line.startswith('{')]
    report = json.loads(rows[-1])
    expected={'deployment-catalogue-verification':'catalogue-verified','dashboard-verification':'dashboard-verified'}
    require(report.get('readOnly') is True and report.get('status')==expected[mode],
            'The verifier did not confirm its read-only transaction and expected result.')
    return report

def recover(source):
    require(len(sys.argv)==1 and os.geteuid()==0 and socket.gethostname().split('.')[0]=='kratos',
            'Run the staged no-argument recovery command interactively as root on Kratos.')
    os.umask(0o077)
    plan_path=source/'plan.json'
    require(digest(plan_path)==PLAN_SHA, 'The validated recovery plan changed.')
    plan=json.loads(plan_path.read_text())
    require(re.fullmatch(r'[0-9a-f]{40}',plan['sourceCommit']) is not None, 'Invalid source commit.')
    verify_files(source/'bundle',plan['files'])
    deployer=Path('/usr/local/sbin/aegis-deploy')
    require(not deployer.is_symlink() and deployer.stat().st_uid==0 and not deployer.stat().st_mode & 0o022 and
            digest(deployer)==DEPLOYER_SHA, 'The installed approved deployer changed.')
    require(not BACKUP.is_symlink() and BACKUP.stat().st_uid==0 and not BACKUP.stat().st_mode & 0o077 and digest(BACKUP)==BACKUP_SHA,
            'The existing protected backup does not match the verified archive.')
    restore=plan['restoreEvidence']
    require(restore['status']=='isolated-restore-verified' and restore['archiveSha256']==BACKUP_SHA and
            restore['migrationsVerified'] and restore['verifiedTables']==57 and not restore['synthetic'],
            'Valid isolated restore evidence is required.')
    require(digest(BACKUP.parent/'report.json')==restore['reportSha256'], 'The protected backup report does not match restore evidence.')
    current=Path('/opt/aegis/requestportal/RequestPortal.Web.dll')
    installed=digest(current)
    require(installed in {plan['previousApplicationSha256'],plan['applicationSha256']},
            'The installed release changed since recovery preparation; no deployment was attempted.')
    command(['/usr/bin/systemctl','is-active','--quiet','aegis-requestportal'])
    http('/health')
    helper=load_preflight()
    preflight=helper.run_reader(['database-preflight'],helper.service_environment())
    require(preflight['status']=='preflight-passed' and '20261106170310_ManagementDashboardAggregate' in preflight['migrations'],
            'The target or existing dashboard migration was not verified.')
    work=Path(tempfile.mkdtemp(prefix='requestportal-startup-recovery-',dir='/var/tmp'))
    operator=pwd.getpwnam('fwright'); account=pwd.getpwnam('aegis')
    # Root owns the copied code; aegis may read it but cannot modify it.
    bundle=work/'bundle'; shutil.copytree(source/'bundle',bundle)
    verify_files(bundle,plan['files'])
    for path in [work,bundle,*bundle.rglob('*')]:
        os.chown(path,0,account.pw_gid);os.chmod(path,0o750 if path.is_dir() else 0o640)
    result={'status':'verification-in-progress','sourceCommit':plan['sourceCommit'],'backupSha256':BACKUP_SHA,
            'restoreVerified':True,'newDatabaseBackups':0,'ffpjmModified':False,'bridgeDeployed':False}
    did_deploy=False
    saved_release=None
    try:
        result['catalogue']=run_audit(bundle,'deployment-catalogue-verification',helper.service_environment())
        since=datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%d %H:%M:%S UTC')
        if installed!=plan['applicationSha256']:
            require(digest(current)==installed, 'The production release changed during preflight; deployment was not attempted.')
            backup_root=Path('/opt/aegis/backups')/work.name
            log=work/'deployment.log'
            with log.open('wb') as stream:
                deployment=subprocess.run(['/usr/local/sbin/aegis-deploy','--source',str(bundle/'payload'),'--apps','requestportal','--backup-root',str(backup_root)],
                                          stdout=stream,stderr=stream,timeout=300)
            require(deployment.returncode==0, 'The established deployer failed and attempted its normal application rollback; deployment log remains private.')
            did_deploy=True
            backups=list(backup_root.iterdir())
            require(len(backups)==1, 'The ordinary application backup could not be identified unambiguously.')
            saved_release=backups[0]
            require(digest(saved_release/'requestportal/RequestPortal.Web.dll')==installed, 'The application rollback artifact does not match the previous release.')
            result['applicationBackup']=str(saved_release)
        require(digest(current)==plan['applicationSha256'], 'Installed application hash does not match validated main.')
        command(['/usr/bin/systemctl','is-active','--quiet','aegis-requestportal'])
        result['systemdSubState']=command(['/usr/bin/systemctl','show','aegis-requestportal','-p','SubState','--value']).decode().strip()
        require(result['systemdSubState']=='running', 'The application systemd service is not running.')
        result['applicationHealth']=http('/health')
        # Wait for initialization independently of ordinary application health.
        first=None
        for attempt in range(45):
            try:
                first=http('/health/dashboard')
                if first.get('ready'): break
            except RuntimeError: pass
            time.sleep(2)
        require(first is not None and first.get('ready'), 'Application is running, but dashboard readiness has not passed.')
        result['dashboardReadiness']=first
        for attempt in range(10):
            try:
                result['dashboard']=run_audit(bundle,'dashboard-verification',helper.service_environment());break
            except RuntimeError:
                if attempt==9: raise
                time.sleep(2)
        # Observe existing activity or scheduled reconciliation; never manufacture business writes.
        last=first
        for attempt in range(45):
            time.sleep(2);last=http('/health/dashboard')
            if last['snapshotVersion']>first['snapshotVersion']: break
        require(last['ready'] and last['snapshotVersion']>first['snapshotVersion'], 'A successful subsequent aggregate publication was not observed.')
        result['subsequentDashboardReadiness']=last
        raw=command(['/usr/bin/journalctl','-u','aegis-requestportal','--since',since,'--no-pager','-o','json'])
        messages=[]
        for line in raw.decode().splitlines():
            event=json.loads(line)
            if isinstance(event.get('MESSAGE'),str):messages.append(event['MESSAGE'])
        failures=sum('Management Dashboard refresh failed (' in m for m in messages)
        fatal=sum(any(term in m for term in ['RP-STARTUP-DB-001','Unhandled exception.','Hosting failed to start']) for m in messages)
        result['dashboardWorkerFailuresSinceDeployment']=failures
        result['fatalStartupFailuresSinceDeployment']=fatal
        result['dashboardMigrationReapplied']=any("Applying migration '20261106170310_ManagementDashboardAggregate'" in m for m in messages)
        result['kestrelStartupObserved']=any('Now listening on:' in m for m in messages) if installed!=plan['applicationSha256'] else None
        result['otherIntegrationWarnings']=sum('Unable to refresh FFP Manager status' in m for m in messages)
        require(failures==0 and fatal==0 and not result['dashboardMigrationReapplied'], 'Startup or dashboard worker verification did not pass.')
        require(result['kestrelStartupObserved'] is not False, 'Kestrel startup was not observed.')
        result['status']='deployed-and-operational-checks-verified'
        result['authenticatedBrowserCheck']='not-performed-by-this-command'
        return result
    except Exception:
        result['status']='blocked'
        # The deployer handles its own initial health failure. If health subsequently
        # fails, promote its verified previous application backup through that same
        # deployer. Never roll back business data or overwrite a different release.
        if did_deploy and saved_release is not None:
            try: http('/health')
            except Exception:
                if digest(current)==plan['applicationSha256'] and digest(saved_release/'requestportal/RequestPortal.Web.dll')==installed:
                    with (work/'rollback.log').open('wb') as stream:
                        rollback=subprocess.run(['/usr/local/sbin/aegis-deploy','--source',str(saved_release),'--apps','requestportal'],
                                                stdout=stream,stderr=stream,timeout=300)
                    result['applicationRollbackSucceeded']=rollback.returncode==0 and digest(current)==installed
        raise
    finally:
        evidence=work/'result.json';evidence.write_text(json.dumps(result,indent=2)+'\n')
        # Raw deployment logs and code remain root-only; export only safe structured evidence.
        os.chmod(work,0o700)
        export=Path(tempfile.mkdtemp(prefix='requestportal-startup-result-',dir='/var/tmp'))
        safe=export/'report.json';safe.write_text(json.dumps(result,indent=2)+'\n')
        os.chown(safe,operator.pw_uid,operator.pw_gid);os.chmod(safe,0o600)
        os.chown(export,operator.pw_uid,operator.pw_gid);os.chmod(export,0o700)
        print(json.dumps({'privateExport':str(export),'reportSha256':digest(safe),'status':result['status']}),flush=True)

if __name__=='__main__':
    try: recover(Path(__file__).resolve().parent)
    except Exception as error:
        print(json.dumps({'status':'blocked','errorType':type(error).__name__,
                          'detail':str(error) if isinstance(error,RuntimeError) else 'Recovery failed; private evidence is retained.'}))
        raise SystemExit(1)
