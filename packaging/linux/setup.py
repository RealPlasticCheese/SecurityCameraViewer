#!/usr/bin/env python3
"""Host configuration for packaged servers. Camera configuration stays in the browser."""
import argparse
import ipaddress
import json
import os
from pathlib import Path, PurePosixPath
import re
import shlex
import subprocess
import sys
import tempfile

CONFIG = Path('/etc/security-camera-viewer/host.json')
DATA = Path('/var/lib/security-camera-viewer')
LEGACY_UNIT = Path('/etc/systemd/system/security-camera-viewer.service')
MARKER = '# Managed by SecurityCameraViewer web-service.sh'


def atomic_write(path, content, mode=0o600):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.is_symlink():
        raise ValueError(f'Refusing to replace symlink: {path}')
    descriptor, temporary = tempfile.mkstemp(dir=path.parent)
    try:
        with os.fdopen(descriptor, 'w', encoding='utf-8') as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def read_legacy_unit(text):
    if MARKER not in text.splitlines():
        raise ValueError('Existing service is not managed by web-service.sh. Migrate it explicitly first.')
    # The old script quotes systemd arguments, doubles %, and disables $ expansion.
    lines = dict(line.split('=', 1) for line in text.splitlines() if '=' in line and not line.startswith('#'))
    user = lines.get('User', '')
    if not re.fullmatch(r'[a-zA-Z_][a-zA-Z0-9_.-]*\$?', user) or user == 'root':
        raise ValueError('Existing service has no supported non-root account.')
    command = shlex.split(lines['ExecStart'].removeprefix(':').replace('%%', '%'))
    values = {}
    for option in ('urls', 'settings', 'ffmpeg'):
        flag = '--' + option
        if flag not in command or command.index(flag) + 1 >= len(command):
            raise ValueError(f'Existing service is missing {flag}.')
        values[option] = command[command.index(flag) + 1]
    application = next((argument for argument in command if argument.endswith('SecurityCameraViewer.Web.dll')), command[0])
    values['ai-model-directory'] = str(Path(application).parent / 'models')
    if '--ai-model-directory' in command:
        values['ai-model-directory'] = command[command.index('--ai-model-directory') + 1]
    if not (Path(values['settings']).is_absolute() or PurePosixPath(values['settings']).is_absolute()) or not PurePosixPath(values['ffmpeg']).is_absolute():
        raise ValueError('Existing settings and FFmpeg paths must be absolute.')
    return user, values


