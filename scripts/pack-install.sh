#!/usr/bin/env bash
set -euo pipefail

dotnet pack src/Waypoint.Cli -c Release -o ./nupkg -p:NuGetAudit=false
dotnet tool uninstall -g Waypoint.Cli || true
dotnet tool install -g --add-source ./nupkg Waypoint.Cli
