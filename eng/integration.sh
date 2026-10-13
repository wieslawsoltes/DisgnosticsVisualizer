#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
version="${1:-0.1.0-alpha.17}"
root="$PWD"
mkdir -p artifacts/integration
sample=tests/DiagnosticsVisualizer.Sample
# Startup deep profiling deliberately exercises large synchronous bursts.
export AVTRACE_SNAPSHOT_INTERVAL_MS=250
export AVTRACE_EVENTS_PER_THREAD=262144
export AVTRACE_MAX_THREADS=16
export AVTRACE_SAMPLE_EVERY=1
export AVTRACE_BINDING_OPERATIONS=1
export AVTRACE_PROPERTY_OPERATIONS=1
export AVTRACE_PROPERTY_SCALARS=1
export AVTRACE_ROUTED_EVENTS=1
export AVTRACE_RESOURCE_LOOKUPS=1
export AVTRACE_STYLE_EVALUATIONS=1
export AVTRACE_XAML_OBJECTS=1
export AVTRACE_DISPATCHER_OPERATIONS=1
export AVTRACE_ASYNC_OPERATIONS=1
export AVTRACE_FLOW_EXECUTION_CONTEXT=1
export AVTRACE_SUBSYSTEM_MASK=0xFFFFFFFFFFFFFFFF
dotnet publish "$sample" -c Release -o artifacts/integration/target \
  -p:Version="$version" -p:UsePackagedDiagnostics=true \
  -p:DiagnosticsEnabled=true -p:DiagnosticsAllowSignedRewrite=true -p:DiagnosticsDeepProfile=true \
  --source "$root/artifacts/packages" --source https://api.nuget.org/v3/index.json
cp artifacts/integration/target/avtrace.manifest.json artifacts/integration/weaving-report.json
rm -f artifacts/integration/sample.avtrace
AVTRACE_ENABLED=1 AVTRACE_FILE="$root/artifacts/integration/sample.avtrace" \
  timeout 45s dotnet artifacts/integration/target/DiagnosticsVisualizer.Sample.dll | tee artifacts/integration/workload.log
check_workload() {
  for expected in \
    'WORKLOAD_OK clicks=24 direct=23 items=4' \
    'ROUTING_WORKLOAD_OK handlers=4 handled=false fault=observed' \
    'ASYNC_WORKLOAD_OK notifications=1' \
    'RESOURCE_WORKLOAD_OK deferred=0' \
    'DISPATCHER_WORKLOAD_OK completed=2 aborted=0 fault=observed' \
    'RESOURCE_LOOKUP_WORKLOAD_OK merged=higher theme=dark lazy=1 null=found missing=false opaque=safe fault=preserved' \
    'STYLE_WORKLOAD_OK active=0.25 local=0.8 cleared=0.25 inactive=1' \
    'PROPERTY_WORKLOAD_OK 10|3|7|6|4|7|9|Original subscriber failure|property' \
    'BINDING_WORKLOAD_OK' 'BINDING_DISPATCH_OK' 'BINDING_THEME_OK'; do
    grep -q "$expected" "$1"
  done
}
check_workload artifacts/integration/workload.log
verifier=tests/DiagnosticsVisualizer.CaptureVerifier/bin/Release/net10.0/DiagnosticsVisualizer.CaptureVerifier.dll
dotnet "$verifier" artifacts/integration/sample.avtrace artifacts/integration/capture-report.json
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
  timeout 45s dotnet artifacts/integration/target/DiagnosticsVisualizer.Sample.dll | tee artifacts/integration/remote-workload.log
check_workload artifacts/integration/remote-workload.log
wait "$viewer_pid"
viewer_pid=
python3 - <<'PY'
import json, pathlib, urllib.request
root = pathlib.Path('artifacts/integration')
with urllib.request.urlopen('http://127.0.0.1:4317/health', timeout=5) as response:
    health = json.load(response)
(root / 'network-report.json').write_text(json.dumps(health, indent=2))
assert health['udpFrames'] > 0 and health['snapshotDeltaFrames'] > 0, health
assert health['udpRejectedPackets'] == 0, health
for name in ('capture-report.json', 'remote-report.json'):
    report = json.loads((root / name).read_text())
    assert report['DroppedEvents'] == 0 and report['ProbeFaults'] == 0 and report['DeltaFrames'] > 0, (name, report)
    assert report['Bindings']['CrossThreadDeliveries'] > 0 and report['Bindings']['InvalidMarkers'] == 0, (name, report)
    print(name, json.dumps({key: value for key, value in report.items() if key != 'Findings'}))
print(json.dumps(health))
PY
kill "$collector_pid"
wait "$collector_pid" || true
collector_pid=
