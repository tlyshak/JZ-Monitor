import re,struct,sys
asm,imgf,impf,out=sys.argv[1:5]
d=open(imgf,'rb').read(); B=0x401000
imp={int(l.split()[0],16):l.split()[1] for l in open(impf)}
names=dict()
# thunks: FF 25 <iat>
for m in re.finditer(rb'\xff\x25(....)',d):
    a=struct.unpack('<I',m.group(1))[0]
    if a in imp: names[B+m.start()]='@'+imp[a]
# delphi method tables: word len, dword addr, shortstr name
for m in re.finditer(rb'(..)(....)([\x03-\x40])([A-Za-z_][A-Za-z0-9_]+)',d,re.S):
    ln,addr=struct.unpack('<HI',m.group(1)+m.group(2)); n=m.group(3)[0]
    nm=m.group(4)
    if len(nm)>=n and ln==n+7 and B<=addr<B+len(d):
        names.setdefault(addr,nm[:n].decode())
open(out+'.names','w').write('\n'.join(f'{k:08X} {v}' for k,v in sorted(names.items())))
o=open(out,'w',encoding='utf-8')
for line in open(asm):
    m=re.match(r'\s+([0-9A-F]{8}):',line)
    if m:
        a=int(m.group(1),16)
        if a in names: o.write(f'\n;================ {names[a]}\n')
    def rep(mm):
        v=int(mm.group(1),16)
        if v in names: return mm.group(0)+' <'+names[v]+'>'
        if v in imp: return mm.group(0)+' <['+imp[v]+']>'
        if B<=v<B+len(d)-8 and False: pass
        return mm.group(0)
    line=re.sub(r'\b([0-9A-F]{8})h?\b',rep,line.rstrip('\n'))
    o.write(line+'\n')
