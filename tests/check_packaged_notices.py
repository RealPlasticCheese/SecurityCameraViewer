"""Verify the licenses actually shipped in a published or installed server."""

import json
from pathlib import Path
import sys


def check(publish):
    dependencies = json.loads((publish / 'SecurityCameraViewer.Web.deps.json').read_text(encoding='utf-8-sig'))
    inventory = json.loads((publish / 'third-party-packages.json').read_text(encoding='utf-8'))
    expected = {name.removeprefix('runtimepack.') for name, details in dependencies['libraries'].items()
                if details['type'] != 'project'}
    actual = {f"{item['id']}/{item['version']}" for item in inventory}
    assert actual == expected, f'Notice inventory differs from resolved dependencies: {actual ^ expected}'
    for package in inventory:
        assert package['notices'], f"No notices for {package['id']}"
        for filename in package['notices']:
            source = (publish / filename).resolve()
            assert source.is_relative_to(publish.resolve()) and source.is_file(), f'Missing notice: {filename}'
            assert source.stat().st_size, f'Empty notice: {filename}'
    for name in ('LICENSE', 'THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-PACKAGES.md', 'MediaMTX-LICENSE.txt',
                 'wwwroot/vendor/glfx/LICENSE', 'wwwroot/vendor/lucide/LICENSE',
                 'wwwroot/vendor/camera-decoder/LICENSE-LGPL.txt',
                 'wwwroot/vendor/camera-decoder/LICENSE-EMSCRIPTEN.txt',
                 'wwwroot/vendor/camera-decoder/ffmpeg-8.0.1.tar.xz',
                 'wwwroot/vendor/camera-decoder/decoder.c', 'wwwroot/vendor/camera-decoder/build.sh'):
        assert (publish / name).is_file(), f'Missing bundled component notice or source: {name}'
    attribution = (publish / 'THIRD-PARTY-NOTICES.md').read_text(encoding='utf-8')
    assert 'Frank Tzanabetis' in attribution and 'https://plasticcheese.com' in attribution
    print(f'PASS: Package attribution, dependency notices, and decoder source verified for {len(inventory)} dependencies.')


if __name__ == '__main__':
    check(Path(sys.argv[1]))
