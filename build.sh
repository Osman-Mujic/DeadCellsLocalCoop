#!/bin/bash
# Builds the mod against an installed Dead Cells Core Modding (DCCM) MDK.
#   DCCM_DIR = a folder that contains DCCM's core/mdk (for example <game dir>/coremod, or an unpacked DCCM release)
# Usage: DCCM_DIR=/path/to/coremod ./build.sh
set -e
: "${DCCM_DIR:?Set DCCM_DIR to the folder that contains the DCCM core/mdk folder, e.g. <game dir>/coremod}"
export DCCM_MDK_ROOT="$DCCM_DIR/core/mdk"
export DEAD_CELLS_GAME_PATH="$DCCM_DIR"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
cd "$(dirname "$0")/src"
dotnet build -c Release "$@" 2>&1 | grep -E "error|warning CS8|Build succeeded" | sed -E "s|^/[^ ]*/src/||; s| \[/[^]]*\]$||" | sort -u; test ${PIPESTATUS[0]} -eq 0
echo "Output: src/bin/Release/net10.0/output/DeadCellsLocalCoop/"
