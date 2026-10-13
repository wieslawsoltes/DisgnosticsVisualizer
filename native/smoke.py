"""Run a bounded, real late-attach/ReJIT fixture with no startup profiler or application references."""
from pathlib import Path
import subprocess,sys,time,json,hashlib,struct,zlib,os,collections
root=Path(__file__).resolve().parent.parent
library=Path(sys.argv[1]).resolve()
output=root/'artifacts/native';output.mkdir(parents=True,exist_ok=True)
dotnet=os.environ.get('DOTNET_HOST_PATH','dotnet')
fixture=root/'tests/DiagnosticsVisualizer.NativeFixture/bin/Release/net10.0/NativeFixture.dll'
client=root/'tools/NativeCapture/bin/Release/net10.0/NativeCapture.dll'
paths=sorted(p for p in fixture.parent.iterdir() if p.is_file())
hashes={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in paths}
env=os.environ.copy()
for k in list(env):
    if k.upper().startswith(('COR_ENABLE_PROFILING','CORECLR_ENABLE_PROFILING','COR_PROFILER','CORECLR_PROFILER','AVTRACE_')):env.pop(k)
capture=output/'reference.dvntrace'
if capture.exists():raise SystemExit('Use a clean output directory; reference recording will not be overwritten')
target_out=(output/'target.log').open('w');target_err=(output/'target-error.log').open('w')
target=subprocess.Popen([dotnet,str(fixture),'12'],stdout=target_out,stderr=target_err,env=env,cwd=root)
try:
    deadline=time.monotonic()+12
    while time.monotonic()<deadline:
        if 'READY ' in (output/'target.log').read_text():break
        if target.poll() is not None:raise RuntimeError('Fixture exited before readiness')
        time.sleep(.05)
    else:raise TimeoutError('Target startup exceeded deadline')
    # A second delay allows initially compiled callers to execute before attachment.
    time.sleep(.3)
    names=['Work','Add','Generic','ReadRef','Throwing','Filtered','Inlined','FinallyCalls','FinallyThrows','Rethrow','NestedFilter']
    rules=['NativeFixture!NativeFixture.Program::'+n for n in names]
    attached=subprocess.run([dotnet,str(client),'attach',str(target.pid),str(library),str(capture),'3000',*rules],capture_output=True,text=True,timeout=25,env=env,cwd=root)
    (output/'attach.log').write_text(attached.stdout+attached.stderr)
    assert attached.returncode==0,(attached.returncode,attached.stdout,attached.stderr)
    target.wait(timeout=25)
    assert target.returncode==0,(target.returncode,(output/'target-error.log').read_text())
finally:
    if target.poll() is None:target.kill();target.wait()
    target_out.close();target_err.close()
assert hashes=={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in paths},'Target output files changed'
lines=(output/'target.log').read_text().splitlines();results=[json.loads(line) for line in lines if line.startswith('{')]
assert results and results[-1]['iterations']==results[-1]['finallyCount'] and results[-1]['iterations']>10000,results
b=capture.read_bytes();assert b[:8]==b'DVNTRC01' and len(b)<=128*1024*1024
assert struct.unpack_from("<II",b,8)==(2,0)
assert zlib.crc32(b[:64])==struct.unpack_from("<I",b,64)[0]
position=68;methods={};status={};threads={};events=[];footer=None
while position<len(b):
    kind,length,crc=struct.unpack_from('<III',b,position);header=b[position:position+8];position+=12
    assert length<=65536 and position+length<=len(b)
    payload=b[position:position+length];position+=length
    assert zlib.crc32(payload,zlib.crc32(header))==crc
    if kind==1:
        site=struct.unpack_from('<I',payload)[0];n=struct.unpack_from('<I',payload,24)[0];assembly=payload[28:28+n].decode('utf-8');m=struct.unpack_from('<I',payload,28+n)[0];name=payload[32+n:32+n+m].decode('utf-8');assert len(payload)==32+n+m
        assert site not in methods;methods[site]=assembly+'!'+name
    elif kind==2:
        slot,os_thread=struct.unpack('<IQ',payload);assert slot not in threads;threads[slot]=os_thread
    elif kind==3:
        count=struct.unpack_from('<I',payload)[0];assert len(payload)==4+48*count
        for i in range(count):events.append(struct.unpack_from('<QQQQIIII',payload,4+48*i))
    elif kind==4:
        site,state,hresult,compiled=struct.unpack('<IiiI',payload);status[site]=(state,hresult,compiled)
    elif kind==5:
        assert footer is None and position==len(b);footer=struct.unpack('<II9Q',payload)
    else:raise ValueError('Unknown native frame')
assert footer is not None and footer[0]==0,footer
assert footer[3:6]==(0,0,0) and footer[9]==0,footer # drops, probe/I/O faults and ReJIT errors
assert set(methods.values())==set(rules),methods
assert all(state==2 and hr==0 and count>0 for state,hr,count in status.values()),status
assert len(threads)>=4,threads
starts={};completed={};method_counts=collections.Counter();outcomes=collections.Counter()
for cookie,parent,at,duration,site,thread,kind,flags in events:
    assert site in methods and thread in threads and flags==0 and (cookie>>48)==thread
    assert kind in (0,1,2)
    if kind==0:
        assert cookie not in starts;starts[cookie]=(parent,at,site,thread);assert duration==0
    else:
        assert cookie not in completed;completed[cookie]=(parent,at,duration,site,thread,kind)
        method_counts[methods[site]]+=1;outcomes[kind]+=1
for cookie,(parent,at,duration,site,thread,kind) in completed.items():
    assert starts[cookie]==(parent,at,site,thread)
    if parent:
        assert parent in starts and starts[parent][1]<=at and starts[parent][3]==thread
        if parent in completed:assert at+duration<=completed[parent][1]+completed[parent][2]
    if methods[site].endswith('::Throwing'):assert kind==2
    else:assert kind==1
assert len(starts)-len(completed)==footer[-1],(len(starts),len(completed),footer)
assert all(method_counts[r]>20 for r in rules),method_counts
report={'status':'passed','attachedToAlreadyRunningProcess':True,'targetFilesUnchanged':True,'target':results[-1],
        'events':len(events),'threads':len(threads),'completed':len(completed),'openAtStop':footer[-1],
        'droppedRecords':footer[3],'recorderFaults':footer[4],'ioFaults':footer[5],'requestedMethods':footer[6],
        'instrumentedMethods':footer[7],'rejitCompilations':footer[8],'rejitErrors':footer[9],
        'returned':outcomes[1],'unwound':outcomes[2],'calls':dict(method_counts),'fileSha256':hashlib.sha256(b).hexdigest(),
        'boundary':'Observed selected-method lifetimes include waits. This is not GPU or CPU-utilization measurement, an async lifetime, or Avalonia object-tree inspection.'}
(output/'native-report.json').write_text(json.dumps(report,indent=2));print(json.dumps(report))
