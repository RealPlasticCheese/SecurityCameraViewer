"""Install official YOLOX ONNX assets for local camera inference (Python standard library only)."""

import argparse
import os
from pathlib import Path
import tempfile
import urllib.request


MODELS = {
    "yolox-nano": "yolox_nano.onnx",
    "yolox-tiny": "yolox_tiny.onnx",
}
BASE_URL = "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/"


def install(directory: Path, model: str) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    name = MODELS[model]
    destination = directory / name
    if destination.exists():
        print(f"Already installed: {destination}")
        return
    temporary = None
    try:
        request = urllib.request.Request(BASE_URL + name, headers={"User-Agent": "SecurityCameraViewer-model-installer"})
        with urllib.request.urlopen(request, timeout=60) as response:
            if not response.url.startswith("https://"):
                raise ValueError("Model download must use HTTPS")
            with tempfile.NamedTemporaryFile(dir=directory, suffix=".download", delete=False) as output:
                temporary = Path(output.name)
                total = 0
                while chunk := response.read(1024 * 1024):
                    total += len(chunk)
                    if total > 128 * 1024 * 1024:
                        raise ValueError("Model download exceeds 128 MB")
                    output.write(chunk)
        if total < 1024:
            raise ValueError("Model download is unexpectedly small")
        os.replace(temporary, destination)
        temporary = None
        print(f"Installed {model}: {destination} ({total:,} bytes). License: Apache-2.0.")
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", required=True, type=Path, help="Server model folder")
    parser.add_argument("--model", choices=MODELS, default="yolox-nano")
    args = parser.parse_args()
    install(args.directory.resolve(), args.model)


if __name__ == "__main__":
    main()
