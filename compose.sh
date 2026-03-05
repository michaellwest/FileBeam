#!/usr/bin/env bash
# compose.sh — Docker Compose auto-detect wrapper for Linux/Mac/WSL hosts
#
# Detects whether the Docker daemon is in Linux or Windows containers mode and
# dispatches to the appropriate compose file automatically.
#
# Usage:
#   bash compose.sh up -d
#   bash compose.sh down
#   bash compose.sh logs -f
#   bash compose.sh build

set -euo pipefail

os_type=$(docker info --format '{{.OSType}}')
if [ "$os_type" = "windows" ]; then
  docker compose -f docker-compose.windows.yml "$@"
else
  docker compose "$@"
fi
