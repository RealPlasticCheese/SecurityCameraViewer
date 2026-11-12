"""Cache official ONVIF/W3C/OASIS schemas for the optional offline C# schema checks.

Run: python tests/fetch_onvif_schemas.py
The cache is under ignored artifacts/. No downloaded code is executed.
"""
import hashlib
import json
from pathlib import Path
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1] / "artifacts" / "onvif-schemas"
BASE = "https://raw.githubusercontent.com/onvif/specs/development/wsdl/"
SOURCES = ["ver10/device/wsdl/devicemgmt.wsdl", "ver10/media/wsdl/media.wsdl",
           "ver20/media/wsdl/media.wsdl", "ver10/events/wsdl/event.wsdl", "ver10/deviceio.wsdl"]
ALLOWED = {"raw.githubusercontent.com", "www.w3.org", "docs.oasis-open.org", "schemas.xmlsoap.org"}
XS = "{http://www.w3.org/2001/XMLSchema}"
ROOT.mkdir(parents=True, exist_ok=True)
manifest = {}


def fetch(url):
    if url in manifest:
        return
    if len(manifest) >= 64 or urllib.parse.urlparse(url).hostname not in ALLOWED:
        raise RuntimeError("Unexpected schema dependency: " + url)
    fetch_url = "https:" + url[5:] if url.startswith("http:") else url
    path = ROOT / (hashlib.sha256(url.encode()).hexdigest() + ".xml")
    request = urllib.request.Request(fetch_url, headers={"User-Agent": "SecurityCameraViewer-schema-tests"})
    with urllib.request.urlopen(request, timeout=30) as response:
        data = response.read(4 * 1024 * 1024)
    if len(data) >= 4 * 1024 * 1024:
        raise RuntimeError("Oversized schema: " + url)
    tree = ET.fromstring(data)
    manifest[url] = path.name
    path.write_bytes(data)
    print("Cached", url, flush=True)
    for element in tree.iter():
        if element.tag in (XS + "import", XS + "include") and element.get("schemaLocation"):
            fetch(urllib.parse.urljoin(url, element.get("schemaLocation")))


for source in SOURCES:
    fetch(BASE + source)
(ROOT / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
print("Saved", len(manifest), "schema resources to", ROOT)
