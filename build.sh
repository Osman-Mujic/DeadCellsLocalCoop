#!/bin/bash
# Builds the mod against the DCCM MDK unpacked at /home/claude/dccm-full (CI: set DCCM_ROOT).
set -e
export DCCM_MDK_ROOT=${DCCM_ROOT:-/home/claude/dccm-full}/core/mdk
export DEAD_CELLS_GAME_PATH=${DCCM_ROOT:-/home/claude/dccm-full}
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
cd "$(dirname "$0")/src"
dotnet build -c Release "$@" 2>&1 | grep -E "error|warning CS8|Build succeeded" | sed -E 's|^.*/src/||; s| \[/home[^]]*\]$||' | sort -u
