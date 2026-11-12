"""Packaging setup tests use temporary files and never touch host services."""
import importlib.util
import contextlib
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('package_setup', ROOT / 'packaging/linux/setup.py')
setup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(setup)
build_spec = importlib.util.spec_from_file_location('build_resource', ROOT / 'eng/build-resource.py')
build_resource = importlib.util.module_from_spec(build_spec)
build_spec.loader.exec_module(build_resource)


class HostSetupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.config = self.root / 'etc/host.json'
        self.unit = self.root / 'system/security-camera-viewer.service'
        self.data = self.root / 'data'

    def configure(self, **options):
        return setup.configure(self.config, self.unit, self.data, **options)

    def legacy(self, user='frank', marker=True, settings=True):
        self.data.mkdir(exist_ok=True)
        self.settings = self.data / 'private settings.json'
        if settings:
            self.settings.write_text('{"Version":1,"camera":"retained"}')
        self.unit.parent.mkdir(parents=True, exist_ok=True)
        self.unit.write_text((setup.MARKER + '\n' if marker else '') +
                             f'[Service]\nUser={user}\nExecStart=:"/opt/app/server" "--urls" "http://127.0.0.1:5081" '
                             f'"--settings" "{self.settings.as_posix()}" "--ffmpeg" "/usr/bin/ffmpeg"\n')

    def test_fresh_install_creates_external_defaults(self):
        values = self.configure()
        self.assertEqual(values['settings'], str(self.data / 'settings.json'))
        self.assertEqual(values['urls'], 'http://0.0.0.0:5080')
        self.assertFalse((self.data / 'settings.json').exists())
        self.assertEqual(json.loads(self.config.read_text()), values)

    def test_reconfigure_validates_ipv4_ipv6_and_port(self):
        self.assertEqual(self.configure(host='127.0.0.1', port=5081)['urls'], 'http://127.0.0.1:5081')
        self.assertEqual(self.configure(host='::1', port=6010)['urls'], 'http://[::1]:6010')
        self.assertEqual(self.configure(port=6011)['urls'], 'http://[::1]:6011')

    def test_invalid_options_leave_existing_configuration_unchanged(self):
        self.configure()
        before = self.config.read_bytes()
        for options in ({'port': 0}, {'port': 65536}, {'port': 80}, {'host': '127.0.0.1\ninvalid'}, {'host': 'foo;bar'}):
            with self.subTest(options=options), self.assertRaises(ValueError):
                self.configure(**options)
            self.assertEqual(self.config.read_bytes(), before)

    def test_upgrade_preserves_all_host_settings_and_camera_data(self):
        values = {'urls': 'http://127.0.0.1:9001', 'settings': '/external/settings.json', 'ffmpeg': '/external/ffmpeg', 'custom': 'keep'}
        self.config.parent.mkdir()
        self.config.write_text(json.dumps(values))
        self.assertEqual(self.configure(), values)

    def test_migration_preserves_account_listener_and_credentials_without_copying_recordings(self):
        self.legacy()
        values = self.configure()
        self.assertEqual(values['urls'], 'http://127.0.0.1:5081')
        self.assertEqual(values['settings'], self.settings.as_posix())
        self.assertEqual(self.settings.read_bytes(), self.settings.with_name(self.settings.name + '.pre-package').read_bytes())
        self.assertFalse(self.unit.exists())
        self.assertTrue(self.unit.with_name(self.unit.name + '.pre-package').exists())
        override = (self.unit.with_name(self.unit.name + '.d') / 'packaged-account.conf').read_text()
        self.assertIn('User=frank', override)
        self.assertIn('WorkingDirectory=/', override)
        self.assertEqual(self.configure(), values)

    def test_migration_rejects_unmanaged_root_or_missing_settings(self):
        for options in ({'marker': False}, {'user': 'root'}, {'settings': False}):
            with self.subTest(options=options):
                self.legacy(**options)
                if not options.get('settings', True) and self.settings.exists():
                    self.settings.unlink()
                with self.assertRaises(ValueError):
                    self.configure()
                self.assertTrue(self.unit.exists())
                self.assertFalse(self.config.exists())

    def test_malformed_host_document_is_not_replaced(self):
        self.config.parent.mkdir()
        self.config.write_text('invalid')
        with self.assertRaises(ValueError):
            self.configure()
        self.assertEqual(self.config.read_text(), 'invalid')

    def test_existing_service_backup_is_never_overwritten(self):
        self.legacy()
        backup = self.unit.with_name(self.unit.name + '.pre-package')
        backup.write_text('keep')
        with self.assertRaises(ValueError):
            self.configure()
        self.assertTrue(self.unit.exists())
        self.assertFalse(self.config.exists())
        self.assertEqual(backup.read_text(), 'keep')

    def test_failed_migration_leaves_original_service_and_can_be_retried(self):
        self.legacy()
        with patch.object(setup.os, 'rename', side_effect=OSError('fixture failure')):
            with self.assertRaises(OSError):
                self.configure()
        self.assertTrue(self.unit.exists())
        self.assertFalse(self.config.exists())
        self.assertFalse((self.unit.with_name(self.unit.name + '.d') / 'packaged-account.conf').exists())
        self.assertEqual(self.configure()['urls'], 'http://127.0.0.1:5081')

    @unittest.skipIf(os.name == 'nt' or not shutil.which('systemctl'), 'Offline systemd tools required')
    def test_migration_can_relink_existing_boot_service_to_packaged_unit(self):
        self.unit = self.root / 'etc/systemd/system/security-camera-viewer.service'
        self.legacy()
        with self.unit.open('a') as stream:
            stream.write('[Install]\nWantedBy=multi-user.target\n')
        package_units = self.root / 'usr/lib/systemd/system'
        package_units.mkdir(parents=True)
        shutil.copyfile(ROOT / 'packaging/linux/security-camera-viewer.service', package_units / self.unit.name)
        (package_units / 'multi-user.target').write_text('[Unit]\nDescription=Fixture target\n')
        command = ['systemctl', '--root', str(self.root)]
        subprocess.run([*command, 'enable', self.unit.name], check=True, capture_output=True)
        self.configure()
        subprocess.run([*command, 'reenable', '--force', self.unit.name], check=True, capture_output=True)
        link = self.root / 'etc/systemd/system/multi-user.target.wants' / self.unit.name
        self.assertEqual(os.readlink(link), '/usr/lib/systemd/system/' + self.unit.name)

    def test_failed_atomic_write_preserves_previous_config_and_cleans_temporary(self):
        self.config.parent.mkdir()
        self.config.write_text('original')
        with patch.object(setup.os, 'replace', side_effect=OSError('fixture failure')):
            with self.assertRaises(OSError):
                setup.atomic_write(self.config, 'replacement')
        self.assertEqual(self.config.read_text(), 'original')
        self.assertEqual(list(self.config.parent.iterdir()), [self.config])

    @unittest.skipIf(os.name == 'nt', 'POSIX permissions')
    def test_settings_backup_is_private(self):
        self.legacy()
        self.configure()
        self.assertEqual(self.settings.with_name(self.settings.name + '.pre-package').stat().st_mode & 0o777, 0o600)

    @unittest.skipIf(os.name == 'nt', 'POSIX symlinks')
    def test_atomic_write_refuses_symlink(self):
        destination = self.root / 'target'
        destination.write_text('keep')
        self.config.parent.mkdir()
        self.config.symlink_to(destination)
        with self.assertRaises(ValueError):
            setup.atomic_write(self.config, 'replace')
        self.assertEqual(destination.read_text(), 'keep')


