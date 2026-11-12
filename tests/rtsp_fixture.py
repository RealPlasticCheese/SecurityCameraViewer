"""Local-only RTSP/H.264 fixture with optional G.711 audio. Requires ffmpeg.

Run with Python, then launch the web server with --demo.
No recorder is contacted. Credentials are intentionally public test values.
"""
import base64
import os
import hashlib
import re
import socket
import socketserver
import struct
import subprocess
import threading
import time
import json
import math

hevc = os.environ.get("RTSP_FIXTURE_CODEC") == "hevc"
encoder = ["-c:v", "libx265", "-x265-params", "aud=1:keyint=10:bframes=0:repeat-headers=1:log-level=error"] if hevc else ["-c:v", "libx264", "-x264-params", "aud=1:keyint=10:repeat-headers=1"]
format_name = "hevc" if hevc else "h264"
shared_epoch = time.monotonic()
moving_scene = os.environ.get("RTSP_FIXTURE_MOVING_SCENE") == "1"
pacing_rate = float(os.environ.get("RTSP_FIXTURE_PACING_RATE", "1"))
if not math.isfinite(pacing_rate) or pacing_rate <= 0:
    raise ValueError("RTSP fixture pacing rate must be positive")
scene_lock = threading.Lock()
scene_pause_started = None
scene_pause_seconds = 0


def scene_time():
    global scene_pause_started, scene_pause_seconds
    with scene_lock:
        now = time.monotonic()
        pause_file = os.environ.get("RTSP_FIXTURE_PAUSE_FILE")
        if pause_file and os.path.exists(pause_file):
            if scene_pause_started is None:
                scene_pause_started = now
            return None
        if scene_pause_started is not None:
            scene_pause_seconds += now - scene_pause_started
            scene_pause_started = None
        return now, shared_epoch + scene_pause_seconds

scene = "color=black:size=640x360:rate=10,geq=lum='if(between(X,32+min(N,60-N)*12,96+min(N,60-N)*12)*between(Y,140,200),235,16)':cb=128:cr=128" if moving_scene else "testsrc2=size=640x360:rate=10"
generated = subprocess.run([
    "ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
    scene, "-t", "6" if moving_scene else "2", "-an",
    *encoder, "-preset", "ultrafast", "-tune", "zerolatency", "-f", format_name, "pipe:1"
], capture_output=True, check=True).stdout
nals = [n for n in re.split(b"\x00\x00\x00?\x01", generated) if n]
nal_type = lambda n: (n[0] >> 1) & 63 if hevc else n[0] & 31
sps = next(n for n in nals if nal_type(n) == (33 if hevc else 7))
vps = next((n for n in nals if hevc and nal_type(n) == 32), None)
pps = next(n for n in nals if nal_type(n) == (34 if hevc else 8))
frames = []
frame = []
for nal in nals:
    if nal_type(nal) == (35 if hevc else 9) and frame:
        frames.append(frame)
        frame = []
    frame.append(nal)
if frame:
    frames.append(frame)

# Optional audio on selected recorder channels, or every path with "all".
audio_channels = os.environ.get("RTSP_FIXTURE_AUDIO_CHANNELS", "").split(",")
audio_samples = b""
talkback_channels = os.environ.get("RTSP_FIXTURE_TALKBACK_CHANNELS", "").split(",")
talkback_modes = dict(item.split("=", 1) for item in os.environ.get("RTSP_FIXTURE_TALKBACK_MODES", "").split(",") if "=" in item)
talkback_log_lock = threading.Lock()
session_log_lock = threading.Lock()


def session_log(record):
    path = os.environ.get("RTSP_FIXTURE_SESSION_LOG_FILE")
    if path:
        with session_log_lock, open(path, "a", encoding="utf-8") as output:
            output.write(json.dumps(record) + "\n")


def talkback_log(record):
    path = os.environ.get("RTSP_FIXTURE_TALKBACK_LOG_FILE")
    if path:
        with talkback_log_lock, open(path, "a", encoding="utf-8") as output:
            output.write(json.dumps(record) + "\n")
if any(audio_channels):
    audio_samples = subprocess.run([
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "sine=frequency=440:sample_rate=8000", "-t", "2", "-c:a", "pcm_mulaw",
        "-f", "mulaw", "pipe:1"
    ], capture_output=True, check=True).stdout

