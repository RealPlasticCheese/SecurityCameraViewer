"""Test package version overrides and the next patch selection without contacting GitHub."""

import importlib.util
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('package_version', ROOT / 'eng/package-version.py')
package_version = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package_version)
REPOSITORY = 'owner/security-camera-viewer'


class PackageVersionTests(unittest.TestCase):
    def test_explicit_version_and_pr_placeholder_skip_lookup(self):
        with patch.object(subprocess, 'check_output') as lookup:
            self.assertEqual(package_version.resolve_version('2.3.4', REPOSITORY), '2.3.4')
            self.assertEqual(package_version.resolve_version('0.0.0', REPOSITORY), '0.0.0')
            lookup.assert_not_called()

    def test_invalid_overrides_fail_without_a_lookup(self):
        with patch.object(subprocess, 'check_output') as lookup:
            for version in ('v1.0.491', '1.0', '1.00.491', '1.0.491-rc.1', '1.0.491\n', ' '):
                with self.subTest(version=version):
                    with self.assertRaisesRegex(ValueError, 'major.minor.patch'):
                        package_version.resolve_version(version, REPOSITORY)
            lookup.assert_not_called()

    def test_blank_version_increments_the_latest_release_patch(self):
        with patch.object(subprocess, 'check_output', return_value='v1.0.491\n') as lookup:
            self.assertEqual(package_version.resolve_version('', REPOSITORY), '1.0.492')
            lookup.assert_called_once_with(
                ['gh', 'release', 'view', '--repo', REPOSITORY, '--json', 'tagName', '--jq', '.tagName'],
                text=True,
            )

    def test_patch_growth_and_tags_without_prefix(self):
        with patch.object(subprocess, 'check_output', return_value='12.4.999\n'):
            self.assertEqual(package_version.resolve_version('', REPOSITORY), '12.4.1000')

    def test_release_lookup_failure_requires_an_explicit_version(self):
        with patch.object(subprocess, 'check_output', side_effect=subprocess.CalledProcessError(1, 'gh')):
            with self.assertRaisesRegex(ValueError, 'Could not read the latest GitHub release'):
                package_version.resolve_version('', REPOSITORY)

    def test_invalid_release_tag_requires_an_explicit_version(self):
        for tag in ('', 'latest', 'v1.0.491-rc.1', 'v01.0.491'):
            with self.subTest(tag=tag), patch.object(subprocess, 'check_output', return_value=tag):
                with self.assertRaisesRegex(ValueError, 'Enter a package version explicitly'):
                    package_version.resolve_version('', REPOSITORY)
