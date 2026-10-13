#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
version="${1:-0.1.0-alpha.15}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$ ]] || { echo 'Invalid package version' >&2; exit 2; }
mkdir -p artifacts/packages
for project in Core Protocol Runtime Analysis Transport Agent.Avalonia Charts Controls Workbench Build; do
  dotnet pack "src/DiagnosticsVisualizer.$project" -c Release -p:Version="$version" -o artifacts/packages
done
python3 eng/verify-packages.py artifacts/packages "$version"
