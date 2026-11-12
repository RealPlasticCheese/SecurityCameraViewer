#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")/.."
version=$1
commit=$2
publish="$PWD/artifacts/publish/linux-x64"
output="$PWD/artifacts/packages"
python3 -m unittest discover -s tests -p test_packaging.py -v
python3 -m unittest discover -s tests -p test_deployment.py -v
if command -v node >/dev/null; then
    node --test tests/web-*.test.cjs
else
    docker run --rm --user "$(id -u):$(id -g)" -v "$PWD:/source:ro" -w /source node:22-bookworm-slim node --test tests/web-*.test.cjs
fi
[[ ! -d $publish ]] || { echo 'Use a clean publish directory.' >&2; exit 1; }
mkdir -p "$publish" "$output"
dotnet publish src/SecurityCameraViewer.Web -c Release -r linux-x64 --self-contained true -o "$publish" \
    "-p:Version=$version" "-p:SourceRevisionId=$commit" "-p:RestoreConfigFile=$PWD/NuGet.Config"
python3 tests/test_host_configuration.py "$publish/SecurityCameraViewer.Web.dll"
for project in SecurityCameraViewer.Tests SecurityCameraViewer.Nvr.Tests SecurityCameraViewer.Recording.Tests SecurityCameraViewer.Web.Tests SecurityCameraViewer.Streaming.Tests; do
    arguments=()
    case "$project" in
        SecurityCameraViewer.Web.Tests) arguments=(-- --settings-only) ;;
        SecurityCameraViewer.Recording.Tests) arguments=(-- --unit-only) ;;
    esac
    dotnet run --project "tests/$project" -c Release "${arguments[@]}"
done
DISPLAY="${DISPLAY:-:scv-test}" dotnet run --project tests/SecurityCameraViewer.Web.Tests -c Release -- --browser-launch-only "$publish/SecurityCameraViewer.Web.dll"
bash install-mediamtx.sh 1.21.1 "$publish"
cp packaging/linux/setup.py web-https.sh "$publish/"
cp docs/installation.md "$publish/INSTALL.md"
cp docs/https-namecheap.md "$publish/"
cp LICENSE THIRD-PARTY-NOTICES.md "$publish/"
curl --fail --location --silent --show-error https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/LICENSE.TXT -o "$publish/DotNet-LICENSE.txt"
curl --fail --location --silent --show-error https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/THIRD-PARTY-NOTICES.TXT -o "$publish/DotNet-THIRD-PARTY-NOTICES.txt"
curl --fail --location --silent --show-error https://raw.githubusercontent.com/bluenviron/mediamtx/v1.21.1/LICENSE -o "$publish/MediaMTX-LICENSE.txt"
printf '{"version":"%s","commit":"%s","runtime":"linux-x64","mediamtx":"1.21.1"}\n' "$version" "$commit" > "$publish/release.json"
bash eng/package-linux.sh "$publish" "$version" "$output"
(cd "$output"; for file in *.deb *.rpm *.tar.gz; do sha256sum "$file" > "$file.sha256"; done)
