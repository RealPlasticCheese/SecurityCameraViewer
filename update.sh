#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")"

bash web-service.sh stop
git pull
bash build-web.sh
bash web-service.sh start
