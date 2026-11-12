"""Loopback Eufy-style sleep/wake fixture. Never contacts a real camera or uses saved credentials."""
import json
from web_settings import document
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

root = Path(__file__).resolve().parent.parent
directory = root / 'artifacts' / ('battery-tests-' + uuid.uuid4().hex)
directory.mkdir(parents=True)
assembly = Path(sys.argv[1] if len(sys.argv) > 1 else root / 'artifacts/web/SecurityCameraViewer.Web.dll').resolve()

def port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        return s.getsockname()[1]

rtsp, web = port(), port()
base = f'http://127.0.0.1:{web}'
sleep = directory / 'sleep'
sleep.touch()
source_id = uuid.uuid4().hex
settings = dict(configurationId='default', configurationName='Battery fixture', autoConnect=True,
                channelCount=1, sources=[dict(id=source_id, channel=1, name='TEST VIDEO — battery fixture',
                    mainStream=f'rtsp://127.0.0.1:{rtsp}/live3', username='viewer', useDefaultPassword=True,
                    motionActivated=True, pollIntervalSeconds=0.5)])
settings_path = directory / 'settings.json'
def persisted(value):
    if isinstance(value, dict): return {k[0].upper() + k[1:]: persisted(v) for k, v in value.items()}
    if isinstance(value, list): return [persisted(v) for v in value]
    return value
settings_path.write_text(json.dumps(document(persisted(settings), "local-only-test")))

def request(path, value=None):
    req = urllib.request.Request(base + path, data=None if value is None else json.dumps(value).encode(),
                                 headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=10) as response:
        data = response.read()
        return json.loads(data) if data else None

def until(predicate, name, seconds=25):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        try:
            if predicate():
                print('PASS:', name, flush=True)
                return
        except (urllib.error.URLError, KeyError, IndexError):
            pass
        time.sleep(.15)
    raise AssertionError(name)

def camera():
    return request('/api/state')['cameras'][0]

