# compose.ps1 — Docker Compose auto-detect wrapper for Windows hosts
#
# Detects whether Docker Desktop is in Linux or Windows containers mode and
# dispatches to the appropriate compose file automatically.
#
# Usage:
#   .\compose.ps1 up -d
#   .\compose.ps1 down
#   .\compose.ps1 logs -f
#   .\compose.ps1 build

$osType = docker info --format '{{.OSType}}'
if ($osType -eq 'windows') {
    docker compose -f docker-compose.windows.yml @args
} else {
    docker compose @args
}
