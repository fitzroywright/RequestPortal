#!/usr/bin/python3 -I
"""Dashboard-only diagnosis and location JSON export. No deployment, SQL writes, NAS scan or backup."""
import hashlib
import datetime
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
sys.dont_write_bytecode=True
MANIFEST_SHA='__MANIFEST_SHA__'
PREFLIGHT_SHA='dd91aa2c3e0956efe3138cad7fc8d9e0cca77dbd67c745f414eefab57db469d2'
EXPECTED_APP_SHA='166edd8cb00c6e8e8cfca61b0f3edefb735202da0f618f18e21859cbc4417963'

def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def require(condition,message):
    if not condition: raise RuntimeError(message)
def verify(root,files):
    require(not any(p.is_symlink() for p in root.rglob('*')),'Diagnostic bundle contains a link.')
    actual={p.relative_to(root).as_posix() for p in root.rglob('*') if p.is_file()}
    require(actual==set(files),'Diagnostic bundle files changed.')
    for name,digest in files.items():
        relative=Path(name)
        require(not relative.is_absolute() and '..' not in relative.parts and sha(root/relative)==digest,'Diagnostic checksum mismatch.')
def request(path):
    result=subprocess.run(['/usr/bin/curl','--silent','--show-error','--max-time','15','--write-out','\n%{http_code}',
                           'https://requestportal.ffpja.org'+path],capture_output=True,timeout=20)
    require(result.returncode==0,'HTTPS diagnostic transport failed; private details suppressed.')
    body,code=result.stdout.rsplit(b'\n',1)
    return {'httpStatus':int(code),'body':json.loads(body)}