# Optional static scene used to verify motion-triggered recording and pre-event RAM buffering.
still_frames = frames
if os.environ.get("RTSP_FIXTURE_STILL_FILE"):
    still_video = subprocess.run([
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=" + ("black" if moving_scene else "gray") + ":size=640x360:rate=10", "-t", "2", "-an", "-c:v", "libx264",
        "-preset", "ultrafast", "-tune", "zerolatency", "-x264-params",
        "aud=1:keyint=10:repeat-headers=1", "-f", "h264", "pipe:1"
    ], capture_output=True, check=True).stdout
    still_frames = []
    still_frame = []
    for nal in [n for n in re.split(b"\x00\x00\x00?\x01", still_video) if n]:
        if nal[0] & 31 == 9 and still_frame:
            still_frames.append(still_frame)
            still_frame = []
        still_frame.append(nal)
    if still_frame:
        still_frames.append(still_frame)


class Handler(socketserver.StreamRequestHandler):
    def handle(self):
        connection_id = str(time.time_ns())
        self.lock = threading.Lock()
        self.stopped = threading.Event()
        self.rtp_channel = 0
        self.audio_rtp_channel = None
        self.session = "12345678"
        self.video_setup = False
        self.talk_rtp_channel = None
        self.talk_active = False
        self.auth_nonce_count = 0
        try:
            while True:
                # Consume RTCP interleaved packets as well as RTSP requests.
                first = self.rfile.read(1)
                if not first:
                    break
                if first == b"$":
                    packet = self.rfile.read(3)
                    if len(packet) < 3:
                        break
                    payload = self.rfile.read(int.from_bytes(packet[1:], "big"))
                    if self.talk_rtp_channel is not None and packet[0] == self.talk_rtp_channel:
                        talkback_log({"type": "rtp", "active": self.talk_active, "data": base64.b64encode(payload).decode()})
                    elif self.talk_rtp_channel is not None and packet[0] == self.talk_rtp_channel + 1:
                        talkback_log({"type": "rtcp", "data": base64.b64encode(payload).decode()})
                    continue
                line = first + self.rfile.readline()
                parts = line.decode("ascii").strip().split(" ")
                if len(parts) != 3:
                    break
                method, uri, _ = parts
                headers = {}
                while True:
                    line = self.rfile.readline().decode("ascii").strip()
                    if not line:
                        break
                    key, value = line.split(":", 1)
                    headers[key.lower()] = value.strip()
                if "content-length" in headers:
                    self.rfile.read(int(headers["content-length"]))
                extra = ""
                body = ""
                code = "200 OK"
                channel_match = re.search(r"/ip(\d+)/", uri)
                channel_id = str(int(channel_match[1])) if channel_match else "0"
                mode = talkback_modes.get(channel_id, "normal")
                if method == "DESCRIBE":
                    auth = {key: quoted or plain for key, quoted, plain in
                            re.findall(r'(\w+)=(?:"([^"]*)"|([^,\s]+))', headers.get("authorization", ""))}
                    md5 = lambda x: hashlib.md5(x.encode()).hexdigest()
                    fixture_user = os.environ.get("RTSP_FIXTURE_USER", "viewer")
                    fixture_password = os.environ.get("RTSP_FIXTURE_PASSWORD", "local-only-test")
                    expected = md5(md5(f"{fixture_user}:Fixture:{fixture_password}") + ":12345678:" + md5("DESCRIBE:" + uri))
                    if os.environ.get("RTSP_FIXTURE_AUTH_QOP"):
                        expected = md5(md5(f"{fixture_user}:Fixture:{fixture_password}") + ":12345678:" +
                                       auth.get("nc", "") + ":" + auth.get("cnonce", "") + ":auth:" + md5(method + ":" + uri))
                    if auth.get("username") != fixture_user or auth.get("response") != expected:
                        code = "401 Unauthorized"
                        extra = 'WWW-Authenticate: Digest realm="Fixture", nonce="12345678"\r\n'
                    elif os.environ.get("RTSP_FIXTURE_SLEEP_FILE") and os.path.exists(os.environ["RTSP_FIXTURE_SLEEP_FILE"]):
                        code = "404 Stream Not Found"
                    else:
                        body = ("v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=Local test pattern\r\n"
                                "c=IN IP4 127.0.0.1\r\nt=0 0\r\na=control:*\r\n"
                                "m=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\n"
                                f"a=fmtp:96 packetization-mode=1;profile-level-id={sps[1:4].hex()};"
                                f"sprop-parameter-sets={base64.b64encode(sps).decode()},{base64.b64encode(pps).decode()}\r\n"
                                "a=control:trackID=0\r\n")
                        channel = re.search(r"/ip(\d+)/", uri)
                        if hevc:
                            body = ("v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=Fixture\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\na=control:*\r\n"
                                    "m=video 0 RTP/AVP 96\r\na=rtpmap:96 H265/90000\r\n"
                                    f"a=fmtp:96 sprop-vps={base64.b64encode(vps).decode()};sprop-sps={base64.b64encode(sps).decode()};sprop-pps={base64.b64encode(pps).decode()}\r\n"
                                    "a=control:trackID=0\r\n")
                        if "all" in audio_channels or channel and str(int(channel[1])) in audio_channels:
                            body += ("m=audio 0 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000/1\r\n"
                                     "a=control:trackID=1\r\n")
                        if ("all" in talkback_channels or channel_id in talkback_channels) and mode != "ignored" and \
                                headers.get("require") == "www.onvif.org/ver20/backchannel":
                            codec = "PCMA" if channel_id == "3" else "PCMU"
                            payload = 98 if channel_id == "3" else 0
                            mapping = f"{codec}/8000/1" if mode != "unsupported" else "MPEG4-GENERIC/16000/1"
                            control = "rtsp://other.example:554/speaker" if mode == "foreign" else "trackID=2"
                            body += (f"m=audio 0 RTP/AVP {payload}\r\na=rtpmap:{payload} {mapping}\r\n"
                                     f"a=control:{control}\r\na=sendonly\r\n")
                        extra = "Content-Type: application/sdp\r\nContent-Base: " + uri.rstrip("/") + "/\r\n"
                elif method == "SETUP":
                    transport = headers.get("transport", "")
                    match = re.search(r"interleaved=(\d+)-(\d+)", transport)
                    if not match:
                        code = "461 Unsupported Transport"
                    else:
                        if uri.endswith("trackID=2"):
                            self.talk_rtp_channel = int(match[1])
                            talkback_log({"type": "setup", "channel": channel_id})
                        elif uri.endswith("trackID=1"):
                            self.audio_rtp_channel = int(match[1])
                        else:
                            self.rtp_channel = int(match[1])
                            self.video_setup = True
                        extra = f"Transport: RTP/AVP/TCP;unicast;interleaved={match[1]}-{match[2]}\r\nSession: {self.session}\r\n"
                        if mode == "udp":
                            extra = f"Transport: RTP/AVP;unicast;server_port=6000-6001\r\nSession: {self.session}\r\n"
                elif method in ("PLAY", "GET_PARAMETER"):
                    extra = f"Session: {self.session}\r\n"
                else:
                    extra = "Public: OPTIONS, DESCRIBE, SETUP, PLAY, GET_PARAMETER, TEARDOWN\r\n"
                if os.environ.get("RTSP_FIXTURE_AUTH_QOP"):
                    auth = {key: quoted or plain for key, quoted, plain in
                            re.findall(r'(\w+)=(?:"([^"]*)"|([^,\s]+))', headers.get("authorization", ""))}
                    md5 = lambda value: hashlib.md5(value.encode()).hexdigest()
                    user = os.environ.get("RTSP_FIXTURE_USER", "viewer")
                    password = os.environ.get("RTSP_FIXTURE_PASSWORD", "local-only-test")
                    nc = int(auth.get("nc", "0"), 16)
                    expected = md5(md5(f"{user}:Fixture:{password}") + ":12345678:" + auth.get("nc", "") + ":" +
                                   auth.get("cnonce", "") + ":auth:" + md5(method + ":" + uri))
                    if auth.get("username") != user or auth.get("response") != expected or nc <= self.auth_nonce_count:
                        code = "401 Unauthorized"
                        extra = 'WWW-Authenticate: Digest realm="Fixture", nonce="12345678", qop="auth"\r\n'
                        body = ""
                    else:
                        self.auth_nonce_count = nc
                if self.talk_rtp_channel is not None and method in ("PLAY", "TEARDOWN", "GET_PARAMETER"):
                    talkback_log({"type": method.lower(), "status": code.split()[0]})
                reply = f"RTSP/1.0 {code}\r\nCSeq: {headers.get('cseq', '1')}\r\n{extra}Content-Length: {len(body)}\r\n\r\n{body}"
                with self.lock:
                    self.connection.sendall(reply.encode("ascii"))
                if method == "PLAY" and code == "200 OK":
                    session_log({"type": "play", "connection": connection_id, "uri": uri})
                    self.talk_active = self.talk_rtp_channel is not None
                    if self.video_setup:
                        threading.Thread(target=self.send_video, daemon=True).start()
                if method == "TEARDOWN" and code == "200 OK":
                    break
        except (ConnectionError, OSError):
            pass
        finally:
            session_log({"type": "closed", "connection": connection_id})
            self.stopped.set()

    def send_video(self):
        sequence = 0
        timestamp = 0
        index = 0
        audio_sequence = 0
        audio_timestamp = 0
        selected_frames = frames
        try:
            if moving_scene:
                # Every RTSP session observes the same generated scene. Start at the
                # next GOP so independently started analysis and viewers stay decodable.
                timing = scene_time()
                while timing is None and not self.stopped.wait(0.1):
                    timing = scene_time()
                if timing is None:
                    return
                index = (int((timing[0] - timing[1]) * 10 / pacing_rate) // 10 + 1) * 10
            while not self.stopped.is_set():
                if moving_scene:
                    timing = scene_time()
                    if timing is None:
                        self.stopped.wait(0.1)
                        continue
                    self.stopped.wait(max(0, timing[1] + index / 10 * pacing_rate - timing[0]))
                sleep_file = os.environ.get("RTSP_FIXTURE_SLEEP_FILE")
                if sleep_file and os.path.exists(sleep_file):
                    self.connection.shutdown(socket.SHUT_RDWR)
                    break
                pause_file = os.environ.get("RTSP_FIXTURE_PAUSE_FILE")
                if pause_file and os.path.exists(pause_file):
                    self.stopped.wait(0.1)
                    continue
                # Change scene only at a keyframe boundary, preserving a valid H.264 stream.
                if index % 10 == 0:
                    still_file = os.environ.get("RTSP_FIXTURE_STILL_FILE")
                    selected_frames = still_frames if still_file and os.path.exists(still_file) else frames
                frame = selected_frames[index % len(selected_frames)]
                for n, nal in enumerate(frame):
                    if len(nal) <= 1200:
                        payloads = [nal]
                    else:
                        data = nal[1:]
                        chunks = [data[i:i+1198] for i in range(0, len(data), 1198)]
                        payloads = [bytes([(nal[0] & 0xE0) | 28, (nal[0] & 31) | (0x80 if j == 0 else 0) | (0x40 if j == len(chunks)-1 else 0)]) + chunk for j, chunk in enumerate(chunks)]
                        if hevc:
                            chunks = [nal[i:i+1198] for i in range(2, len(nal), 1198)]
                            payloads = [bytes([(nal[0] & 0x81) | (49 << 1), nal[1], nal_type(nal) | (0x80 if j == 0 else 0) | (0x40 if j == len(chunks)-1 else 0)]) + chunk for j, chunk in enumerate(chunks)]
                    for p, payload in enumerate(payloads):
                        marker = 0x80 if n == len(frame)-1 and p == len(payloads)-1 else 0
                        rtp = struct.pack("!BBHII", 0x80, 96 | marker, sequence & 0xFFFF, timestamp & 0xFFFFFFFF, 0x12345678) + payload
                        with self.lock:
                            self.connection.sendall(b"$" + bytes([self.rtp_channel]) + len(rtp).to_bytes(2, "big") + rtp)
                        sequence += 1
                if self.audio_rtp_channel is not None:
                    # Five 20 ms G.711 packets per 100 ms video frame; both clocks start at zero.
                    for _ in range(5):
                        offset = audio_timestamp % len(audio_samples)
                        payload = audio_samples[offset:offset + 160]
                        rtp = struct.pack("!BBHII", 0x80, 0, audio_sequence & 0xFFFF,
                                          audio_timestamp & 0xFFFFFFFF, 0x23456789) + payload
                        with self.lock:
                            self.connection.sendall(b"$" + bytes([self.audio_rtp_channel]) +
                                                    len(rtp).to_bytes(2, "big") + rtp)
                        audio_sequence += 1
                        audio_timestamp += 160
                timestamp += 9000
                index += 1
                if not moving_scene:
                    self.stopped.wait(0.1)
        except (ConnectionError, OSError):
            self.stopped.set()


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


port = int(os.environ.get("RTSP_FIXTURE_PORT", "18554"))
with Server(("127.0.0.1", port), Handler) as server:
    print(f"Test-only RTSP server listening on 127.0.0.1:{port}", flush=True)
    server.serve_forever()
