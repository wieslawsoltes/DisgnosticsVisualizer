#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
version="${1:-0.1.0-alpha.17}"
root="$PWD"
mkdir -p artifacts/gpu
# The workload itself has no diagnostics calls. Its alternate --backend-check
# mode explicitly verifies GL error and query-buffer preservation separately.
dotnet publish tests/DiagnosticsVisualizer.GpuFixture -c Release -o artifacts/gpu/packaged-target \
  -p:Version="$version" -p:UsePackagedDiagnostics=true -p:DiagnosticsEnabled=true -p:DiagnosticsAllowSignedRewrite=true \
  --source "$root/artifacts/packages" --source https://api.nuget.org/v3/index.json
cp artifacts/gpu/packaged-target/avtrace.manifest.json artifacts/gpu/weaving-report.json
python3 - <<'PY'
import json
manifest = json.load(open('artifacts/gpu/weaving-report.json'))
hooks = [m for a in manifest['Assemblies'] for m in a['Methods'] if m['Status'] == 'GpuTimestamp']
assert len(hooks) == 3, hooks
PY
export LIBGL_ALWAYS_SOFTWARE=1
AVTRACE_ENABLED=0 timeout 30s dotnet artifacts/gpu/packaged-target/DiagnosticsVisualizer.GpuFixture.dll --backend-check | tee artifacts/gpu/backend.log
AVTRACE_ENABLED=1 AVTRACE_GPU_TIMINGS=1 AVTRACE_EVENTS_PER_THREAD=65536 AVTRACE_FILE="$root/artifacts/gpu/capture.avtrace" \
  timeout 30s dotnet artifacts/gpu/packaged-target/DiagnosticsVisualizer.GpuFixture.dll | tee artifacts/gpu/workload.log
grep -q 'GPU_BACKEND_OK preserved-error=1 preserved-query-buffer=1' artifacts/gpu/backend.log
grep -q 'GPU_WORKLOAD_OK frames=48 disposed=48' artifacts/gpu/workload.log
dotnet tests/DiagnosticsVisualizer.CaptureVerifier/bin/Release/net10.0/DiagnosticsVisualizer.CaptureVerifier.dll \
  --gpu artifacts/gpu/capture.avtrace artifacts/gpu/gpu-report.json
