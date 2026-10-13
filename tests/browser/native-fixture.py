"""Create an explicitly synthetic, checksummed native file for real browser file-picker tests."""
from pathlib import Path
import struct,uuid,zlib
out=Path('artifacts/browser/SYNTHETIC-native.dvntrace');out.parent.mkdir(parents=True,exist_ok=True)
header=b'DVNTRC01'+struct.pack('<IIQQQ',3,0,1_000_000_000,0,638000000000000000)+uuid.UUID('84527711-30b2-400c-876c-68660aeb4211').bytes_le+struct.pack('<II',100,8)
data=header+struct.pack('<I',zlib.crc32(header))
def text(s):
 b=s.encode();return struct.pack('<I',len(b))+b
def frame(kind,payload):
 global data
 h=struct.pack('<II',kind,len(payload));data+=h+struct.pack('<I',zlib.crc32(payload,zlib.crc32(h)))+payload
frame(1,struct.pack('<I',1)+uuid.UUID('2817d5f1-d02b-407a-8e91-609774590e76').bytes_le+struct.pack('<I',0x06000001)+text('SyntheticBrowserFixture')+text('SyntheticBrowserFixture.Method'))
frame(2,struct.pack('<IQ',1,789))
cookie=(1<<48)+1
frame(3,struct.pack('<I',2)+b''.join(struct.pack('<QQQQIIII',cookie,0,100_000_000,0 if phase==0 else 200_000_000,1,1,phase,0) for phase in (0,1)))
frame(4,struct.pack('<IiiI',1,3,0,1))
frame(5,struct.pack('<II9Q',0,0,400_000_000,0,0,0,1,1,1,0,0))
out.write_bytes(data)
