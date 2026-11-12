"""Loopback-only ONVIF Media/Media2, mixed RTSP, catalog and diagnostics regression tests.

Run from the repo root after publishing the web app:
python tests/test_camera_setup.py artifacts/onvif-web/SecurityCameraViewer.Web.dll
"""
import base64
import datetime
import hashlib
import http.server
import json
from web_settings import document
import os
from pathlib import Path
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
SOAP = "http://www.w3.org/2003/05/soap-envelope"
DEVICE = "http://www.onvif.org/ver10/device/wsdl"
MEDIA = "http://www.onvif.org/ver10/media/wsdl"
MEDIA2 = "http://www.onvif.org/ver20/media/wsdl"
SCHEMA = "http://www.onvif.org/ver10/schema"
SECURITY = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"
UTILITY = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd"
fixture_errors = []
operations = []


def check(condition, label):
    if not condition:
        raise AssertionError(label)
    print("PASS:", label, flush=True)


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def wait_for(predicate, label, timeout=30):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        try:
            if predicate():
                return
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(0.2)
    raise AssertionError("Timed out: " + label)


def stop(process):
    if process.poll() is not None:
        return
    if os.name == "nt":
        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True, timeout=10)
    else:
        process.terminate()
    if process.poll() is None:
        process.kill()
    process.wait(timeout=15)


