"""Losslessly wrap uncompressed RAC2 sections in the existing modern LZ4 container."""
import struct
import sys
import zlib
from pathlib import Path

def compress(data):
    table = [-1] * 65536
    out = bytearray()
    anchor = i = 0
    def extra(n):
        while n >= 255:
            out.append(255)
            n -= 255
        out.append(n)
    while i <= len(data) - 12:
        word = struct.unpack_from('<I', data, i)[0]
        h = ((word * 2654435761) & 0xffffffff) >> 16
        ref = table[h]
        table[h] = i
        if ref < 0 or i-ref > 65535 or data[ref:ref+4] != data[i:i+4]:
            i += 1
            continue
        end = i+4
        while end < len(data)-5 and data[ref+end-i] == data[end]:
            end += 1
        literals = i-anchor
        match = end-i-4
        out.append((min(literals,15)<<4)|min(match,15))
        if literals >= 15: extra(literals-15)
        out.extend(data[anchor:i])
        out.extend(struct.pack('<H',i-ref))
        if match >= 15: extra(match-15)
        i = anchor = end
    literals = len(data)-anchor
    out.append(min(literals,15)<<4)
    if literals >= 15: extra(literals-15)
    out.extend(data[anchor:])
    return bytes(out)

def decompress(data):
    out=bytearray(); i=0
    while i<len(data):
        token=data[i]; i+=1; size=token>>4
        if size==15:
            while True:
                n=data[i]; i+=1; size+=n
                if n!=255: break
        out.extend(data[i:i+size]); i+=size
        if i==len(data): break
        offset=struct.unpack_from('<H',data,i)[0]; i+=2
        assert 0<offset<=len(out)
        size=(token&15)+4
        if token&15==15:
            while True:
                n=data[i]; i+=1; size+=n
                if n!=255: break
        for _ in range(size): out.append(out[-offset])
    return bytes(out)

source,target=map(Path,sys.argv[1:])
assert not target.exists(), 'Do not overwrite existing delivery asset'
data=source.read_bytes()
magic,version,total,count,flags,toc=struct.unpack_from('<4s5I',data)
assert magic==b'RAC2' and version==3 and total==len(data) and flags==0
sections=[]
for n in range(count):
    tag,offset,size,_=struct.unpack_from('<4s3I',data,24+n*16)
    raw=data[offset:offset+size]; assert len(raw)==size
    packed=compress(raw)
    assert decompress(packed)==raw, 'LZ4 roundtrip mismatch'
    codec=int(len(packed)<len(raw))
    stored=packed if codec else raw
    sections.append((tag,stored,len(raw),codec,zlib.adler32(raw)&0xffffffff))
offset=24+count*24
total=offset+sum(len(s[1]) for s in sections)
result=bytearray(struct.pack('<4s5I',magic,version,total,count,3,24))
for tag,stored,size,codec,checksum in sections:
    result.extend(struct.pack('<4s5I',tag,offset,len(stored),size,codec,checksum)); offset+=len(stored)
for _,stored,_,_,_ in sections: result.extend(stored)
assert len(result)==total
target.parent.mkdir(parents=True,exist_ok=True)
target.write_bytes(result)
print(f'PASS: {len(data)} -> {len(result)} bytes; every section decompresses byte-for-byte to original')
