"""Validate real native-file import, authenticated live forwarding and late-joining WebSocket metadata."""
from pathlib import Path
import json, os, struct, subprocess, threading, time, urllib.request
from dataclasses import asdict
from streaming_fixture import choose_pause
root = Path(__file__).resolve().parent.parent
out = root / 'artifacts/native'
dotnet = os.environ.get('DOTNET_HOST_PATH', 'dotnet')
client = root / 'tools/NativeCapture/bin/Release/net10.0/NativeCapture.dll'
verifier = root / 'tests/DiagnosticsVisualizer.NativeImportValidation/bin/Release/net10.0/DiagnosticsVisualizer.NativeImportValidation.dll'
collector = root / 'src/DiagnosticsVisualizer.Collector/bin/Release/net10.0/DiagnosticsVisualizer.Collector.dll'
reference = out / 'reference.dvntrace'
env = os.environ.copy(); env['AVTRACE_TOKEN'] = os.urandom(32).hex()
pause = choose_pause(reference.read_bytes())
(out / 'native-streaming-plan.json').write_text(json.dumps({**asdict(pause), 'maximumReplayBurstFrames': 4, 'readiness': 'client-data-and-control-roundtrip'}, indent=2))
def run(arguments, log):
    result = subprocess.run([dotnet, *map(str, arguments)], capture_output=True, text=True, timeout=40, env=env, cwd=root)
    (out / log).write_text(result.stdout + result.stderr)
    if result.returncode: raise RuntimeError(f'{log} exited {result.returncode}')
run([verifier, 'file', reference, out / 'native-import-report.json'], 'native-import.log')
run([client, 'convert', reference, out / 'converted.avtrace'], 'native-convert.log')
source = out / 'follow-input.dvntrace'
if source.exists(): raise RuntimeError('Use clean native test outputs')
ready = threading.Event(); release = threading.Event(); cancel = threading.Event(); errors = []
def append():
    try:
        with reference.open('rb') as reader, source.open('xb', buffering=0) as writer:
            # Pause after enough records to evict the original method/thread definitions
            # from the collector tail. The late viewer must obtain materialized provenance.
            # Native event frames vary by producer throughput. A byte-paced
            # replay can release dozens of tiny Windows frames in one burst,
            # overwhelming the intentionally bounded 64-frame viewer queue.
            # This test checks streaming/late joining, not overload behavior.
            header = reader.read(68)
            if len(header) != 68: raise ValueError('Truncated reference header')
            writer.write(header); total = len(header); frame_count = 0
            while header := reader.read(12):
                if cancel.is_set(): return
                if len(header) != 12: raise ValueError('Truncated reference frame')
                length = struct.unpack_from('<I', header, 4)[0]
                if length > 65536: raise ValueError('Oversized reference frame')
                payload = reader.read(length)
                if len(payload) != length: raise ValueError('Truncated reference payload')
                writer.write(header[:5]); writer.write(header[5:])
                for start in range(0, length, 8192): writer.write(payload[start:start + 8192])
                total += 12 + length; frame_count += 1
                if total == pause.byte_offset and not ready.is_set():
                    ready.set()
                    if not release.wait(15): raise TimeoutError('Late viewer did not complete its data/control handshake')
                if frame_count % 4 == 0: time.sleep(.003)
    except BaseException as error:
        errors.append(str(error)); ready.set()
logs = []
def start(arguments, name):
    log = (out / name).open('w'); logs.append(log)
    return subprocess.Popen([dotnet, *map(str, arguments)], stdout=log, stderr=subprocess.STDOUT, env=env, cwd=root)
processes = []; worker = threading.Thread(target=append, daemon=True)
try:
    server = start([collector, '--http', 'http://127.0.0.1:4317', '--tcp', 'tcp://127.0.0.1:4318'], 'native-collector.log'); processes.append(server)
    for _ in range(100):
        try:
            with urllib.request.urlopen('http://127.0.0.1:4317/health', timeout=1) as response: json.load(response)
            break
        except OSError: time.sleep(.05)
    else: raise TimeoutError('Collector did not start')
    worker.start()
    for _ in range(100):
        if source.exists(): break
        time.sleep(.01)
    relay = start([client, 'relay', source, 'tcp://127.0.0.1:4318', '10000', '2'], 'native-relay.log'); processes.append(relay)
    if not ready.wait(15) or errors: raise RuntimeError(errors or 'Append workload did not reach the late-join point')
    for _ in range(100):
        with urllib.request.urlopen('http://127.0.0.1:4317/health', timeout=2) as response: health = json.load(response)
        if health['frames'] >= pause.blocks: break
        time.sleep(.02)
    else: raise RuntimeError('Events were not forwarded before the footer')
    viewer = start([verifier, 'remote', 'ws://127.0.0.1:4317/trace', out / 'native-relay-report.json'], 'native-viewer.log'); processes.append(viewer)
    readiness_deadline = time.monotonic() + 10
    while time.monotonic() < readiness_deadline:
        if 'NATIVE_VIEWER_READY' in (out / 'native-viewer.log').read_text(encoding='utf-8'): break
        if viewer.poll() is not None: raise RuntimeError('Viewer exited before its data/control handshake')
        time.sleep(.025)
    else: raise RuntimeError('Viewer did not confirm pre-footer data and control replies')
    release.set(); worker.join(20)
    if worker.is_alive() or errors: raise RuntimeError(errors or 'Writer did not terminate')
    relay.wait(20); viewer.wait(20)
    if relay.returncode or viewer.returncode: raise RuntimeError(f'Relay/viewer exited {relay.returncode}/{viewer.returncode}; inspect logs')
    report = json.loads((out / 'native-relay-report.json').read_text())
    assert report['eventsBeforeFooter'] and report['pingAcknowledged'] and report['binaryRoundtrip'], report
    print(json.dumps(report))
finally:
    cancel.set(); release.set()
    if worker.is_alive(): worker.join(3)
    for process in reversed(processes):
        if process.poll() is None: process.terminate()
        try: process.wait(5)
        except subprocess.TimeoutExpired: process.kill(); process.wait()
    for log in logs: log.close()
