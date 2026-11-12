"""Start the published server with external host configuration and no camera data."""
from contextlib import closing
import http.client
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time


def free_port():
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        return listener.getsockname()[1]


def run(assembly):
    assembly = str(Path(assembly).resolve())
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        config = root / 'host.json'
        selected_port = free_port()
        config.write_text(json.dumps({'urls': f'http://127.0.0.1:{selected_port}', 'settings': str(root / 'settings.json')}))
        for override in ('file', 'command', 'environment'):
            port = selected_port if override == 'file' else free_port()
            arguments = ['dotnet', assembly, '--host-config', str(config)]
            environment = dict(os.environ)
            if override == 'command':
                arguments += ['--urls', f'http://127.0.0.1:{port}']
            elif override == 'environment':
                environment['ASPNETCORE_URLS'] = f'http://127.0.0.1:{port}'
            with (root / 'server.log').open('w') as log:
                process = subprocess.Popen(arguments, cwd=root, env=environment, stdout=log, stderr=subprocess.STDOUT)
                try:
                    deadline = time.monotonic() + 30
                    while True:
                        try:
                            with closing(http.client.HTTPConnection('127.0.0.1', port, timeout=2)) as connection:
                                connection.request('GET', '/manifest.webmanifest')
                                response = connection.getresponse()
                                assert response.status == 200, response.status
                                assert json.loads(response.read())['name']
                            break
                        except OSError:
                            if process.poll() is not None or time.monotonic() >= deadline:
                                raise AssertionError((root / 'server.log').read_text())
                            time.sleep(0.1)
                finally:
                    process.terminate()
                    process.wait(timeout=15)
            print('PASS: external host configuration with ' + override + ' precedence from unrelated working directory')
        failure = subprocess.run(['dotnet', assembly, '--host-config', str(root / 'missing.json')], cwd=root, capture_output=True, timeout=30)
        assert failure.returncode != 0, 'Missing explicitly selected host file must fail'
        assert not (root / 'settings.json').exists(), 'Startup checks must not create camera settings'
        print('PASS: missing configuration fails without resetting camera data')


if __name__ == '__main__':
    run(sys.argv[1])
