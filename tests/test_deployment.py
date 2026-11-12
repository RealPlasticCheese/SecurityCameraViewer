"""Run deployment with shell functions replacing all privileged/network operations."""
import hashlib
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
HARNESS = r'''
record() { printf '%s\n' "$*" >> "$TEST_LOG"; }
sudo() {
    shift # -n
    record "$*"
    case "$1" in
        true) [[ ${TEST_SUDO:-yes} == yes ]] ;;
        test)
            case "$3" in
                */host.json) return 1 ;;
                */current.deb) [[ ${TEST_CACHED:-yes} == yes ]] ;;
                *) return 0 ;;
            esac ;;
        dpkg-deb) printf '%s\n' "${TEST_CACHE_VERSION:-1.0.1}" ;;
        apt-get)
            if [[ ${TEST_DOWNGRADE:-no} == yes && "$*" != *--allow-downgrades* ]]; then return 45; fi
            if [[ $2 != --download-only && ${TEST_INSTALL:-yes} != yes ]]; then return 44; fi ;;
        *) return 0 ;;
    esac
}
dpkg-query() { printf '%s' "${TEST_OLD_VERSION-1.0.1}"; }
systemctl() {
    record "systemctl $*"
    if [[ $1 == is-enabled ]]; then [[ ${TEST_ENABLED:-yes} == yes ]]; else [[ ${TEST_ACTIVE:-yes} == yes ]]; fi
}
curl() { record 'curl'; [[ ${TEST_HEALTH:-yes} == yes ]]; }
sleep() { :; }
source "$1" "$2" https://viewer.example/manifest.webmanifest
'''


@unittest.skipIf(os.name == 'nt', 'Linux deployment uses POSIX shell')
class DeploymentTests(unittest.TestCase):
    def run_deploy(self, **environment):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            package = root / 'server.deb'
            package.write_bytes(b'fixture')
            package.with_suffix('.deb.sha256').write_text(hashlib.sha256(b'fixture').hexdigest() + '  server.deb\n')
            log = root / 'commands'
            result = subprocess.run(['bash', '-c', HARNESS, 'test', str(ROOT / 'eng/deploy-linux.sh'), directory],
                                    env={**os.environ, 'TEST_LOG': str(log), **environment}, capture_output=True, text=True)
            return result, log.read_text() if log.exists() else ''

    def test_success_downloads_before_stop_and_caches_only_after_health(self):
        result, log = self.run_deploy()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertLess(log.index('apt-get --download-only'), log.index('systemctl stop'))
        self.assertLess(log.index('curl'), log.rindex('/var/cache/security-camera-viewer/current.deb'))
        self.assertNotIn('dpkg --install', log)

    def test_no_sudo_or_no_rollback_cache_fails_before_stopping_service(self):
        for options in ({'TEST_SUDO': 'no'}, {'TEST_CACHED': 'no'}, {'TEST_CACHE_VERSION': 'wrong'}):
            with self.subTest(options=options):
                result, log = self.run_deploy(**options)
                self.assertNotEqual(result.returncode, 0)
                self.assertNotIn('systemctl stop', log)

    def test_install_failure_restores_cached_package_and_original_running_state(self):
        result, log = self.run_deploy(TEST_INSTALL='no')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('dpkg --install /var/cache/security-camera-viewer/previous.deb', log)
        self.assertIn('systemctl start security-camera-viewer.service', log)

    def test_health_failure_rolls_back_without_marking_new_package_current(self):
        result, log = self.run_deploy(TEST_HEALTH='no')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('dpkg --install', log)
        self.assertNotIn('server.deb /var/cache/security-camera-viewer/current.deb', log)

    def test_first_migration_failure_restores_checkout_unit(self):
        result, log = self.run_deploy(TEST_OLD_VERSION='', TEST_HEALTH='no')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('dpkg --remove security-camera-viewer', log)
        self.assertIn('cp /var/cache/security-camera-viewer/legacy.service /etc/systemd/system/security-camera-viewer.service', log)

    def test_failed_update_does_not_start_previously_stopped_service(self):
        result, log = self.run_deploy(TEST_HEALTH='no', TEST_ACTIVE='no')
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn('systemctl start security-camera-viewer.service', log)

    def test_older_selected_build_allows_downgrade_during_preflight_and_install(self):
        result, log = self.run_deploy(TEST_OLD_VERSION='1.0.2', TEST_CACHE_VERSION='1.0.2', TEST_DOWNGRADE='yes')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('apt-get --download-only install -y --allow-downgrades', log)

    def test_failed_update_restores_disabled_boot_setting(self):
        result, log = self.run_deploy(TEST_HEALTH='no', TEST_ENABLED='no')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('systemctl disable security-camera-viewer.service', log)


if __name__ == '__main__':
    unittest.main()
