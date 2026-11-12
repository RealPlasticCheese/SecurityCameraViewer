"""Collect notices for the exact NuGet packages and runtime packs being published."""

import argparse
import json
from pathlib import Path
import re
import shutil
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
SQLITE_VERSION = '3.0.5'
NOTICE_NAME = re.compile(r'^(licen[cs]e|notice|third.?party.?notices?|copying|copyright)([._-].*)?$', re.I)


def package_directory(package, assets):
    name, version = package.removeprefix('runtimepack.').split('/')
    relative = assets['libraries'].get(package, {}).get('path', f'{name.lower()}/{version}')
    for folder in assets['packageFolders']:
        directory = Path(folder) / relative
        if directory.is_dir():
            return name, version, directory
    raise ValueError(f'Restored package is unavailable: {package}')


def package_notices(name, version, directory, metadata):
    files = {
        str(path.relative_to(directory)).replace('\\', '/'): path.read_bytes()
        for path in sorted(directory.rglob('*'))
        if path.is_file() and NOTICE_NAME.fullmatch(path.name)
    }
    license_element = metadata.find('{*}license')
    if license_element is None or not license_element.text:
        raise ValueError(f'{name} has no declared license. Review its upstream notices.')
    license_value = license_element.text.strip()
    if license_element.get('type') == 'file':
        source = (directory / license_value).resolve()
        if not source.is_relative_to(directory.resolve()) or not source.is_file():
            raise ValueError(f'{name} declares a missing or invalid license file: {license_value}')
        files[license_value] = source.read_bytes()
    elif name.startswith('SQLitePCLRaw.'):
        if license_value != 'Apache-2.0' or version != SQLITE_VERSION:
            raise ValueError(f'{name} changed version or license. Refresh its vendored license and NOTICE.')
        for filename in ('LICENSE.TXT', 'NOTICE.TXT'):
            files[filename] = (ROOT / 'packaging/licenses/SQLitePCLRaw' / filename).read_bytes()
    elif not any(path.lower().startswith(('license', 'licence', 'copying')) for path in files):
        copyright_notice = metadata.findtext('{*}copyright')
        if license_value != 'MIT' or not copyright_notice:
            raise ValueError(f'{name} needs a full license text for {license_value}.')
        template = (ROOT / 'packaging/licenses/MIT.txt').read_text(encoding='utf-8')
        files['LICENSE.txt'] = template.replace('{copyright}', copyright_notice).encode('utf-8')
    return license_value, files


def collect(publish, assets_path):
    assets = json.loads(assets_path.read_text(encoding='utf-8-sig'))
    dependencies = json.loads((publish / 'SecurityCameraViewer.Web.deps.json').read_text(encoding='utf-8-sig'))
    inventory = []
    outputs = {}
    for package, details in sorted(dependencies['libraries'].items()):
        if details['type'] == 'project':
            continue
        name, version, directory = package_directory(package, assets)
        metadata = ET.parse(directory / f'{name.lower()}.nuspec').getroot().find('{*}metadata')
        if metadata is None:
            raise ValueError(f'{package} is missing NuGet metadata.')
        license_value, files = package_notices(name, version, directory, metadata)
        repository = metadata.find('{*}repository')
        inventory.append({
            'id': name,
            'version': version,
            'license': license_value,
            'copyright': metadata.findtext('{*}copyright', ''),
            'repository': repository.attrib if repository is not None else {},
            'notices': [f'third-party-licenses/{name}/{version}/{filename}' for filename in files],
        })
        for filename, content in files.items():
            outputs[Path(name) / version / filename] = content

    # Resolve every dependency before writing, so missing notices fail the build before packaging.
    destination = publish / 'third-party-licenses'
    if destination.is_symlink() or destination.resolve().parent != publish.resolve():
        raise ValueError('The notice directory must be inside the publish directory.')
    if destination.exists():
        shutil.rmtree(destination)
    for relative, content in outputs.items():
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content)
    (publish / 'third-party-packages.json').write_text(
        json.dumps(inventory, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    lines = ['# Bundled .NET and NuGet dependencies', '',
             'The following versions were resolved for this server build. Full license and notice files accompany each entry.', '']
    for package in inventory:
        lines.extend([f"## {package['id']} {package['version']}", '',
                      f"Declared license {package['license']}", '', package['copyright'], ''])
        lines.extend(f'- [{path}]({path})' for path in package['notices'])
        lines.append('')
    (publish / 'THIRD-PARTY-PACKAGES.md').write_text('\n'.join(lines), encoding='utf-8')
    return inventory


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('publish', type=Path)
    parser.add_argument('assets', type=Path)
    arguments = parser.parse_args()
    try:
        inventory = collect(arguments.publish, arguments.assets)
    except (OSError, ValueError, ET.ParseError) as error:
        parser.exit(1, f'Could not collect package notices: {error}\n')
    print(f'Collected licenses and notices for {len(inventory)} resolved dependencies.')


if __name__ == '__main__':
    main()