def configure(config_path=CONFIG, legacy_unit=LEGACY_UNIT, data_dir=DATA, host=None, port=None):
    config_path, legacy_unit, data_dir = map(Path, (config_path, legacy_unit, data_dir))
    user = 'security-camera-viewer'
    if legacy_unit.is_symlink():
        raise ValueError('Existing service must not be a symlink.')
    if legacy_unit.exists() and config_path.exists():
        raise ValueError('Host configuration and a legacy unit both exist; review the migration before continuing.')
    legacy = legacy_unit.exists()
    if config_path.exists():
        values = json.loads(config_path.read_text(encoding='utf-8'))
    elif legacy:
        if legacy_unit.with_name(legacy_unit.name + '.pre-package').exists():
            raise ValueError('Legacy service backup already exists; inspect it before migration.')
        user, values = read_legacy_unit(legacy_unit.read_text(encoding='utf-8'))
        if (legacy_unit.with_name(legacy_unit.name + '.d') / 'packaged-account.conf').exists():
            raise ValueError('A packaged-account override already exists; inspect it before migration.')
        settings = Path(values['settings'])
        if not settings.is_file():
            raise ValueError('Existing settings file is missing; refusing to reset camera configuration.')
        # Preserve the service account and absolute paths, including external recordings.
        backup = settings.with_name(settings.name + '.pre-package')
        if not backup.exists():
            descriptor = os.open(backup, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(descriptor, 'wb') as stream:
                stream.write(settings.read_bytes())
            os.chmod(backup, 0o600)
    else:
        values = {'urls': 'http://0.0.0.0:5080', 'settings': str(data_dir / 'settings.json'), 'ffmpeg': '/usr/bin/ffmpeg',
                  'ai-model-directory': str(data_dir / 'models')}
    if host is not None or port is not None:
        current = values['urls'].rsplit(':', 1)
        address = host if host is not None else current[0].removeprefix('http://').strip('[]')
        ipaddress.ip_address(address)
        number = port if port is not None else int(current[1])
        if not 1024 <= number <= 65535:
            raise ValueError('The non-root service needs a port between 1024 and 65535.')
        values['urls'] = f'http://[{address}]:{number}' if ':' in address else f'http://{address}:{number}'
    try:
        atomic_write(config_path, json.dumps(values, indent=2) + '\n', 0o644)
        if legacy:
            override = legacy_unit.with_name(legacy_unit.name + '.d') / 'packaged-account.conf'
            atomic_write(override, f'[Service]\nUser={user}\nGroup=\nWorkingDirectory=/\n', 0o644)
            # Keep the old complete unit for recovery; let systemd use the packaged unit.
            os.rename(legacy_unit, legacy_unit.with_name(legacy_unit.name + '.pre-package'))
    except OSError:
        if legacy:
            config_path.unlink(missing_ok=True)
            (legacy_unit.with_name(legacy_unit.name + '.d') / 'packaged-account.conf').unlink(missing_ok=True)
        raise
    return values


def caddy_site(hostname, backend_port, mode, https_port=443, provider=None):
    hostname = hostname.lower()
    if len(hostname) > 253 or not all(re.fullmatch(r'[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?', label)
                                      for label in hostname.split('.')):
        raise ValueError('Use a DNS hostname without scheme, path, or port.')
    if not 1024 <= backend_port <= 65535 or not 1 <= https_port <= 65535 or backend_port == https_port:
        raise ValueError('Use distinct valid backend and HTTPS ports; backend must be at least 1024.')
    if mode in ('public', 'dns') and ('.' not in hostname or hostname.endswith(('.local', '.localhost', '.internal'))):
        raise ValueError('Public certificates require a domain you control.')
    if mode in ('public', 'dns'):
        try:
            ipaddress.ip_address(hostname)
        except ValueError:
            pass
        else:
            raise ValueError('Public certificate setup requires a domain you control, not an IP address.')
    tls = ''
    if mode == 'internal':
        tls = '    tls internal\n'
    elif mode == 'dns':
        if not provider or not re.fullmatch(r'[a-z][a-z0-9_]*', provider):
            raise ValueError('DNS validation needs a Caddy DNS provider module name.')
        # Provider-specific credentials are set in the Caddy service environment, never here.
        credentials = ' {env.CLOUDFLARE_API_TOKEN}' if provider == 'cloudflare' else ''
        tls = f'    tls {{\n        dns {provider}{credentials}\n    }}\n'
    return (f'# Generated by SecurityCameraViewer setup; review before importing.\n'
            f'https://{hostname}:{https_port} {{\n{tls}    reverse_proxy 127.0.0.1:{backend_port}\n}}\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    setup = commands.add_parser('configure', help='Create/preserve host settings and migrate the old managed service')
    setup.add_argument('--bind', help='Bind IP, e.g. 127.0.0.1 behind Caddy')
    setup.add_argument('--port', type=int)
    setup.add_argument('--start', action='store_true', help='Enable and start/restart the systemd service')
    proxy = commands.add_parser('https', help='Generate a Caddy site without modifying existing sites or certificates')
    proxy.add_argument('--hostname', required=True)
    proxy.add_argument('--mode', choices=['public', 'dns', 'internal'], required=True)
    proxy.add_argument('--provider', help='DNS provider module; credentials belong in the Caddy service environment')
    proxy.add_argument('--backend-port', type=int, default=5080)
    proxy.add_argument('--https-port', type=int, default=443)
    proxy.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == 'configure':
            if os.geteuid() != 0:
                raise ValueError('Run configure with sudo.')
            values = configure(host=args.bind, port=args.port)
            subprocess.run(['systemctl', 'daemon-reload'], check=True)
            if args.start:
                subprocess.run(['systemctl', 'reenable', '--force', 'security-camera-viewer.service'], check=True)
                subprocess.run(['systemctl', 'restart', 'security-camera-viewer.service'], check=True)
            print('Server:', values['urls'], '\nSettings:', values['settings'])
            print('Set up cameras in the browser. Existing HTTPS configuration is unchanged.')
        else:
            if args.output.exists():
                raise ValueError('Output exists; choose a new path and compare before replacing.')
            atomic_write(args.output, caddy_site(args.hostname, args.backend_port, args.mode, args.https_port, args.provider), 0o644)
            print('Generated:', args.output)
            print('Review and import into your Caddyfile, validate with caddy validate, then reload Caddy.')
            if args.mode == 'dns':
                print(f'Install a Caddy build with dns.providers.{args.provider}; configure its provider credentials in the service environment.')
                print('Create local DNS resolving this hostname to the server. No inbound internet port is required for DNS validation.')
            elif args.mode == 'public':
                print('Public DNS must resolve to this server; ACME validation needs inbound port 80 or 443.')
            else:
                print('Trust the Caddy local CA on each client device.')
            print('Caddy handles renewal. See the included installation guide for validation and existing Namecheap/lego support.')
    except (ValueError, OSError, KeyError, subprocess.CalledProcessError) as error:
        print(f'Setup failed: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
