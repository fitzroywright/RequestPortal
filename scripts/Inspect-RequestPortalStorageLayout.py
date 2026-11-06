#!/usr/bin/python3 -I
"""Fixed read-only mount/layout/permission inspection; no storage changes."""
import hashlib,json,os,pwd,socket,subprocess,sys,tempfile
from pathlib import Path
sys.dont_write_bytecode=True

def main():
 if len(sys.argv)!=1 or os.geteuid()!=0 or socket.gethostname().split('.')[0]!='kratos':raise RuntimeError('Run the no-argument inspector as root on Kratos.')
 os.umask(0o077);mount=Path('/mnt/ffpmanager-data')
 r=subprocess.run(['/usr/bin/findmnt','-J','-T',str(mount),'-o','TARGET,SOURCE,FSTYPE,OPTIONS'],capture_output=True,text=True,check=True)
 fs=json.loads(r.stdout)['filesystems'][0]
 if fs['target']!=str(mount) or fs['source'].casefold()!='//nas01.fftpj.local/ffpmanager_data' or fs['fstype']!='cifs':raise RuntimeError('The existing approved CIFS mount is not present.')
 allowed={'uid','gid','file_mode','dir_mode','multiuser','noperm','nounix','cifsacl','acl','vers','ro','rw','sec','mfsymlinks','nosuid','nodev','noexec','forceuid','forcegid','serverino','cache','actimeo'}
 fs['options']=[v for v in fs.get('options','').split(',') if v.split('=',1)[0] in allowed]
 def stat(p):
  s=p.stat();return {'mode':oct(s.st_mode&0o7777),'uid':s.st_uid,'gid':s.st_gid,'isSymlink':p.is_symlink()}
 out={'status':'read-only-layout-evidence-collected','mount':fs,'mountPermissions':stat(mount),'productionBusinessWrites':False,'storageChanges':False,'deploymentAttempted':False}
 out['supportingDirectories']=[]
 for child in mount.iterdir():
  if child.is_dir() and 'support' in child.name.casefold() and not child.is_symlink():
   row={'path':str(child),'permissions':stat(child),'directoryCount':sum(x.is_dir() for x in child.iterdir()),'fileCount':sum(x.is_file() for x in child.iterdir()),'requestPortalExists':(child/'RequestPortal').exists()}
   if (child/'RequestPortal').exists():row['requestPortalPermissions']=stat(child/'RequestPortal')
   acl=subprocess.run(['/usr/bin/getfacl','-cp',str(child)],capture_output=True,text=True) if Path('/usr/bin/getfacl').exists() else None
   row['acl']=acl.stdout.splitlines() if acl is not None and acl.returncode==0 else None
   out['supportingDirectories'].append(row)
 out['services']={}
 for service in ['aegis-requestportal','aegis-ffpmanager-bridge']:
  props=['User','Group','SupplementaryGroups','MainPID','ActiveState','SubState','NoNewPrivileges','PrivateTmp','ProtectSystem','BindPaths','InaccessiblePaths','ReadWritePaths','CapabilityBoundingSet','AmbientCapabilities']
  r=subprocess.run(['/usr/bin/systemctl','show',service,*sum((['-p',p] for p in props),[])],capture_output=True,text=True,check=True)
  values=dict(l.split('=',1) for l in r.stdout.splitlines() if '=' in l);out['services'][service]=values
  pid=values.get('MainPID','0')
  if pid.isdecimal() and int(pid)>1:
   entries=dict(x.decode().split('=',1) for x in Path('/proc',pid,'environ').read_bytes().split(b'\0') if b'=' in x)
   keys={'Bridge__SupportingDocumentRoot','Bridge__SupportingDocumentManagerRoot','Storage__Provider','Storage__NetworkFolder__RootPath'}
   out['services'][service]['approvedStorageSettings']={k:v for k,v in entries.items() if k in keys}
 out['identity']={name:{'uid':pwd.getpwnam(name).pw_uid,'gid':pwd.getpwnam(name).pw_gid} for name in ['aegis','aegis-ffpmanager-bridge']}
 work=Path(tempfile.mkdtemp(prefix='requestportal-storage-layout-',dir='/var/tmp'));report=work/'report.json';report.write_text(json.dumps(out,indent=2)+'\n')
 operator=pwd.getpwnam('fwright');os.chown(report,operator.pw_uid,operator.pw_gid);os.chmod(report,0o600);os.chown(work,operator.pw_uid,operator.pw_gid);os.chmod(work,0o700)
 print(json.dumps({'status':out['status'],'privateExport':str(work),'reportSha256':hashlib.sha256(report.read_bytes()).hexdigest(),'productionBusinessWrites':False,'storageChanges':False}))
if __name__=='__main__':
 try:main()
 except Exception as error:
  print(json.dumps({'status':'blocked','errorType':type(error).__name__,'detail':str(error) if isinstance(error,RuntimeError) else 'Read-only layout inspection failed; private details suppressed.'}));raise SystemExit(1)
