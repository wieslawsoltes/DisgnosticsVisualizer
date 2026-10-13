"""Validate real native-file import, authenticated live forwarding and late-joining WebSocket metadata."""
from pathlib import Path
import json, os, subprocess, threading, time, urllib.request
root = Path(__file__).resolve().parent.parent
out = root / 'artifacts/native'
dotnet = os.environ.get('DOTNET_HOST_PATH', 'dotnet')
client = root / 'tools/NativeCapture/bin/Release/net10.0/NativeCapture.dll'
verifier = root / 'tests/DiagnosticsVisualizer.NativeImportValidation/bin/Release/net10.0/DiagnosticsVisualizer.NativeImportValidation.dll'
collector = root / 'src/DiagnosticsVisualizer.Collector/bin/Release/net10.0/DiagnosticsVisualizer.Collector.dll'
reference = out / 'reference.dvntrace'
env = os.environ.copy(); env['AVTRACE_TOKEN'] = os.urandom(32).hex()
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
            total = 0
            while data := reader.read(16384):
                if cancel.is_set(): return
                writer.write(data); total += len(data)
                if total >= 2 * 1024 * 1024 and not ready.is_set():
                    ready.set()
                    if not release.wait(15): raise TimeoutError('Late viewer did not connect')
                time.sleep(.003)
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
        if health['frames'] > 100: break
        time.sleep(.02)
    else: raise RuntimeError('Events were not forwarded before the footer')
    viewer = start([verifier, 'remote', 'ws://127.0.0.1:4317/trace', out / 'native-relay-report.json'], 'native-viewer.log'); processes.append(viewer)
    for _ in range(100):
        with urllib.request.urlopen('http://127.0.0.1:4317/health', timeout=2) as response: health = json.load(response)
        if health['viewers'] > 0: break
        if viewer.poll() is not None: raise RuntimeError('Viewer exited before readiness')
        time.sleep(.02)
    else: raise RuntimeError('Viewer did not connect')
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
