#!/usr/bin/env bash
# fnexportAPI - one-step start (Linux / macOS)
#
# Usage:
#   ./run.sh              -> http://localhost:3849
#   ./run.sh 5000         -> http://localhost:5000
#   ./run.sh 5000 Debug   -> Debug build
#
# Data (libs/, mappings/, chunk_cache/, build_history/ ...) is always kept in
# this folder, wherever the script is started from.
set -euo pipefail

cd "$(dirname "$0")"

export PORT="${1:-${PORT:-3849}}"
CONFIG="${2:-Release}"

if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    echo "ERROR: .NET 10 SDK was not found." >&2
    echo "Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this again." >&2
    exit 1
fi

echo
echo " fnexportAPI"
echo " Config  : $CONFIG"
echo " Swagger : http://localhost:$PORT/swagger"
echo " (Ctrl+C to stop. The first start downloads native libraries and the manifest.)"
echo

exec dotnet run --project FortnitePorting/FortnitePorting.csproj -c "$CONFIG"
