"""Resolve the package version for Windows, Linux, and GitHub Releases."""

import argparse
import re
import subprocess


VERSION_PATTERN = re.compile(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)')


def resolve_version(requested: str, repository: str) -> str:
    if requested:
        if not VERSION_PATTERN.fullmatch(requested):
            raise ValueError('Package version must be major.minor.patch without leading zeros.')
        return requested

    try:
        tag = subprocess.check_output(
            ['gh', 'release', 'view', '--repo', repository, '--json', 'tagName', '--jq', '.tagName'],
            text=True,
        ).strip()
    except (OSError, subprocess.CalledProcessError) as error:
        raise ValueError('Could not read the latest GitHub release. Enter a package version explicitly.') from error

    version = VERSION_PATTERN.fullmatch(tag.removeprefix('v'))
    if not version:
        raise ValueError(f'The latest release tag {tag!r} has no numeric package version. Enter a package version explicitly.')

    major, minor, patch = version.groups()
    return f'{major}.{minor}.{int(patch) + 1}'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('repository', help='GitHub repository in owner/name form')
    parser.add_argument('--version', default='', help='Override the next patch version')
    args = parser.parse_args()
    try:
        print(resolve_version(args.version, args.repository))
    except ValueError as error:
        parser.exit(1, f'{error}\n')