class HttpsSetupTests(unittest.TestCase):
    def test_certificate_modes_generate_only_the_requested_site(self):
        public = setup.caddy_site('viewer.example.com', 5081, 'public')
        self.assertNotIn('tls internal', public)
        self.assertIn('reverse_proxy 127.0.0.1:5081', public)
        self.assertIn('tls internal', setup.caddy_site('camera-host.local', 5081, 'internal', 5080))
        self.assertIn('dns cloudflare', setup.caddy_site('viewer.example.com', 5081, 'dns', provider='cloudflare'))

    def test_hostname_and_provider_injection_rejected(self):
        for hostname in ('a\n{', '*.example.com', 'https://viewer.example', 'a..example', '-a.example', 'a' * 64 + '.example'):
            with self.subTest(hostname=hostname), self.assertRaises(ValueError):
                setup.caddy_site(hostname, 5081, 'public')
        for provider in (None, 'bad provider', 'a\nkey', '../plugin'):
            with self.subTest(provider=provider), self.assertRaises(ValueError):
                setup.caddy_site('viewer.example.com', 5081, 'dns', provider=provider)

    def test_private_hostnames_and_colliding_ports_rejected_for_public_certificates(self):
        for hostname in ('camera-host', 'camera-host.local', 'camera-host.internal', '192.168.1.50'):
            with self.assertRaises(ValueError):
                setup.caddy_site(hostname, 5081, 'public')
        with self.assertRaises(ValueError):
            setup.caddy_site('viewer.example.com', 5081, 'public', 5081)


