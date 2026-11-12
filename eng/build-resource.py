#!/usr/bin/env python3
"""Validate/retain the exact Azure build consumed by a manual deployment."""
import argparse
import json
import os
import urllib.request

# Azure displays leases exceeding 36500 days as retained forever.
DEFAULT_RETENTION_DAYS = 36501


def retention_payload(build, owner, days):
    if days < 1:
        raise ValueError('Retention days must be positive.')
    return [{'daysValid': days, 'definitionId': build['definition']['id'], 'ownerId': owner,
             'protectPipeline': True, 'runId': build['id']}]


def validate_build(build):
    if build.get('status') != 'completed' or build.get('result') != 'succeeded':
        raise ValueError('Select a completed successful build.')
    if build.get('sourceBranch') != 'refs/heads/master':
        raise ValueError('Production deployment requires a build from master.')
    if build.get('definition', {}).get('name') != 'SecurityCameraViewer-Build':
        raise ValueError('The selected build is not the server packaging pipeline.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--retain', action='store_true')
    parser.add_argument('--retention-days', type=int, default=DEFAULT_RETENTION_DAYS)
    args = parser.parse_args()
    if args.retention_days < 1:
        raise ValueError('Retention days must be positive.')
    run = int(os.environ['SOURCE_BUILD_ID'])
    url = (os.environ['SYSTEM_COLLECTIONURI'] + os.environ['SYSTEM_TEAMPROJECTID'] +
           f'/_apis/build/builds/{run}?api-version=7.1')
    headers = {'Authorization': 'Bearer ' + os.environ['SYSTEM_ACCESSTOKEN']}
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=30) as response:
        build = json.load(response)
    validate_build(build)
    if args.retain:
        lease_url = (os.environ['SYSTEM_COLLECTIONURI'] + os.environ['SYSTEM_TEAMPROJECTID'] +
                     '/_apis/build/retention/leases?api-version=7.1')
        payload = retention_payload(build, 'Pipeline:' + os.environ['SYSTEM_DEFINITIONID'], args.retention_days)
        request = urllib.request.Request(lease_url, data=json.dumps(payload).encode('utf-8'),
                                         headers={**headers, 'Content-Type': 'application/json'}, method='POST')
        with urllib.request.urlopen(request, timeout=30) as response:
            json.load(response)
        print(f'Retained released build {run}.')
    else:
        print(f'Validated successful master build {run}.')


if __name__ == '__main__':
    main()
