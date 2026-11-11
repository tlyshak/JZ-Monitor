import re,struct,sys
asm,imgf=sys.argv[1],sys.argv[2]
d=open(imgf,'rb').read(); B=0x401000
def rd(a,n): return d[a-B:a-B+n]
def ann(v):
    if not (B<=v<B+len(d)-10): return None
    # delphi ansistring: refcnt -1, len
    rc,ln=struct.unpack_from('<iI',d,v-B-8)
    if rc==-1 and 0<ln<300:
        try: return '"'+rd(v,ln).decode('cp1251')+'"'
        except: pass
    return None
for line in open(asm,encoding='utf-8'):
    line=line.rstrip('\n')
    m=re.search(r'(fld|fmul|fdiv|fadd|fsub|fcomp|fcom|fdivr|fsubr|fild|fimul)\s+(dword|qword|tbyte) ptr (ds:)?\[([0-9A-F]{8})h\]',line)
    if m:
        a=int(m.group(4),16); t=m.group(2)
        try:
            if t=='dword': v=struct.unpack('<f',rd(a,4))[0] if m.group(1)[1]!='i' else struct.unpack('<i',rd(a,4))[0]
            elif t=='qword': v=struct.unpack('<d',rd(a,8))[0] if m.group(1)!='fild' else struct.unpack('<q',rd(a,8))[0]
            else:
                raw=rd(a,10); mm=int.from_bytes(raw[:8],'little'); e=int.from_bytes(raw[8:],'little'); s=-1 if e&0x8000 else 1; e&=0x7fff
                v=s*mm*2.0**(e-16383-63) if mm else 0.0
            if B<=a<0x4AF000: line+=f'   ; = {v:g}'
        except Exception as ex: pass
    for mm in re.finditer(r'\b([0-9A-F]{6,8})h\b',line):
        s=ann(int(mm.group(1),16))
        if s: line+='   ; '+s
    print(line)
