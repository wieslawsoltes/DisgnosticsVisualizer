"""Real native file -> authenticated TCP collector -> WebSocket viewer, plus a late viewer."""
from pathlib import Path
import os, subprocess, sys, time, json, socket, secrets, urllib.request, hashlib
root=Path(__file__).resolve().parent.parent
library=Path(sys.argv[1]).resolve()
out=root/'artifacts/native-relay';out.mkdir(parents=True,exist_ok=True)
capture=out/'live.dvntrace'
if capture.exists():raise SystemExit('Use a clean output directory; recordings are not overwritten')
def port():
 with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
http,tcp=port(),port()
while tcp==http:tcp=port()
dotnet=os.environ.get('DOTNET_HOST_PATH','dotnet')
client=root/'tools/NativeCapture/bin/Release/net10.0/NativeCapture.dll'
fixture=root/'tests/DiagnosticsVisualizer.NativeFixture/bin/Release/net10.0/NativeFixture.dll'
verifier=root/'tests/DiagnosticsVisualizer.NativeImportValidation/bin/Release/net10.0/DiagnosticsVisualizer.NativeImportValidation.dll'
collector=root/'src/DiagnosticsVisualizer.Collector/bin/Release/net10.0/DiagnosticsVisualizer.Collector.dll'
env=os.environ.copy();env['AVTRACE_TOKEN']=secrets.token_hex(32)
processes=[];files=[]
def start(name,args):
 log=(out/f'{name}.log').open('w');files.append(log)
 p=subprocess.Popen([dotnet,*map(str,args)],stdout=log,stderr=subprocess.STDOUT,env=env,cwd=root);processes.append(p);return p
try:
 c=start('collector',[collector,'--http',f'http://127.0.0.1:{http}','--tcp',f'tcp://127.0.0.1:{tcp}','--no-udp','--archive',out/'archive'])
 for _ in range(100):
  try:
   with urllib.request.urlopen(f'http://127.0.0.1:{http}/health',timeout=1) as f:
    if f.status==200:break
  except OSError:time.sleep(.05)
 else:raise TimeoutError('Collector startup timed out')
 v=start('viewer',[verifier,'viewer',f'ws://127.0.0.1:{http}/trace',out/'viewer.json'])
 for _ in range(100):
  with urllib.request.urlopen(f'http://127.0.0.1:{http}/health',timeout=1) as f:
   if json.load(f)['viewers']>0:break
  time.sleep(.025)
 else:raise TimeoutError('Viewer did not connect')
 t=start('target',[fixture,'8'])
 for _ in range(100):
  if 'READY ' in (out/'target.log').read_text():break
  time.sleep(.025)
 else:raise TimeoutError('Target did not become ready')
 names=['Work','Add','Generic','ReadRef','Throwing','Filtered','Inlined','FinallyCalls','FinallyThrows','Rethrow','NestedFilter']
 a=start('attach',[client,'attach',t.pid,library,capture,2000,*['NativeFixture!NativeFixture.Program::'+n for n in names]])
 assert a.wait(timeout=20)==0,'Attach failed'
 relay=start('relay',[client,'relay',capture,f'tcp://127.0.0.1:{tcp}','--follow','--attempts','1','--idle-ms','5000'])
 assert relay.wait(timeout=20)==0,'Relay failed'
 assert v.wait(timeout=10)==0,'Live viewer failed'
 late=start('late-viewer',[verifier,'late',f'ws://127.0.0.1:{http}/trace',out/'late.json'])
 assert late.wait(timeout=10)==0,'Late viewer failed'
 offline=start('offline',[verifier,'offline',capture,out/'offline.json'])
 assert offline.wait(timeout=20)==0,'Offline import/export failed'
 assert t.wait(timeout=15)==0,'Fixture behavior changed'
 reports={n:json.loads((out/f'{n}.json').read_text()) for n in ['viewer','late','offline']}
 assert reports['viewer']['events']==reports['offline']['events'],reports
 assert reports['viewer']['PairedSpans']==reports['offline']['PairedSpans'],reports
 for key in ('eventSha256','provenanceSha256'):
  assert reports['viewer'][key]==reports['offline'][key],(key,reports)
 assert reports['late']['events']<reports['viewer']['events'] and reports['late']['Incomplete'],reports
 with urllib.request.urlopen(f'http://127.0.0.1:{http}/health',timeout=1) as f:health=json.load(f)
 result={'status':'passed','captureSha256':hashlib.sha256(capture.read_bytes()).hexdigest(),'reports':reports,'health':health,
  'boundary':'Native target controls remain explicitly unsupported. The late viewer has complete method/thread provenance but only a retained event tail.'}
 (out/'relay-report.json').write_text(json.dumps(result,indent=2));print(json.dumps(result))
finally:
 for p in reversed(processes):
  if p.poll() is None:p.terminate()
 for p in processes:
  try:p.wait(timeout=5)
  except subprocess.TimeoutExpired:p.kill();p.wait()
 for f in files:f.close()