fixture = server = None
try:
    env = dict(os.environ, RTSP_FIXTURE_PORT=str(rtsp), RTSP_FIXTURE_SLEEP_FILE=str(sleep),
               RTSP_FIXTURE_AUDIO_CHANNELS='all')
    fixture = subprocess.Popen([sys.executable, str(root / 'tests/rtsp_fixture.py')], cwd=root, env=env,
                               stdout=(directory / 'fixture.log').open('w'), stderr=subprocess.STDOUT)
    command = ['dotnet', str(assembly), '--urls', base, '--settings', str(settings_path)]
    server = subprocess.Popen(command, cwd=root, stdout=(directory / 'server.log').open('w'), stderr=subprocess.STDOUT)
    until(lambda: camera()['status'] == 'waiting', 'An idle 404 shows waiting, not a connection alarm')
    assert camera()['lastSeenAt'] is None
    for interval in [-0.1, 0, 0.09, 0.15, 1.25, 3600.1]:
        try:
            bad = json.loads(json.dumps(settings)); bad['sources'][0]['pollIntervalSeconds'] = interval
            request('/api/connection', dict(settings=bad))
            raise AssertionError(f'invalid polling accepted: {interval}')
        except urllib.error.HTTPError as e:
            assert e.code == 400
    print('PASS: API rejects out-of-range and more precise poll intervals', flush=True)
    for interval in [0.1, 1.6, 0.5]:
        settings['sources'][0]['pollIntervalSeconds'] = interval
        request('/api/setup/configurations', dict(settings=settings))
        assert request('/api/state')['settings']['sources'][0]['pollIntervalSeconds'] == interval
    print('PASS: Camera setup saves 0.1, 0.5 and 1.6 seconds without truncation', flush=True)
    until(lambda: camera()['status'] == 'waiting', 'Fractional interval is active')
    rec = request('/api/recording')['settings']
    rec.update(enabled=True, directory=str(directory / 'recordings'), preCaptureSeconds=0)
    rec['cameras'][0].update(enabled=True, recordWhenStreaming=True)
    request('/api/recording', rec)
    until(lambda: request('/api/recording')['cameras'][0]['status'] == 'waiting', 'Idle recording waits without decoder errors')
    assert request('/api/recordings') == []
    sleep.unlink()
    until(lambda: camera()['live'], 'Automatic polling discovers video after the camera wakes')
    until(lambda: request('/api/recording')['cameras'][0]['status'] == 'recording', 'Stream availability starts recording without software motion')
    until(lambda: len(request('/api/recordings')) > 0, 'An activity clip is created')
    time.sleep(3)
    sleep.touch()
    until(lambda: camera()['status'] == 'waiting', 'Viewer returns to waiting when the stream disappears')
    assert camera()['lastSeenAt'] is not None and camera()['frameAgeSeconds'] > 0
    with urllib.request.urlopen(base + '/api/cameras/1/snapshot') as response:
        assert response.read().startswith(b'\xff\xd8')
    print('PASS: Sleeping tile retains a JPEG and last-seen timestamp', flush=True)
    until(lambda: any(e['status'] == 'ready' for e in request('/api/recordings')), 'Activity recording finalizes to MP4 after stream end', 50)
    event = next(e for e in request('/api/recordings') if e['status'] == 'ready')
    assert event['trigger']['source'] == 'stream' and not event['interrupted']
    clip = directory / 'checked.mp4'
    with urllib.request.urlopen(base + '/api/recordings/' + event['id'] + '/video') as response:
        clip.write_bytes(response.read())
    subprocess.run(['ffmpeg', '-v', 'error', '-xerror', '-i', str(clip), '-f', 'null', '-'], check=True, capture_output=True)
    print('PASS: Saved activity clip decodes and is not marked interrupted', flush=True)
    streams = json.loads(subprocess.check_output(['ffprobe', '-v', 'error', '-count_packets',
        '-show_entries', 'stream=codec_type,codec_name,start_time,duration,nb_read_packets', '-of', 'json', str(clip)]))['streams']
    video = next(stream for stream in streams if stream['codec_type'] == 'video')
    audio = next(stream for stream in streams if stream['codec_type'] == 'audio')
    assert audio['codec_name'] == 'aac' and int(audio['nb_read_packets']) > 0
    assert abs(float(audio['start_time']) - float(video['start_time'])) < .2
    assert abs(float(audio['duration']) - float(video['duration'])) < .3
    samples = subprocess.check_output(['ffmpeg', '-v', 'error', '-i', str(clip), '-map', '0:a:0',
        '-f', 's16le', 'pipe:1'])
    assert any(samples)
    print('PASS: Activity recording retains synchronized, audible audio after stream end', flush=True)
    # A long configured interval is persisted and Check now can interrupt that wait.
    settings['sources'][0]['pollIntervalSeconds'] = 60.5
    request('/api/connection', dict(settings=settings))
    until(lambda: camera()['status'] == 'waiting', 'Long interval is active')
    sleep.unlink()
    request('/api/cameras/1/check', {})
    until(lambda: camera()['live'], 'Check now bypasses a long idle interval', 15)
    sleep.touch()
    # Authentication errors must not become quiet sleep states.
    request('/api/connection', dict(settings=settings, password='incorrect-fixture-password'))
    until(lambda: camera()['status'] == 'error' and 'login rejected' in camera()['message'], 'Authentication rejection stays visible')
    request('/api/connection', dict(settings=settings, password='local-only-test'))
    server.terminate(); server.wait(timeout=15)
    server = subprocess.Popen(command, cwd=root, stdout=(directory / 'server.log').open('w'), stderr=subprocess.STDOUT)
    until(lambda: camera()['status'] == 'waiting', 'Battery settings survive server restart')
    restored = request('/api/state')['settings']['sources'][0]
    assert restored['motionActivated'] and restored['pollIntervalSeconds'] == 60.5
    assert request('/api/recording')['settings']['cameras'][0]['recordWhenStreaming']
    print('PASS: Fractional poll interval and recording mode persist together', flush=True)
finally:
    for process in [server, fixture]:
        if process is not None and process.poll() is None:
            process.terminate()
            try: process.wait(timeout=15)
            except subprocess.TimeoutExpired: process.kill(); process.wait()
