"""Validate notices against the resolved publish dependencies using synthetic packages."""

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('package_notices', ROOT / 'eng/package-notices.py')
notices = importlib.util.module_from_spec(spec)
spec.loader.exec_module(notices)


class PackageNoticesTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.publish = self.root / 'publish'
        self.publish.mkdir()
        self.cache = self.root / 'packages'
        self.assets = self.root / 'project.assets.json'
        self.libraries = {}

    def package(self, name, version='2.0.0', license_value='MIT', license_type='expression', files=None,
                runtime=False, copyright_notice='Copyright (c) Example authors', revision=None):
        directory = self.cache / name.lower() / version
        directory.mkdir(parents=True)
        metadata = ET.Element('metadata')
        ET.SubElement(metadata, 'license', type=license_type).text = license_value
        if copyright_notice:
            ET.SubElement(metadata, 'copyright').text = copyright_notice
        if revision:
            ET.SubElement(metadata, 'repository', commit=revision)
        document = ET.Element('package')
        document.append(metadata)
        ET.ElementTree(document).write(directory / f'{name.lower()}.nuspec', encoding='utf-8')
        for path, content in (files or {}).items():
            target = directory / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
        key = f'{"runtimepack." if runtime else ""}{name}/{version}'
        self.libraries[key] = {'type': 'runtimepack' if runtime else 'package'}
        return directory

    def collect(self):
        self.assets.write_text(json.dumps({'packageFolders': {str(self.cache): {}}, 'libraries': {}}))
        (self.publish / 'SecurityCameraViewer.Web.deps.json').write_text(json.dumps({
            'libraries': {'SecurityCameraViewer/1.0.0': {'type': 'project'}, **self.libraries},
        }))
        return notices.collect(self.publish, self.assets)

    def test_runtime_notices_follow_resolved_versions_and_include_both_runtime_packs(self):
        for name in ('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.AspNetCore.App.Runtime.win-x64'):
            self.package(name, version='10.0.15', runtime=True,
                         files={'LICENSE.TXT': b'runtime license', 'THIRD-PARTY-NOTICES.TXT': b'10.0.15 notices'})
        inventory = self.collect()
        self.assertEqual([item['version'] for item in inventory], ['10.0.15', '10.0.15'])
        self.assertTrue(all(len(item['notices']) == 2 for item in inventory))
        for item in inventory:
            self.assertEqual((self.publish / item['notices'][1]).read_bytes(), b'10.0.15 notices')

    def test_embedded_license_and_nested_notices_are_copied_unchanged(self):
        self.package('Inference', license_type='file', license_value='legal/terms.txt',
                     files={'legal/terms.txt': b'full custom terms\r\n',
                            'native/ThirdPartyNotices.txt': b'upstream notice', 'lib/ignored.dll': b'code'})
        inventory = self.collect()
        self.assertEqual(len(inventory[0]['notices']), 2)
        self.assertEqual((self.publish / 'third-party-licenses/Inference/2.0.0/legal/terms.txt').read_bytes(),
                         b'full custom terms\r\n')
        self.assertFalse(list((self.publish / 'third-party-licenses').rglob('*.dll')))

    def test_mit_expression_preserves_package_copyright(self):
        self.package('Managed.Library', copyright_notice='Copyright © 2026 Library contributors')
        self.collect()
        license_text = (self.publish / 'third-party-licenses/Managed.Library/2.0.0/LICENSE.txt').read_text(encoding='utf-8')
        self.assertIn('Copyright © 2026 Library contributors', license_text)
        self.assertIn('Permission is hereby granted', license_text)
        self.assertNotIn('Frank Tzanabetis', license_text)

    def test_sqlitepclraw_includes_the_pinned_upstream_notice(self):
        self.package('SQLitePCLRaw.core', version='3.0.5', license_value='Apache-2.0',
                     revision='source-revision')
        inventory = self.collect()
        self.assertEqual(inventory[0]['repository']['commit'], 'source-revision')
        for name in ('LICENSE.TXT', 'NOTICE.TXT'):
            self.assertEqual((self.publish / 'third-party-licenses/SQLitePCLRaw.core/3.0.5' / name).read_bytes(),
                             (ROOT / 'packaging/licenses/SQLitePCLRaw' / name).read_bytes())

    def test_changed_sqlite_version_requires_the_matching_notice(self):
        self.package('SQLitePCLRaw.core', version='4.0.0', license_value='Apache-2.0')
        with self.assertRaisesRegex(ValueError, 'Refresh its vendored license'):
            self.collect()

    def test_public_domain_file_is_preserved_without_replacing_it_with_package_metadata(self):
        self.package('SQLite', license_type='file', license_value='LICENSE.txt',
                     files={'LICENSE.txt': b'SQLite is Public Domain'})
        self.collect()
        self.assertEqual((self.publish / 'third-party-licenses/SQLite/2.0.0/LICENSE.txt').read_bytes(),
                         b'SQLite is Public Domain')

    def test_missing_restored_package_fails_before_overwriting_existing_notices(self):
        self.libraries['Missing/1.0.0'] = {'type': 'package'}
        previous = self.publish / 'third-party-licenses/previous.txt'
        previous.parent.mkdir()
        previous.write_text('preserve')
        with self.assertRaisesRegex(ValueError, 'Restored package is unavailable'):
            self.collect()
        self.assertEqual(previous.read_text(), 'preserve')

    def test_missing_or_escaping_license_files_are_rejected(self):
        for index, path in enumerate(('missing.txt', '../../outside.txt')):
            with self.subTest(path=path):
                self.package('Invalid' + str(index), license_type='file', license_value=path)
                (self.cache / 'outside.txt').write_text('outside')
                with self.assertRaisesRegex(ValueError, 'missing or invalid license'):
                    self.collect()
                self.libraries.clear()

    def test_unsupported_license_or_absent_copyright_requires_review(self):
        self.package('Unknown', license_value='Different-License')
        with self.assertRaisesRegex(ValueError, 'needs a full license text'):
            self.collect()
        self.libraries.clear()
        self.package('NoCopyright', copyright_notice=None)
        with self.assertRaisesRegex(ValueError, 'needs a full license text'):
            self.collect()

    def test_repeat_collection_replaces_stale_notices_and_inventory(self):
        self.package('Original')
        self.collect()
        self.libraries.clear()
        self.package('Replacement', version='3.0.0')
        self.collect()
        self.assertFalse((self.publish / 'third-party-licenses/Original').exists())
        inventory = json.loads((self.publish / 'third-party-packages.json').read_text(encoding='utf-8'))
        self.assertEqual([(item['id'], item['version']) for item in inventory], [('Replacement', '3.0.0')])
        self.assertIn('Replacement 3.0.0', (self.publish / 'THIRD-PARTY-PACKAGES.md').read_text())


if __name__ == '__main__':
    unittest.main()
