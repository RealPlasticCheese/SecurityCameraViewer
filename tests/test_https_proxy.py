"""Verify local HTTPS proxy handling and published install assets without a camera.

Run from the repo root after publishing:
python tests/test_https_proxy.py artifacts/menu-install-web/SecurityCameraViewer.Web.dll
"""
from contextlib import closing
import http.client
import json
from web_settings import document
from pathlib import Path
import socket
import struct
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parent.parent


def check(condition, label):
    if not condition:
        raise AssertionError(label)
    print("PASS:", label, flush=True)


def run(server_assembly):
    artifacts = ROOT / "artifacts"
    artifacts.mkdir(exist_ok=True)
    directory = Path(tempfile.mkdtemp(prefix="https-proxy-", dir=artifacts))
    settings = directory / "settings.json"
    settings.write_text(json.dumps(document({"AutoConnect": False})), encoding="utf-8")
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]

    def request(path, *, method="GET", headers=None, body=None, source="127.0.0.1"):
        with closing(http.client.HTTPConnection("127.0.0.1", port, timeout=5, source_address=(source, 0))) as connection:
            connection.request(method, path, body=body, headers=headers or {})
            response = connection.getresponse()
            return response.status, dict(response.getheaders()), response.read()

    proxy_headers = {
        "Host": "viewer.example",
        "Origin": "https://viewer.example",
        "X-Forwarded-Proto": "https",
        "X-Forwarded-For": "198.51.100.10",
    }
    with (directory / "server.log").open("w", encoding="utf-8") as log:
        server = subprocess.Popen([
            "dotnet", str(server_assembly), "--urls", f"http://127.0.0.1:{port}",
            "--settings", str(settings),
        ], cwd=ROOT, stdout=log, stderr=subprocess.STDOUT)
        try:
            deadline = time.monotonic() + 30
            while True:
                try:
                    if request("/api/state")[0] == 200:
                        break
                except OSError:
                    pass
                if server.poll() is not None or time.monotonic() > deadline:
                    raise AssertionError(f"Server did not start; see {directory / 'server.log'}")
                time.sleep(0.2)

            check(request("/api/disconnect", method="POST", headers=proxy_headers)[0] == 200,
                  "Local HTTPS proxy preserves same-origin camera controls")
            direct_headers = {"Host": "viewer.example", "Origin": "http://viewer.example"}
            check(request("/api/state", headers=direct_headers)[0] == 200,
                  "Direct HTTP remains supported")
            check(request("/api/state", headers={"Host": "viewer.example", "Origin": "https://viewer.example"})[0] == 403,
                  "HTTPS origin without a trusted proxy is rejected")
            check(request("/api/state", headers=proxy_headers, source="127.0.0.2")[0] == 403,
                  "Forwarding headers from an untrusted address cannot spoof HTTPS")
            foreign_headers = {**proxy_headers, "Origin": "https://foreign.example", "X-Forwarded-Host": "foreign.example"}
            check(request("/api/state", headers=foreign_headers)[0] == 403,
                  "Forwarded host cannot bypass the cross-origin guard")

            with closing(http.client.HTTPConnection("127.0.0.1", port, timeout=5)) as connection:
                connection.request("GET", "/api/video", headers={
                    **proxy_headers, "Connection": "Upgrade", "Upgrade": "websocket",
                    "Sec-WebSocket-Version": "13", "Sec-WebSocket-Key": "dGhlIHNhbXBsZSBub25jZQ==",
                })
                check(connection.getresponse().status == 101,
                      "HTTPS-origin video WebSocket upgrades through the local proxy")

            status, headers, body = request("/api/integrations/setup", method="POST", headers=proxy_headers)
            check(status == 200 and "secure" in headers.get("Set-Cookie", "").lower(),
                  "HTTPS proxy sign-in issues a Secure session cookie")
            token = json.loads(body)["token"]
            login_headers = {**proxy_headers, "Content-Type": "application/json"}
            status, headers, _ = request("/api/auth/session", method="POST", headers=login_headers,
                                         body=json.dumps({"token": token}))
            check(status == 200 and "secure" in headers.get("Set-Cookie", "").lower(),
                  "Subsequent browser sign-in also retains the HTTPS scheme")

            status, headers, body = request("/manifest.webmanifest")
            check(status == 200 and "application/manifest+json" in headers.get("Content-Type", ""),
                  "Published manifest has the correct MIME type and is available before sign-in")
            manifest = json.loads(body)
            check(manifest["display"] == "standalone" and manifest["start_url"] == "/" and manifest["scope"] == "/",
                  "Installed app opens the viewer in its own window")
            for icon in manifest["icons"]:
                status, headers, data = request(icon["src"])
                dimensions = struct.unpack(">II", data[16:24])
                size = int(icon["sizes"].split("x")[0])
                check(status == 200 and headers.get("Content-Type") == "image/png"
                      and data[:8] == b"\x89PNG\r\n\x1a\n" and dimensions == (size, size),
                      f"Published {size}px app icon is a valid PNG and available before sign-in")
            check(request("/app-install.js")[0] == 200,
                  "Install prompt script is included in the published web app")
        finally:
            server.terminate()
            try:
                server.wait(timeout=10)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait(timeout=10)


if __name__ == "__main__":
    assembly = Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/menu-install-web/SecurityCameraViewer.Web.dll").resolve()
    run(assembly)