class OnvifFixture(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        try:
            body = ET.fromstring(self.rfile.read(int(self.headers["Content-Length"])))
            operation = list(body.find(f"{{{SOAP}}}Body"))[0]
            name = operation.tag.split("}")[1]
            namespace = operation.tag[1:].split("}")[0]
            operations.append((self.path, name, namespace))
            if name != "GetSystemDateAndTime":
                security = body.find(f".//{{{SECURITY}}}UsernameToken")
                assert security is not None, "Missing WS-Security username token"
                nonce = base64.b64decode(security.findtext(f"{{{SECURITY}}}Nonce"))
                created = security.findtext(f"{{{UTILITY}}}Created")
                expected = base64.b64encode(hashlib.sha1(nonce + (created + "onvif-secret").encode()).digest()).decode()
                assert security.findtext(f"{{{SECURITY}}}Password") == expected, "Incorrect WS-Security digest"
                assert security.findtext(f"{{{SECURITY}}}Username") == "onvif-user"
            address = f"http://127.0.0.1:{self.server.server_port}"
            if self.path == "/unauthorized":
                self.send_response(401)
                self.end_headers()
                return
            if self.path == "/xxe":
                response = '<!DOCTYPE x [<!ENTITY xxe SYSTEM "file:///missing">]><x>&xxe;</x>'
            elif name == "GetSystemDateAndTime":
                now = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(minutes=10)
                value = f'<tt:UTCDateTime><tt:Time><tt:Hour>{now.hour}</tt:Hour><tt:Minute>{now.minute}</tt:Minute><tt:Second>{now.second}</tt:Second></tt:Time><tt:Date><tt:Year>{now.year}</tt:Year><tt:Month>{now.month}</tt:Month><tt:Day>{now.day}</tt:Day></tt:Date></tt:UTCDateTime>'
                response = self.envelope(f'<tds:GetSystemDateAndTimeResponse>{value}</tds:GetSystemDateAndTimeResponse>')
            elif name == "GetDeviceInformation":
                response = self.envelope('<tds:GetDeviceInformationResponse><tds:Manufacturer>Fixture Labs</tds:Manufacturer><tds:Model>Two-channel recorder</tds:Model><tds:FirmwareVersion>1.2.3</tds:FirmwareVersion><tds:SerialNumber>TEST-ONLY</tds:SerialNumber></tds:GetDeviceInformationResponse>')
            elif name == "GetServices":
                if self.path == "/legacy":
                    response = self.envelope('<s:Fault><s:Reason><s:Text>Not supported</s:Text></s:Reason></s:Fault>')
                else:
                    target = "http://localhost:1/forbidden" if self.path == "/foreign" else address + "/media2"
                    response = self.envelope(f'<tds:GetServicesResponse><tds:Service><tds:Namespace>{MEDIA2}</tds:Namespace><tds:XAddr>{target}</tds:XAddr></tds:Service><tds:Service><tds:Namespace>{MEDIA}</tds:Namespace><tds:XAddr>{address}/media</tds:XAddr></tds:Service></tds:GetServicesResponse>')
            elif name == "GetCapabilities":
                response = self.envelope(f'<tds:GetCapabilitiesResponse><tt:Capabilities><tt:Media><tt:XAddr>{address}/media</tt:XAddr></tt:Media></tt:Capabilities></tds:GetCapabilitiesResponse>')
            elif name == "GetProfiles":
                profiles = ""
                for token, source, width in [("main-a", "camera-a", 1920), ("sub-a", "camera-a", 640), ("main-b", "camera-b", 1280)]:
                    profiles += f'<m:Profiles token="{token}"><tt:Name>{token}</tt:Name><tt:VideoSourceConfiguration><tt:SourceToken>{source}</tt:SourceToken></tt:VideoSourceConfiguration><tt:VideoEncoderConfiguration><tt:Encoding>H264</tt:Encoding><tt:Resolution><tt:Width>{width}</tt:Width><tt:Height>720</tt:Height></tt:Resolution><tt:RateControl><tt:FrameRateLimit>15</tt:FrameRateLimit><tt:BitrateLimit>2048</tt:BitrateLimit></tt:RateControl></tt:VideoEncoderConfiguration></m:Profiles>'
                response = self.envelope(f'<m:GetProfilesResponse xmlns:m="{namespace}">{profiles}</m:GetProfilesResponse>')
            elif name == "GetStreamUri":
                if namespace == MEDIA2:
                    assert operation.findtext(f"{{{MEDIA2}}}Protocol") == "RTSP"
                else:
                    assert operation.findtext(f".//{{{SCHEMA}}}Protocol") == "RTSP"
                token = operation.findtext(f"{{{namespace}}}ProfileToken")
                response = self.envelope(f'<m:GetStreamUriResponse xmlns:m="{namespace}"><tt:MediaUri><tt:Uri>rtsp://embedded:do-not-export@127.0.0.1:{self.server.rtsp_port}/{token}?vendorSecret=opaque-test-token</tt:Uri></tt:MediaUri></m:GetStreamUriResponse>')
            else:
                raise AssertionError("Unexpected mutating or unsupported operation " + name)
            data = response.encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/soap+xml")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        except Exception as error:
            fixture_errors.append(str(error))
            self.send_error(500)

    @staticmethod
    def envelope(value):
        return f'<s:Envelope xmlns:s="{SOAP}" xmlns:tds="{DEVICE}" xmlns:tt="{SCHEMA}"><s:Body>{value}</s:Body></s:Envelope>'


def main():
    assembly = Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/onvif-web/SecurityCameraViewer.Web.dll").resolve()
    # Retain logs on failure, just like the other end-to-end suites.
    work = ROOT / "artifacts" / ("camera-setup-" + uuid.uuid4().hex)
    work.mkdir(parents=True)
    try:
        processes = []
        logs = []
        rtsp_ports = [free_port(), free_port()]
        port = free_port()
        onvif = http.server.ThreadingHTTPServer(("127.0.0.1", 0), OnvifFixture)
        onvif.rtsp_port = rtsp_ports[0]
        threading.Thread(target=onvif.serve_forever, daemon=True).start()
        base = f"http://127.0.0.1:{port}"

        def start(command, environment=None):
            log = open(work / f"process-{len(logs)}.log", "wb")
            logs.append(log)
            process = subprocess.Popen(command, cwd=ROOT, env={**os.environ, **(environment or {})}, stdout=log, stderr=log, creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            processes.append(process)
            return process

        def api(path, body=None, method=None, expected=200):
            request = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(), headers={"Content-Type": "application/json"}, method=method or ("GET" if body is None else "POST"))
            try:
                response = urllib.request.urlopen(request, timeout=65)
            except urllib.error.HTTPError as error:
                response = error
            raw = response.read()
            assert response.status == expected, f"{path}: expected {expected}, got {response.status}: {raw[:300]!r}"
            return json.loads(raw) if raw else None

        try:
            for i, rtsp_port in enumerate(rtsp_ports):
                fixture = start([sys.executable, "tests/rtsp_fixture.py"], {"RTSP_FIXTURE_PORT": str(rtsp_port), "RTSP_FIXTURE_USER": f"viewer-{i}", "RTSP_FIXTURE_PASSWORD": f"camera-secret-{i}"})

                def fixture_ready():
                    assert fixture.poll() is None, f"RTSP fixture exited with code {fixture.returncode}"
                    with socket.create_connection(("127.0.0.1", rtsp_port), timeout=1):
                        return True

                wait_for(fixture_ready, f"RTSP fixture {i} startup")
            (work / "settings.json").write_text(json.dumps({"Host": "127.0.0.1", "RtspPort": rtsp_ports[0], "Username": "viewer-0", "AutoConnect": False}))
            (work / "settings.json").write_text(json.dumps(document({"AutoConnect": False}, "camera-secret-0")))
            # This API-only fixture has no browser subscriber to request preview decoding.
            server = start(["dotnet", str(assembly), "--urls", base, "--settings", str(work / "settings.json"),
                            "--preview-on-demand", "false"])
            wait_for(lambda: api("/api/state"), "web startup")
            catalog = api("/api/setup/configurations")
            settings = catalog["configurations"][0]
            check(len(settings["sources"]) == 4 and settings["sources"][0]["useDefaultPassword"], "Legacy recorder migrates to stable camera sources without discarding credentials")
            sources = [{"id": uuid.uuid4().hex, "channel": i + 1, "name": f"Independent {i}", "mainStream": f"rtsp://127.0.0.1:{rtsp_port}/ip01/0?token=opaque-test-token", "username": f"viewer-{i}"} for i, rtsp_port in enumerate(rtsp_ports)]
            credentials = {source["id"]: {"password": f"camera-secret-{i}"} for i, source in enumerate(sources)}
            settings.update(configurationName="Mixed cameras", sources=sources)
            api("/api/setup/configurations", {"settings": settings, "cameraCredentials": credentials})
            wait_for(lambda: len(api("/api/state")["cameras"]) == 2 and all(c["live"] for c in api("/api/state")["cameras"]), "independent streams")
            check(True, "Two independent hosts/ports and credentials decode simultaneously")
            result = api("/api/setup/test", {"channel": 2, "substream": True})
            check(result["videoDecoded"] and result["media"]["width"] == 640 and result["media"]["height"] == 360, "Single-stream fallback decodes video and reports actual source dimensions")
            report = api("/api/setup/metadata")
            encoded = json.dumps(report)
            check("camera-secret" not in encoded and "opaque-test-token" not in encoded and "REDACTED" in encoded and "tests" in report, "Metadata contains timestamped tests and redacts credentials and vendor query tokens")
            saved = (work / "settings.json").read_text()
            check("camera-secret" in saved, "Named collections and passwords persist in one private document")
            settings["sources"] = list(reversed(sources))
            api("/api/setup/configurations", {"settings": settings})
            recording = api("/api/recording")
            identity = {c["channel"]: c["cameraId"] for c in recording["settings"]["cameras"]}
            check(identity[1] == "camera-" + sources[0]["id"], "Reordering preserves camera identities for recording and integrations")
            configured_sources = [dict(source, motionActivated=source["channel"] == 2) for source in settings["sources"]]
            settings["sources"] = configured_sources
            api("/api/setup/configurations", {"settings": settings})
            rules = api("/api/recording")["settings"]
            action_id = uuid.uuid4().hex
            rules["actions"].append({"id": action_id, "name": "Entrance alert", "type": "webhook", "enabled": False,
                                     "url": "http://127.0.0.1:1/unused", "cooldownSeconds": 45})
            for camera in rules["cameras"]:
                camera.update(sensitivity=83, minimumAreaPercent=2.5, analysisWidth=640, analysisFramesPerSecond=3,
                              aiConfidence=0.6, aiClasses=["person"], aiIgnoreClasses=["car"], defaultActionIds=["record", action_id],
                              recordWhenStreaming=camera["channel"] == 2,
                              streamSchedule=[{"start": "20:00", "end": "06:00", "days": [1, 3, 5]}],
                              zones=[{"id": uuid.uuid4().hex, "name": "Entrance", "x": 0.1, "y": 0.2, "width": 0.4, "height": 0.3,
                                      "times": [{"start": "08:00", "end": "17:00", "days": [1, 2, 3, 4, 5]}], "actionIds": [action_id]}])
            api("/api/recording", rules)
            saved_settings = api("/api/recording")["settings"]
            saved_rules = {camera["channel"]: camera for camera in saved_settings["cameras"]}

            def check_rules(label):
                current = api("/api/recording")["settings"]
                check({camera["channel"]: camera for camera in current["cameras"]} == saved_rules and current["actions"] == saved_settings["actions"], label)

            settings["sources"] = [dict(source, enabled=source["channel"] != 1) for source in configured_sources]
            api("/api/setup/configurations", {"settings": settings})
            check_rules("Disabling a camera preserves its motion, AI, zones, schedules and action selections")
            check([camera["channel"] for camera in api("/api/state")["cameras"]] == [2], "Disabled cameras stop live viewing")
            api("/api/cameras/1/snapshot", expected=404)
            check(True, "A disabled camera snapshot cannot return another camera's picture")
            api("/api/recording", api("/api/recording")["settings"])
            check_rules("Saving recording settings while a camera is disabled preserves its rules")
            stop(server)
            server = start(["dotnet", str(assembly), "--urls", base, "--settings", str(work / "settings.json"),
                            "--preview-on-demand", "false"])
            wait_for(lambda: api("/api/state"), "restart with disabled camera")
            check_rules("Disabled camera recording rules survive a server restart")
            settings["sources"] = [dict(source, enabled=False) for source in configured_sources]
            api("/api/setup/configurations", {"settings": settings})
            check_rules("Disabling every camera preserves all recording rules")
            paused_rules = api("/api/recording")["settings"]
            paused_rules["enabled"] = True
            api("/api/recording", paused_rules)
            check(not api("/api/recording")["cameras"] and not api("/api/state")["cameras"],
                  "Disabled cameras do not start capture even when recording is enabled")
            paused_rules["enabled"] = False
            api("/api/recording", paused_rules)
            settings["sources"] = configured_sources
            api("/api/setup/configurations", {"settings": settings})
            check_rules("Re-enabling cameras restores their saved recording rules")
            wait_for(lambda: len(api("/api/state")["cameras"]) == 2 and all(c["live"] for c in api("/api/state")["cameras"]), "re-enabled cameras")
            settings["sources"] = [source for source in configured_sources if source["channel"] != 1]
            api("/api/setup/configurations", {"settings": settings})
            check(api("/api/recording")["settings"]["cameras"] == [saved_rules[2]], "Removing a camera still removes only its recording rules")
            settings["sources"] = list(reversed(sources))
            api("/api/setup/configurations", {"settings": settings})
            old_id = settings["configurationId"]
            second = dict(settings, configurationId=uuid.uuid4().hex, configurationName="Workshop", sources=[dict(sources[0], id=uuid.uuid4().hex, channel=7, name="Workshop")])
            api("/api/setup/configurations", {"settings": second, "cameraCredentials": {second["sources"][0]["id"]: {"password": "incorrect"}}})
            check(not api("/api/recording")["settings"]["enabled"] and api("/api/recording")["settings"]["configurationId"] == second["configurationId"], "New collections have separate disabled recording settings")
            api(f"/api/setup/configurations/{old_id}/activate", method="POST")
            wait_for(lambda: all(c["live"] for c in api("/api/state")["cameras"]), "switch back")
            check(True, "Switching collections restores credentials held in memory")
            for path in ["/device", "/legacy", "/foreign", "/xxe", "/unauthorized"]:
                response = api("/api/setup/onvif", {"address": f"http://127.0.0.1:{onvif.server_port}{path}", "username": "onvif-user", "password": "onvif-secret"})
                if path == "/device":
                    check(response["manufacturer"] == "Fixture Labs" and len(response["profiles"]) == 6, "ONVIF returns device identity and Media plus Media2 profiles")
                    check(response["profiles"][0]["videoSourceToken"] == "camera-a" and response["profiles"][2]["videoSourceToken"] == "camera-b", "ONVIF preserves source tokens to distinguish quality variants from separate cameras")
                    check(590 < response["clockOffsetSeconds"] < 610, "Device clock offset is measured and used for WS-Security authentication")
                    exported = api("/api/setup/export", response)
                    check("do-not-export" not in json.dumps(exported) and "opaque-test-token" not in json.dumps(exported), "ONVIF inspection export strips embedded credentials and vendor tokens")
                elif path == "/legacy":
                    check(len(response["profiles"]) == 3, "Legacy GetCapabilities fallback supports devices without GetServices")
                elif path == "/foreign":
                    check(any("different host" in w for w in response["warnings"]), "Advertised foreign service endpoints cannot receive ONVIF credentials")
                else:
                    check(not response["profiles"] and response["warnings"], "ONVIF " + path + " returns bounded diagnostic errors without raw XML")
            api("/api/setup/configurations", {"settings": dict(settings, sources=[dict(sources[0], mainStream="file:///etc/passwd")])}, expected=400)
            check(True, "Non-RTSP source schemes are rejected before playback")
            # One rejected login must not stop the other source.
            api("/api/setup/configurations", {"settings": settings, "cameraCredentials": {sources[0]["id"]: {"password": "incorrect"}}})
            wait_for(lambda: any(c["channel"] == 2 and c["live"] for c in api("/api/state")["cameras"]), "healthy source after bad login")
            check(True, "Authentication failure is isolated to the affected camera")
            api("/api/setup/configurations", {"settings": settings, "cameraCredentials": credentials})
            stop(server)
            server = start(["dotnet", str(assembly), "--urls", base, "--settings", str(work / "settings.json"),
                            "--preview-on-demand", "false"])
            wait_for(lambda: api("/api/state"), "restart with named catalog")
            api("/api/connect", method="POST")
            wait_for(lambda: len(api("/api/state")["cameras"]) == 2 and all(c["live"] for c in api("/api/state")["cameras"]), "restart source credentials")
            check(api("/api/state")["settings"]["configurationName"] == "Mixed cameras", "Named collection and saved credentials restore playback after restart")
            check(not fixture_errors, "ONVIF fixture verified read-only operations and WS-Security digests: " + "; ".join(fixture_errors))
            print("All camera setup checks passed.", flush=True)
        finally:
            onvif.shutdown()
            try:
                for process in reversed(processes):
                    stop(process)
            finally:
                for log in logs:
                    log.close()
    except Exception:
        print("Test logs:", work, flush=True)
        for log in work.glob("*.log"):
            print(log.name, log.read_text(errors="replace")[-4000:], flush=True)
        raise


if __name__ == "__main__":
    main()
