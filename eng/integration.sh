#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
version="${1:-0.1.0-alpha.2}"
root="$PWD"
mkdir -p artifacts/integration
sample=tests/DiagnosticsVisualizer.Sample
# Restore from the produced packages, not project references, to verify the actual consumer experience.
dotnet publish "$sample" -c Release -o artifacts/integration/target \
  -p:Version="$version" -p:UsePackagedDiagnostics=true \
  -p:DiagnosticsEnabled=true -p:DiagnosticsAllowSignedRewrite=true -p:DiagnosticsDeepProfile=true \
  --source "$root/artifacts/packages" --source https://api.nuget.org/v3/index.json
rm -f artifacts/integration/sample.avtrace
AVTRACE_ENABLED=1 AVTRACE_FILE="$root/artifacts/integration/sample.avtrace" \
  timeout 45s dotnet artifacts/integration/target/DiagnosticsVisualizer.Sample.dll | tee artifacts/integration/workload.log
grep -q 'WORKLOAD_OK clicks=24 direct=23 items=4' artifacts/integration/workload.log
dotnet run --project tests/DiagnosticsVisualizer.CaptureVerifier -c Release -- \
  artifacts/integration/sample.avtrace artifacts/integration/capture-report.json
cp artifacts/integration/target/avtrace.manifest.json artifacts/integration/weaving-report.json
