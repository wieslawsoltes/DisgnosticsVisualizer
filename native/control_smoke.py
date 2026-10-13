"""Deterministic native pause boundaries, capability isolation, stop/revert and untouched application outputs."""
from pathlib import Path
import os, sys, subprocess, json, socket, struct, time, uuid, zlib, hashlib, threading, queue, urllib.request
root = Path(__file__).resolve().parent.parent
out = root / 'artifacts/native-control'; out.mkdir(parents=True, exist_ok=True)
lib = Path(sys.argv[1]).resolve(); dotnet = os.environ.get('DOTNET_HOST_PATH', 'dotnet')
fixture = root / 'tests/DiagnosticsVisualizer.NativeFixture/bin/Release/net10.0/NativeFixture.dll'
client = root / 'tools/NativeCapture/bin/Release/net10.0/NativeCapture.dll'
verifier = root / 'tests/DiagnosticsVisualizer.NativeImportValidation/bin/Release/net10.0/DiagnosticsVisualizer.NativeImportValidation.dll'
env = {k:v for k,v in os.environ.items() if not k.upper().startswith(('CORECLR_PROFILER','COR_PROFILER','CORECLR_ENABLE_PROFILING','COR_ENABLE_PROFILING','AVTRACE_'))}
secret = os.urandom(32); env['AVTRACE_NATIVE_CONTROL_TOKEN'] = secret.hex()
with socket.socket() as reservation: reservation.bind(('127.0.0.1',0)); port = reservation.getsockname()[1]
files = sorted(p for p in fixture.parent.iterdir() if p.is_file())
before = {p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
capture = out/'controlled.dvntrace'
if capture.exists(): raise RuntimeError('Use clean native-control outputs')
lines = queue.Queue(); log = []
p = subprocess.Popen([dotnet,str(fixture),'control'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=env)
def consume():
    for line in p.stdout: log.append(line); lines.put(line.strip())
threading.Thread(target=consume, daemon=True).start()
def expect(value):
    actual = lines.get(timeout=10)
    if not actual.startswith(value): raise RuntimeError((value,actual))
def workload(command, expected): p.stdin.write(command+'\n'); p.stdin.flush(); expect(expected)
def call(operation):
    command = subprocess.run([dotnet,str(client),'control',session,str(port),operation], env=env, text=True,capture_output=True,timeout=8)
    if command.returncode: raise RuntimeError((operation,command.stdout,command.stderr))
    return json.loads(command.stdout)
def deny(token, identity):
    packet = b'DVNCMD01'+identity+token+os.urandom(8)+struct.pack('<II',1,0)
    with socket.create_connection(('127.0.0.1',port),timeout=2) as sock:
        sock.sendall(packet)
        try: response = sock.recv(64)
        except ConnectionResetError: response = b''
        assert response == b'', 'Unauthenticated command received a response'
try:
    expect('READY')
    rules = ['NativeFixture!NativeFixture.Program::'+n for n in ['Work','Add','Held']]
    result = subprocess.run([dotnet,str(client),'attach',str(p.pid),str(lib),str(capture),'30000','--control-port',str(port),*rules],text=True,capture_output=True,env=env,timeout=20)
    (out/'attach.log').write_text(result.stdout+result.stderr)
    assert result.returncode==0,result.stderr
    session = json.loads(result.stdout)['session']
    time.sleep(.5) # Allow CLR ReJIT preparation before deterministic admission phases.
    assert call('status')['State']==0
    deny(os.urandom(32),uuid.UUID(session).bytes_le)
    deny(secret,uuid.uuid4().bytes_le)
    # A stalled unauthenticated local client cannot hold the control slot indefinitely.
    with socket.create_connection(('127.0.0.1',port),timeout=2) as sock: sock.sendall(b'DVNC'); time.sleep(.35)
    assert call('status')['State']==0
    workload('run','RUN_OK'); workload('hold','HOLD_OK')
    paused = call('pause'); assert paused['State']==1
    assert call('pause')['State']==1  # Idempotent, not a toggle.
    workload('run','RUN_OK'); workload('release','RELEASE_OK')
    workload('hold','HOLD_OK')  # This whole root must stay suppressed across resume.
    resumed = call('resume'); assert resumed['State']==0
    workload('release','RELEASE_OK'); workload('run','RUN_OK')
    collector = root/'src/DiagnosticsVisualizer.Collector/bin/Release/net10.0/DiagnosticsVisualizer.Collector.dll'
    env['AVTRACE_TOKEN'] = os.urandom(32).hex()
    logs = [(out/'collector.log').open('w'), (out/'relay.log').open('w')]
    server = subprocess.Popen([dotnet,str(collector)], env=env, stdout=logs[0],stderr=subprocess.STDOUT)
    relay = None
    try:
        for _ in range(100):
            try:
                with urllib.request.urlopen('http://127.0.0.1:4317/health',timeout=1): break
            except OSError: time.sleep(.05)
        else: raise RuntimeError('Collector did not start')
        relay = subprocess.Popen([dotnet,str(client),'relay',str(capture),'tcp://127.0.0.1:4318','--follow','--control-port',str(port)], env=env,stdout=logs[1],stderr=subprocess.STDOUT)
        checked = subprocess.run([dotnet,str(verifier),'control-viewer','ws://127.0.0.1:4317/trace',str(out/'viewer-control-report.json')], env=env,text=True,capture_output=True,timeout=15)
        (out/'viewer.log').write_text(checked.stdout+checked.stderr)
        assert checked.returncode==0,checked.stderr
    finally:
        if relay and relay.poll() is None: relay.terminate(); relay.wait(5)
        if server.poll() is None: server.terminate(); server.wait(5)
        for handle in logs: handle.close()
    assert call('status')['State']==0, 'Canceling the sidecar changed native recording state'
    stopped = call('revert'); assert stopped['State']==3 and stopped['Methods']==3 and stopped['FailedMethods']==0,stopped
    workload('run','RUN_OK'); workload('quit','CONTROL_WORKLOAD_OK'); p.wait(10); assert p.returncode==0
finally:
    if p.poll() is None: p.kill(); p.wait()
    (out/'workload.log').write_text(''.join(log)+p.stderr.read())
assert before=={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
b = capture.read_bytes(); assert b[:8]==b'DVNTRC01' and struct.unpack_from('<I',b,8)[0]==4 and zlib.crc32(b[:64])==struct.unpack_from('<I',b,64)[0]
pos=68; events=[]; states=[]; names={}; footer=None; statuses={}
while pos<len(b):
    kind,n,crc=struct.unpack_from('<III',b,pos); prefix=b[pos:pos+8]; data=b[pos+12:pos+12+n]; pos+=12+n
    assert n<=65536 and len(data)==n and zlib.crc32(data,zlib.crc32(prefix))==crc
    if kind==1:
        site=struct.unpack_from('<I',data)[0];a=struct.unpack_from('<I',data,24)[0];l=struct.unpack_from('<I',data,28+a)[0];names[site]=data[32+a:32+a+l].decode()
    elif kind==3:
        for i in range(struct.unpack_from('<I',data)[0]): events.append(struct.unpack_from('<QQQQIIII',data,4+48*i))
    elif kind==4: statuses[struct.unpack_from('<I',data)[0]]=struct.unpack('<IiiI',data)
    elif kind==6: states.append(struct.unpack('<IIQIiII',data))
    elif kind==5: footer=struct.unpack('<II9Q',data)
assert [s[1] for s in states]==[0,1,0,1,0,3],states
assert all(s[1]==3 and s[2]==0 for s in statuses.values()),statuses
assert footer and footer[0]==6 and footer[3:6]==(0,0,0) and footer[-1]==0,footer
starts={e[0]:e for e in events if e[6]==0}; ends={e[0]:e for e in events if e[6]!=0}
assert starts.keys()==ends.keys() and len(starts)>100
assert not [e for e in starts.values() if e[1]==0 and paused['Timestamp']<=e[2]<resumed['Timestamp']], 'A paused root was admitted'
held=[e for e in starts.values() if names[e[4]].endswith('::Held')];assert len(held)==1,held
assert any(e[1]==held[0][0] and e[2]>paused['Timestamp'] for e in starts.values()),'An admitted tree did not finish while paused'
check=subprocess.run([dotnet,str(verifier),'control-file',str(capture),str(out/'import-report.json')],env=env,capture_output=True,text=True,timeout=15)
(out/'import.log').write_text(check.stdout+check.stderr);assert check.returncode==0,check.stderr
report={'status':'passed','unauthenticatedRejected':True,'wrongSessionRejected':True,'slowClientBounded':True,'noPausedRoots':True,'admittedTreeCompleted':True,'suppressedTreeStayedSuppressed':True,'applicationFilesUnchanged':True,'pairedSpans':len(starts),'controlTransitions':len(states),'remotePauseResumeAcknowledged':True,'revert':stopped,'nativeLibraryUnloaded':False}
(out/'control-report.json').write_text(json.dumps(report,indent=2));print(json.dumps(report))
