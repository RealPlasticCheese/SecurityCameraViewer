"""Opt-in real capture check with an isolated local RTSP fixture and settings.

Run after publishing and installing diagnostic tools:
python tests/test_profiling_capture.py --assembly artifacts/web/SecurityCameraViewer.Web.dll
"""
import argparse
import csv
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request


REPO = Path(__file__).resolve().parents[1]


def port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def stop(process):
    if process is None or process.poll() is not None:
        return
    if os.name == "nt":
        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], check=True, capture_output=True)
    else:
        os.killpg(process.pid, signal.SIGTERM)
    process.wait(timeout=15)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assembly", default="artifacts/web/SecurityCameraViewer.Web.dll")
    parser.add_argument("--bash")
    options = parser.parse_args()
    assembly = (REPO / options.assembly).resolve()
    if not assembly.is_file():
        parser.error("Publish the web server first, or specify --assembly")
    fixture_root = REPO / "artifacts/profiling-capture-tests"
    fixture_root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="capture-", dir=fixture_root) as temporary:
        root = Path(temporary)
        if not root.resolve().is_relative_to(fixture_root.resolve()):
            raise RuntimeError("Unexpected fixture location")
        rtsp_port, web_port = port(), port()
        settings = {
            "Version": 1,
            "ActiveConfigurationId": "default",
            "Integrations": {},
            "Configurations": [{
                "Connection": {"Host": "127.0.0.1", "RtspPort": rtsp_port, "Username": "viewer", "ChannelCount": 1, "AutoConnect": True},
                "Password": "local-only-test",
                "Recording": {"Directory": str(root / "recordings"), "Enabled": True,
                              "Cameras": [{"Channel": 1, "DetectionSubstream": False,
                                           "Adaptive": {"SmoothFrames": True, "RegionPersistenceFrames": 2}}]}
            }]
        }
        settings_path = root / "settings.json"
        settings_path.write_text(json.dumps(settings), encoding="utf-8")
        environment = os.environ.copy()
        environment["RTSP_FIXTURE_PORT"] = str(rtsp_port)
        popen_options = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {"start_new_session": True}
        fixture = server = None
        with (root / "fixture.log").open("w") as fixture_log, (root / "server.log").open("w") as server_log:
            try:
                fixture = subprocess.Popen([sys.executable, str(REPO / "tests/rtsp_fixture.py")], cwd=REPO, env=environment,
                                           stdout=fixture_log, stderr=subprocess.STDOUT, **popen_options)
                server = subprocess.Popen(["dotnet", str(assembly), "--settings", str(settings_path), "--urls", f"http://127.0.0.1:{web_port}"],
                                          cwd=REPO, stdout=server_log, stderr=subprocess.STDOUT, **popen_options)
                snapshot_url = f"http://127.0.0.1:{web_port}/api/cameras/1/snapshot"
                deadline = time.monotonic() + 40
                while True:
                    if server.poll() is not None or fixture.poll() is not None:
                        raise RuntimeError("Fixture or web server exited during startup:\n" +
                                           (root / "server.log").read_text(encoding="utf-8", errors="replace")[:2500] +
                                           (root / "fixture.log").read_text(encoding="utf-8", errors="replace")[:1500])
                    try:
                        with urllib.request.urlopen(snapshot_url, timeout=2) as response:
                            if response.read().startswith(b"\xff\xd8"):
                                break
                    except (urllib.error.URLError, TimeoutError):
                        pass
                    if time.monotonic() >= deadline:
                        raise RuntimeError("Timed out waiting for real decoded video")
                    time.sleep(0.2)
                shell = shutil.which("pwsh") or shutil.which("powershell")
                for mode in ("metrics", "trace", "allocations"):
                    if os.name == "nt" and not options.bash:
                        command = [shell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(REPO / "profile-web.ps1"),
                                   "-ProcessId", str(server.pid), "-Mode", mode, "-DurationSeconds", "5", "-OutputDirectory", str(root / "captures")]
                    else:
                        tool = REPO / "artifacts/profiling-tools" / ("dotnet-counters" if mode == "metrics" else "dotnet-trace")
                        if os.name == "nt":
                            tool = tool.with_suffix(".exe")
                        command = [options.bash or "bash", str(REPO / "profile-web.sh"), "--process-id", str(server.pid), "--mode", mode,
                                   "--duration-seconds", "5", "--output-directory", str(root / "captures"), "--tool-path", str(tool)]
                    result = subprocess.run(command, cwd=REPO, capture_output=True, text=True, timeout=40)
                    if result.returncode:
                        raise RuntimeError(result.stdout + result.stderr)
                    extension = "csv" if mode == "metrics" else "nettrace"
                    output = root / "captures" / f"{mode}.{extension}"
                    if output.stat().st_size == 0:
                        raise RuntimeError("Empty capture: " + mode)
                    with urllib.request.urlopen(snapshot_url, timeout=2) as response:
                        if not response.read().startswith(b"\xff\xd8"):
                            raise RuntimeError("Capture interrupted video delivery")
                    print("PASS: real " + mode + " capture completes while server keeps delivering video")
                metrics = (root / "captures/metrics.csv").read_text(encoding="utf-8-sig")
                for stage in ("jpeg.parse_chunk", "motion.total", "analysis.captured", "analysis.dequeued"):
                    if stage not in metrics:
                        raise RuntimeError("Missing captured stage: " + stage)
                with (root / "captures/metrics.csv").open(encoding="utf-8-sig", newline="") as capture:
                    if len(list(csv.reader(capture))) < 2:
                        raise RuntimeError("Metrics capture contains no measurements")
                print("PASS: capture includes real JPEG and adaptive-analysis metrics")
            finally:
                try:
                    stop(server)
                finally:
                    stop(fixture)


if __name__ == "__main__":
    main()