class ReleaseSelectionTests(unittest.TestCase):
    def test_only_successful_master_packaging_builds_can_be_deployed(self):
        build = {'status': 'completed', 'result': 'succeeded', 'sourceBranch': 'refs/heads/master',
                 'definition': {'name': 'SecurityCameraViewer-Build'}}
        build_resource.validate_build(build)
        for change in ({'status': 'inProgress'}, {'result': 'failed'}, {'result': 'partiallySucceeded'},
                       {'sourceBranch': 'refs/heads/codex/test'}, {'definition': {'name': 'Other'}}):
            with self.subTest(change=change), self.assertRaises(ValueError):
                build_resource.validate_build({**build, **change})

    def test_retention_uses_the_supported_lease_post_api(self):
        build = {'id': 453, 'status': 'completed', 'result': 'succeeded', 'sourceBranch': 'refs/heads/master',
                 'definition': {'id': 17, 'name': 'SecurityCameraViewer-Build'}}
        requests = []

        def respond(request, timeout):
            requests.append(request)
            data = build if request.get_method() == 'GET' else [{'leaseId': 1}]
            return io.BytesIO(json.dumps(data).encode())

        environment = {'SOURCE_BUILD_ID': '453', 'SYSTEM_COLLECTIONURI': 'https://dev.azure.com/fixture/',
                       'SYSTEM_TEAMPROJECTID': 'project', 'SYSTEM_ACCESSTOKEN': 'fixture-token', 'SYSTEM_DEFINITIONID': '18'}
        output = io.StringIO()
        with patch.dict(os.environ, environment), patch.object(sys, 'argv', ['build-resource', '--retain']), \
                patch.object(build_resource.urllib.request, 'urlopen', side_effect=respond), contextlib.redirect_stdout(output):
            build_resource.main()
        self.assertEqual(requests[1].get_method(), 'POST')
        self.assertTrue(requests[1].full_url.endswith('/_apis/build/retention/leases?api-version=7.1'))
        payload = json.loads(requests[1].data)
        self.assertEqual(payload[0]['definitionId'], 17)
        self.assertEqual(payload[0]['runId'], 453)
        self.assertEqual(payload[0]['ownerId'], 'Pipeline:18')
        self.assertGreater(payload[0]['daysValid'], 36500)
        self.assertNotIn('fixture-token', output.getvalue())

    def test_retention_duration_can_be_overridden(self):
        build = {'id': 1, 'definition': {'id': 2}}
        self.assertEqual(build_resource.retention_payload(build, 'owner', 14)[0]['daysValid'], 14)

    def test_invalid_retention_duration_is_rejected(self):
        for days in (0, -1):
            with self.assertRaises(ValueError):
                build_resource.retention_payload({'id': 1, 'definition': {'id': 2}}, 'owner', days)


if __name__ == '__main__':
    unittest.main()
