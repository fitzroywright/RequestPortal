#!/usr/bin/env python3
"""Verify a protected backup using a disposable, socket-only PostgreSQL instance.
Never connects to Axios, starts RequestPortal, or processes notifications/outbox work.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time

PG = Path('/Library/PostgreSQL/18/bin')


def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(block)
    return value.hexdigest()


def private_file(path):
    info = path.lstat()
    if path.is_symlink() or info.st_uid != os.getuid() or info.st_mode & 0o077:
        raise RuntimeError('Backup verification inputs must be owned by the operator and private.')


def quote(value):
    return '"' + value.replace('"', '""') + '"'


def verify(export, report_sha):
    private_file(export)
    archive, report_file = export / 'requestportal.dump', export / 'report.json'
    private_file(archive)
    private_file(report_file)
    if digest(report_file) != report_sha:
        raise RuntimeError('The transferred report does not match the administrator evidence.')
    report = json.loads(report_file.read_text())
    backup = report['backup']
    if backup['source']['database'] != 'requestportal' or backup['source']['address'] != '10.10.0.226':
        raise RuntimeError('The backup target is not the approved production database.')
    if digest(archive) != backup['archiveSha256']:
        raise RuntimeError('The transferred archive checksum does not match.')
    if not (PG / 'postgres').is_file():
        raise RuntimeError('The existing local PostgreSQL 18 installation is required.')
    if shutil.disk_usage(export).free < max(1024**3, archive.stat().st_size * 15):
        raise RuntimeError('Insufficient space for a safe isolated restore.')
    work = Path(tempfile.mkdtemp(prefix='rp-verify-', dir='/private/tmp'))
    os.chmod(work, 0o700)
    cluster, socket_dir = work / 'cluster', work / 'socket'
    socket_dir.mkdir(mode=0o700)
    log_path = work / 'verification.log'
    server = None
    success = False
    env = {key: value for key, value in os.environ.items() if not key.startswith('PG')}
    env.update(PGHOST=str(socket_dir), PGPORT='6547', PGDATABASE='requestportal_restore', PGUSER=os.environ.get('USER', ''))
    try:
        with log_path.open('wb') as log:
            def command(args, data=None):
                result = subprocess.run([str(arg) for arg in args], input=data, capture_output=True, env=env, timeout=600)
                if result.returncode:
                    log.write(result.stderr)
                    log.flush()
                    raise RuntimeError('Isolated restore command failed; protected local log: ' + str(log_path))
                return result.stdout
            command([PG / 'initdb', '-D', cluster, '--auth-local=trust', '--auth-host=reject', '--no-locale', '-E', 'UTF8'])
            # No TCP listener. The Unix socket is inside an operator-only directory.
            server = subprocess.Popen([str(PG / 'postgres'), '-D', str(cluster), '-h', '', '-k', str(socket_dir), '-p', '6547'], stdout=log, stderr=log, env=env)
            deadline = time.monotonic() + 30
            while subprocess.run([str(PG / 'pg_isready'), '-h', str(socket_dir), '-p', '6547'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode:
                if server.poll() is not None or time.monotonic() >= deadline:
                    raise RuntimeError('The isolated PostgreSQL instance did not become ready.')
                time.sleep(0.2)
            command([PG / 'createdb', '--maintenance-db=postgres', 'requestportal_restore'])
            command([PG / 'pg_restore', '--exit-on-error', '--single-transaction', '--no-owner', '--no-privileges', '--no-tablespaces', '--dbname=requestportal_restore', archive])
            sql = '\n'.join('SELECT COUNT(*) FROM ' + quote(row['schema']) + '.' + quote(row['name']) + ';' for row in backup['tables'])
            counts = command([PG / 'psql', '-X', '-A', '-t', '-v', 'ON_ERROR_STOP=1'], sql.encode()).decode().splitlines()
            if [int(value) for value in counts] != [row['count'] for row in backup['tables']]:
                raise RuntimeError('Restored table counts do not match the exported source snapshot.')
            migrations = command([PG / 'psql', '-X', '-A', '-t', '-v', 'ON_ERROR_STOP=1'], b'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";').decode().splitlines()
            if migrations != backup['migrations']:
                raise RuntimeError('Restored migration history does not match.')
            result = {'status': 'isolated-restore-verified', 'synthetic': report.get('synthetic') is True, 'archiveSha256': backup['archiveSha256'],
                      'reportSha256': report_sha, 'verifiedTables': len(counts), 'migrationsVerified': True,
                      'productionBusinessWrites': False, 'networkListener': False,
                      'restoreOwnershipAndGrantsReplayed': False, 'restoreEngineMajor': 18, 'sourceEngineMajor': backup['source']['major']}
            (export / 'restore-verification.json').write_text(json.dumps(result, indent=2) + '\n')
            os.chmod(export / 'restore-verification.json', 0o600)
            success = True
            return result
    finally:
        if server is not None and server.poll() is None:
            subprocess.run([str(PG / 'pg_ctl'), '-D', str(cluster), 'stop', '-m', 'fast', '-w'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=60)
        if success and (server is None or server.poll() is not None):
            # Only the freshly allocated private verification cluster is removed. The backup remains.
            shutil.rmtree(work)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--export-dir', type=Path, required=True)
    parser.add_argument('--report-sha256', required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(verify(args.export_dir.resolve(), args.report_sha256)))
    except Exception as error:
        print(json.dumps({'status': 'blocked', 'errorType': type(error).__name__,
                          'detail': str(error) if isinstance(error, RuntimeError) else 'Isolated restore verification failed.'}))
        raise SystemExit(1)
