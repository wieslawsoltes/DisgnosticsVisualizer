#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
version="${1:-0.1.0-alpha.2}"
root="$PWD"
mkdir -p artifacts/integration
sample=tests/DiagnosticsVisualizer.Sample
# Consume produced packages: do not substitute project references for package validation.
dotnet publish "$sample" -c Release -o artifacts/integration/target \
  -p:Version="$version" -p:UsePackagedDiagnostics=true \
  -p:DiagnosticsEnabled=true -p:DiagnosticsAllowSignedRewrite=true -p:DiagnosticsDeepProfile=true \
  --source "$root/artifacts/packages" --source https://api.nuget.org/v3/index.json
cp artifacts/integration/target/avtrace.manifest.json artifacts/integration/weaving-report.json
rm -f artifacts/integration/sample.avtrace
AVTRACE_ENABLED=1 AVTRACE_FILE="$root/artifacts/integration/sample.avtrace" \
  timeout 45s dotnet artifacts/integration/target/DiagnosticsVisualizer.Sample.dll | tee artifacts/integration/workload.log
grep -q 'WORKLOAD_OK clicks=24 direct=23 items=4' artifacts/integration/workload.log
verifier=tests/DiagnosticsVisualizer.CaptureVerifier/bin/Release/net10.0/DiagnosticsVisualizer.CaptureVerifier.dll
dotnet "$verifier" artifacts/integration/sample.avtrace artifacts/integration/capture-report.json

# A second real execution exercises reliable bootstrap/checkpoints, encrypted UDP data,
# WebSocket viewing and a round-trip control request, using an ephemeral test-only credential.
collector=src/DiagnosticsVisualizer.Collector/bin/Release/net10.0/DiagnosticsVisualizer.Collector.dll
export AVTRACE_TOKEN
AVTRACE_TOKEN="$(dotnet "$collector" --new-token)"
collector_pid= viewer_pid=
cleanup() {
  [[ -z "$viewer_pid" ]] || kill "$viewer_pid" 2>/dev/null || true
  [[ -z "$collector_pid" ]] || kill "$collector_pid" 2>/dev/null || true
  wait 2>/dev/null || true
}
trap cleanup EXIT
dotnet "$collector" --archive "$root/artifacts/integration/remote-archive" > artifacts/integration/collector.log 2>&1 &
collector_pid=$!
python3 - <<'PY'
import time, urllib.request
for attempt in range(100):
    try:
        with urllib.request.urlopen('http://127.0.0.1:4317/health', timeout=1) as response:
            if response.status == 200: break
    except OSError: time.sleep(.1)
else: raise SystemExit('Collector did not start')
PY
dotnet "$verifier" --remote ws://127.0.0.1:4317/trace artifacts/integration/remote-report.json > artifacts/integration/viewer.log 2>&1 &
viewer_pid=$!
AVTRACE_ENABLED=1 AVTRACE_ENDPOINT=tcp://127.0.0.1:4318 AVTRACE_UDP_ENDPOINT=127.0.0.1:4319 \
  timeout 45s dotnet artifacts/integration/target/DiagnosticsVisualizer.Sample.dll | tee -a artifacts/integration/workload.log
wait "$viewer_pid"
viewer_pid=
python3 - <<'PY'
import json, pathlib, urllib.request
with urllib.request.urlopen('http://127.0.0.1:4317/health', timeout=5) as response:
    health = json.load(response)
pathlib.Path('artifacts/integration/network-report.json').write_text(json.dumps(health, indent=2))
assert health['udpFrames'] > 0, health
assert health['udpRejectedPackets'] == 0, health
print(json.dumps(health))
PY
kill "$collector_pid"
wait "$collector_pid" || true
collector_pid=
