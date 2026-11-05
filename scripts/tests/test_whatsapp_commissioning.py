import contextlib
import importlib.util
import io
import json
import pathlib
import tempfile
import types
import unittest
from unittest.mock import patch

SOURCE = pathlib.Path(__file__).resolve().parents[1] / 'Commission-WhatsApp-Kratos.py'
spec = importlib.util.spec_from_file_location('whatsapp_commission', SOURCE)
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)


class ConfigurationTests(unittest.TestCase):
    def test_preserves_unrelated_bytes_and_is_idempotent(self):
        original = b'# unrelated\r\nSECRET="fixture-only $() `command`"\r\nNotifications__WhatsAppEnabled=false\n'
        original += b'NOTIFICATIONS__WHATSAPPWEBHOOKENABLED=false\n'
        actual = c.patch_environment(original)
        self.assertTrue(actual.startswith(original.split(b'NOTIFICATIONS')[0]))
        self.assertEqual(actual, c.patch_environment(actual))
        for key, value in c.EXPECTED.items():
            self.assertEqual(1, actual.count((key + '=').encode()))
            self.assertIn((key + '=' + value).encode(), actual)
        self.assertNotIn(b'NOTIFICATIONS__WHATSAPPWEBHOOKENABLED=false', actual)
        self.assertNotIn(b'access-token', actual)

    def test_refuses_unsafe_environment_and_enabled_outbound(self):
        for value in [b'Notifications__WhatsAppEnabled=true\n', b'KEY="multiline\ncredential"\n',
                      b'KEY=unquoted spaces\n', b'KEY=continued\\\nline\n', b'export KEY=value\n']:
            with self.subTest(value=value), self.assertRaises(c.Stop):
                c.patch_environment(value)

    def test_nginx_preserves_every_proxy_tls_and_redirect_directive(self):
        original = b'server {\n    return 301 https://$host$request_uri;\n}\nserver {\n    ssl_certificate /protected/cert;\n    location / { proxy_pass http://127.0.0.1:5401; }\n}\n'
        with patch.object(c, 'BASE_SITE_HASH', c.digest(original)):
            candidate = c.patch_site(original)
            self.assertEqual(original, candidate[len(c.MAP.encode()):].replace(c.LOG.encode(), b''))
            self.assertEqual(2, candidate.count(c.LOG.encode()))
            self.assertEqual(candidate, c.patch_site(candidate))
            with self.assertRaises(c.Stop):
                c.patch_site(candidate.replace(b'5401', b'5402'))
            with self.assertRaises(c.Stop):
                c.patch_site(original + b'# drift\n')

    def test_rollback_prevalidates_all_backups_before_changing_either_file(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            env, site = root / 'env', root / 'site'
            env.write_bytes(b'new-env'); site.write_bytes(b'new-site')
            (root / 'env.before').write_bytes(b'old-env')
            (root / 'site.before').write_bytes(b'tampered')
            (root / 'result.json').write_text(json.dumps({'envBefore': c.digest(b'old-env'), 'envAfter': c.digest(b'new-env'),
                'siteBefore': c.digest(b'old-site'), 'siteAfter': c.digest(b'new-site')}))
            with patch.object(c, 'ENV', env), patch.object(c, 'SITE', site), patch.object(c, 'protected'), patch.object(c, 'atomic_replace') as replace:
                with self.assertRaises(c.Stop): c.restore(root)
                replace.assert_not_called()
            self.assertEqual(b'new-env', env.read_bytes())

    def test_no_root_or_wrong_host_cannot_start(self):
        for uid, host in [(1000, 'kratos'), (0, 'other')]:
            with patch.object(c.os, 'geteuid', return_value=uid), patch.object(c.socket, 'gethostname', return_value=host), \
                 patch.object(c.sys, 'argv', ['commission', '--apply']), patch.object(c, 'commission') as apply:
                with self.assertRaises(c.Stop): c.main()
                apply.assert_not_called()

    def test_native_errors_never_echo_commands_or_provider_output(self):
        with patch.object(c.subprocess, 'run', return_value=types.SimpleNamespace(returncode=1, stdout='PRIVATE', stderr='PRIVATE')):
            with self.assertRaises(c.Stop) as error: c.native(['PRIVATE'])
            self.assertNotIn('PRIVATE', str(error.exception))

    def test_probe_uses_private_environment_snapshot_and_redacts_failed_output(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            def run(args, **kwargs):
                self.assertNotIn('PRIVATE', ' '.join(args))
                snapshot = pathlib.Path(next(x.split('=', 2)[2] for x in args if x.startswith('--property=EnvironmentFile=')))
                self.assertIn(b'PRIVATE', snapshot.read_bytes())
                self.assertEqual(0, snapshot.stat().st_mode & 0o077)
                return types.SimpleNamespace(returncode=1, stdout='{"status":"failed","phase":"approle-api-token-read"}\n', stderr='PRIVATE')
            with patch.object(c.subprocess, 'run', side_effect=run):
                with self.assertRaises(c.Stop) as error:
                    c.run_probe(root, root, b'', b'BOOTSTRAP=PRIVATE\n', 'preflight', 1)
            self.assertIn('approle-api-token-read', str(error.exception))
            self.assertNotIn('PRIVATE', str(error.exception))
            self.assertEqual([], list(root.glob('*.env')))

    def test_candidate_nginx_test_substitutes_only_portal_include(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary).resolve()
            site = root / 'portal'; site.write_text('live portal')
            main = root / 'nginx.conf'; main.write_text('http { include /etc/nginx/sites-enabled/*; }')
            enabled = root / 'enabled'; enabled.mkdir()
            (enabled / 'portal').symlink_to(site)
            other = enabled / 'other'; other.write_text('other site')
            work = root / 'work'; work.mkdir()
            real_path = pathlib.Path
            def path(value): return enabled if str(value) == '/etc/nginx/sites-enabled' else real_path(value)
            with patch.object(c, 'SITE', site), patch.object(c, 'MAIN', main), patch.object(c, 'Path', side_effect=path), \
                 patch.object(c, 'native', return_value='live-graph') as native:
                c.validate_nginx(work, b'candidate portal')
                self.assertEqual([c.NGINX, '-t'], native.call_args_list[0].args[0])
                self.assertIn('-c', native.call_args_list[1].args[0])
            candidate = (work / 'nginx-main.candidate').read_text()
            self.assertIn(str(work / 'nginx-site.candidate'), candidate)
            self.assertIn(str(other), candidate)
            self.assertEqual('live portal', site.read_text())
            self.assertEqual('http { include /etc/nginx/sites-enabled/*; }', main.read_text())


class ProcedureTests(unittest.TestCase):
    def run_case(self, mode='apply', fail=None, idempotent=False):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            env, site, main = root / 'env', root / 'site', root / 'main'
            env.write_bytes(b'# original\nPRESERVED="fixture-only"\n')
            site.write_bytes(b'original-site'); main.write_bytes(b'original-main')
            original_env, original_site = env.read_bytes(), site.read_bytes()
            candidate_env = c.patch_environment(original_env)
            candidate_site = b'safe-candidate-site'
            if idempotent:
                env.write_bytes(candidate_env); site.write_bytes(candidate_site)
                original_env, original_site = env.read_bytes(), site.read_bytes()
            operations = []
            root_stat = types.SimpleNamespace(st_uid=0, st_gid=0, st_mode=0o100600)
            def native(args, timeout=35):
                operations.append(args)
                if args == [c.NGINX, '-T']: return 'unchanged-configuration-graph'
                if fail == 'reload' and args == ['/usr/bin/systemctl', 'reload', 'nginx'] and sum(x == args for x in operations) == 1:
                    raise c.Stop('fixture reload failed')
                return ''
            def probe(bundle, work, prefix, environment, probe_mode, since):
                operations.append(['probe', probe_mode])
                if fail == probe_mode: raise c.Stop('fixture private validation failed')
                if probe_mode == 'preflight':
                    self.assertEqual(original_env, env.read_bytes())
                    self.assertEqual(original_site, site.read_bytes())
                    self.assertTrue((work / 'env.before').exists())
                if probe_mode == 'verify':
                    self.assertEqual(candidate_env, env.read_bytes())
                    self.assertEqual(candidate_site, site.read_bytes())
                return [{'status': 'ok', 'mode': probe_mode}]
            def replace(path, data, metadata):
                operations.append(['replace', path.name]); path.write_bytes(data)
            (root / 'access.log').write_text('ordinary logs')
            real_path = pathlib.Path
            def path(value):
                if str(value) == '/var/log/nginx/access.log': return root / 'access.log'
                if str(value) == '/var/log/nginx/access.log.1': return root / 'missing.log'
                return real_path(value)
            output = io.StringIO()
            with patch.object(c, 'ENV', env), patch.object(c, 'SITE', site), patch.object(c, 'MAIN', main), \
                 patch.object(c, 'AUDIT', root / 'audit'), patch.object(c, 'check_bundle'), patch.object(c, 'protected', return_value=root_stat), \
                 patch.object(c, 'protected_directory'), patch.object(c, 'service_context', return_value=b''), \
                 patch.object(c, 'patch_site', return_value=candidate_site), patch.object(c, 'health'), \
                 patch.object(c, 'validate_nginx', return_value=c.digest(b'unchanged-configuration-graph')), \
                 patch.object(c, 'native', side_effect=native), patch.object(c, 'run_probe', side_effect=probe), \
                 patch.object(c, 'atomic_replace', side_effect=replace), patch.object(c.urllib.request, 'urlopen'), \
                 patch.object(c, 'Path', side_effect=path), contextlib.redirect_stdout(output):
                if fail:
                    with self.assertRaises(c.Stop): c.commission(mode, root)
                else: c.commission(mode, root)
            result = json.loads(next((root / 'audit').glob('*/result.json')).read_text())
            self.assertNotIn('fixture-only', output.getvalue())
            backups = next((root / 'audit').iterdir())
            self.assertEqual(original_env, (backups / 'env.before').read_bytes())
            self.assertEqual(0, (backups / 'env.before').stat().st_mode & 0o077)
            if mode != 'apply' or fail in ('preflight', 'scan-logs'):
                self.assertEqual(original_env, env.read_bytes()); self.assertEqual(original_site, site.read_bytes())
                self.assertFalse(any(x[0] == 'replace' for x in operations))
            elif fail:
                self.assertEqual('rolled-back', result['status'])
                self.assertEqual(original_env, env.read_bytes()); self.assertEqual(original_site, site.read_bytes())
            else:
                self.assertEqual('commissioned', result['status'])
                self.assertEqual(candidate_env, env.read_bytes()); self.assertEqual(candidate_site, site.read_bytes())
                self.assertLess(operations.index(['probe', 'preflight']), operations.index(['replace', 'site']))
                self.assertLess(operations.index(['/usr/bin/systemctl', 'reload', 'nginx']), operations.index(['replace', 'env']))
                self.assertIn(['probe', 'verify'], operations)
            return result

    def test_dry_run_has_no_production_writes(self): self.run_case('dry-run')
    def test_preflight_has_no_production_writes(self): self.run_case('preflight')
    def test_missing_secrets_prevent_enablement(self): self.run_case(fail='preflight')
    def test_existing_secret_in_logs_prevents_enablement(self): self.run_case(fail='scan-logs')
    def test_success_applies_logging_before_enablement(self): self.run_case()
    def test_repeat_apply_is_idempotent(self): self.run_case(idempotent=True)
    def test_failed_reload_rolls_back_both_files(self): self.run_case(fail='reload')
    def test_failed_https_or_durable_verification_rolls_back(self): self.run_case(fail='verify')


if __name__ == '__main__': unittest.main()