def main():
    require(len(sys.argv)==1 and os.geteuid()==0 and socket.gethostname().split('.')[0]=='kratos',
            'Run the staged no-argument diagnostic interactively as root on Kratos.')
    os.umask(0o077);source=Path(__file__).resolve().parent
    require(sha(source/'manifest.json')==MANIFEST_SHA,'The reviewed diagnostic manifest changed.')
    manifest=json.loads((source/'manifest.json').read_text());verify(source/'bundle',manifest['files'])
    helper=Path('/usr/local/sbin/aegis-requestportal-postgres-preflight')
    require(helper.stat().st_uid==0 and not helper.stat().st_mode&0o022 and sha(helper)==PREFLIGHT_SHA,'The approved environment reader changed.')
    from importlib.machinery import SourceFileLoader
    spec=importlib.util.spec_from_loader('reviewed_preflight',SourceFileLoader('reviewed_preflight',str(helper)))
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);module.validate_bundle()
    pid=subprocess.check_output(['/usr/bin/systemctl','show','aegis-requestportal','-p','MainPID','--value'],text=True).strip()
    require(pid.isdecimal() and int(pid)>1,'The service is not running.')
    env=module.service_environment()
    values=dict(item.decode().split('=',1) for item in Path('/proc',pid,'environ').read_bytes().split(b'\0') if b'=' in item)
    # Only the actual service storage/dashboard configuration is added. Nothing is exported or printed.
    env.update({key:value for key,value in values.items() if key.startswith(('Storage__','ManagementDashboard__'))})
    operator=pwd.getpwnam('fwright');account=pwd.getpwnam('aegis')
    work=Path(tempfile.mkdtemp(prefix='requestportal-location-recovery-',dir='/var/tmp'))
    bundle=work/'bundle';shutil.copytree(source/'bundle',bundle);verify(bundle,manifest['files'])
    for path in [work,bundle,*bundle.rglob('*')]:
        os.chown(path,0,account.pw_gid);os.chmod(path,0o750 if path.is_dir() else 0o640)
    temporary=work/'temp';temporary.mkdir(mode=0o700);os.chown(temporary,account.pw_uid,account.pw_gid)
    geography=work/'geography';geography.mkdir(mode=0o700);os.chown(geography,account.pw_uid,account.pw_gid)
    env.update(HOME=account.pw_dir,TMPDIR=str(temporary))
    report={'status':'collecting','deploymentAttempted':False,'migrationRequested':False,'backupCreated':False,
            'productionBusinessWrites':False,'probeStorageWrites':False}
    try:
        binary=Path('/opt/aegis/requestportal/RequestPortal.Web.dll');data=binary.read_bytes()
        report['installed']={'sha256':sha(binary),'expectedReleaseVerified':sha(binary)==EXPECTED_APP_SHA,
                             'informationalVersions':sorted(set(x.decode() for x in re.findall(rb'\d+\.\d+\.\d+\+[0-9a-f]{40}',data)))}
        state=subprocess.run(['/usr/bin/systemctl','show','aegis-requestportal','-p','ActiveState','-p','SubState'],capture_output=True,text=True,timeout=15)
        report['systemd']=dict(line.split('=',1) for line in state.stdout.splitlines() if '=' in line)
        report['health']=request('/health');report['readinessBefore']=request('/health/dashboard')
        def drop():
            os.initgroups('aegis',account.pw_gid);os.setgid(account.pw_gid);os.setuid(account.pw_uid);os.umask(0o077)
        probe=subprocess.run(['/usr/bin/dotnet',str(bundle/'RequestPortal.IntegrationAudit.dll'),'dashboard-storage-diagnostics'],
                             cwd='/opt/aegis/requestportal',env=env,preexec_fn=drop,capture_output=True,timeout=660)
        if probe.returncode:
            failure={'exitCode':probe.returncode}
            for line in reversed(probe.stderr.decode().splitlines()):
                try: error=json.loads(line)
                except ValueError: continue
                if not isinstance(error,dict) or error.get('status')!='failed': continue
                for key in ['stage','code','errorType']:
                    value=error.get(key)
                    if isinstance(value,str) and re.fullmatch(r'[A-Za-z][A-Za-z0-9_.+-]{0,100}',value):failure[key]=value
                break
            report['probeFailure']=failure
        else:
            lines=[line for line in probe.stdout.decode().splitlines() if line.startswith('{')]
            evidence=json.loads(lines[-1])
            require(evidence.get('readOnly') is True and evidence.get('status')=='storage-diagnostic-collected','The probe did not confirm read-only collection.')
            report['storageDiagnostic']=evidence
        print(json.dumps({'status':'dashboard-probe-collected-reading-location-history','productionBusinessWrites':False,
                          'supportingDocumentsScanned':0}),flush=True)
        retry_after=report.get('storageDiagnostic',{}).get('retryAfterSeconds')
        if isinstance(retry_after,(int,float)) and retry_after>0:
            throttle=geography/'throttle.json'
            throttle.write_text(json.dumps((datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(seconds=retry_after)).isoformat()))
            os.chown(throttle,account.pw_uid,account.pw_gid);os.chmod(throttle,0o600)
        export_run=subprocess.run(['/usr/bin/dotnet',str(bundle/'RequestPortal.IntegrationAudit.dll'),
                                   'location-export',str(geography/'locations.json')],cwd='/opt/aegis/requestportal',
                                  env=env,preexec_fn=drop,capture_output=True,timeout=7260)
        output=export_run.stdout if export_run.returncode==0 else export_run.stderr
        summary=None
        for line in reversed(output.decode(errors='replace').splitlines()):
            try: value=json.loads(line)
            except ValueError: continue
            if isinstance(value,dict) and value.get('status') in ['location-export-verified','failed']:
                summary=value;break
        report['geographyPreparation']=summary if summary is not None else {'status':'blocked','errorType':'ExporterProcessFailure'}
        report['privateRecoveryWork']=str(work)
        report['readinessAfter']=request('/health/dashboard')
        journal=subprocess.run(['/usr/bin/journalctl','-u','aegis-requestportal','--since','2026-11-06 20:58:30 UTC','-n','10000','--no-pager','-o','json'],capture_output=True,timeout=30)
        messages=[]
        for line in journal.stdout.decode().splitlines():
            try: event=json.loads(line)
            except ValueError: continue
            if isinstance(event.get('MESSAGE'),str):messages.append(event['MESSAGE'])
        report['journalSummary']={'available':journal.returncode==0,
            'dashboardRefreshWarnings':sum('Management Dashboard refresh failed (' in m for m in messages),
            'dashboardPublications':sum('Management Dashboard snapshot published successfully.' in m for m in messages),
            'postgresExceptionMentions':sum('PostgresException' in m or 'NpgsqlException' in m for m in messages),
            'fatalStartupMentions':sum('Hosting failed to start' in m or 'Unhandled exception.' in m or 'RP-STARTUP-DB-001' in m for m in messages),
            'storageCodes':sorted(set(code for m in messages for code in re.findall(r'\bSTORAGE-[A-Z0-9-]{1,90}\b',m)))}
        report['status']='read-only-evidence-collected'
    except Exception:
        report['status']='blocked';raise
    finally:
        # Remove only this command's private temporary downloads. Keep the sanitized report.
        shutil.rmtree(temporary)
        os.chmod(work,0o700)
        export=Path(tempfile.mkdtemp(prefix='requestportal-storage-result-',dir='/var/tmp'))
        report['geographyExportSha256']=sha(geography/'locations.json') if (geography/'locations.json').is_file() else None
        report['geographyFailureSha256']=sha(geography/'locations.json.failure.json') if (geography/'locations.json.failure.json').is_file() else None
        path=export/'report.json';path.write_text(json.dumps(report,indent=2)+'\n')
        for name in ['locations.json','locations.json.failure.json']:
            source_file=geography/name
            if source_file.is_file():
                copied=export/name;shutil.copyfile(source_file,copied)
                os.chown(copied,operator.pw_uid,operator.pw_gid);os.chmod(copied,0o600)
        os.chown(path,operator.pw_uid,operator.pw_gid);os.chmod(path,0o600)
        os.chown(export,operator.pw_uid,operator.pw_gid);os.chmod(export,0o700)
        print(json.dumps({'status':report['status'],'privateExport':str(export),'reportSha256':sha(path),
                          'deploymentAttempted':False,'productionBusinessWrites':False}),flush=True)
if __name__=='__main__':
    try:main()
    except Exception as error:
        print(json.dumps({'status':'blocked','errorType':type(error).__name__,
                          'detail':str(error) if isinstance(error,RuntimeError) else 'Private diagnostic failed; no deployment was attempted.'}))
        raise SystemExit(1)
