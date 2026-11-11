import struct,sys,re
class R:
    def __init__(s,d,p): s.d=d; s.p=p
    def b(s): v=s.d[s.p]; s.p+=1; return v
    def ss(s): n=s.b(); v=s.d[s.p:s.p+n]; s.p+=n; return v.decode('cp1251')
    def u(s,f): v=struct.unpack_from(f,s.d,s.p)[0]; s.p+=struct.calcsize(f); return v
def val(r,t=None):
    if t is None: t=r.b()
    if t==0: return 'null'
    if t==1:
        L=[]
        while True:
            t2=r.b()
            if t2==0: break
            L.append(val(r,t2))
        return '('+' '.join(map(str,L))+')'
    if t==2: return r.u('<b')
    if t==3: return r.u('<h')
    if t==4: return r.u('<i')
    if t==5:
        raw=r.d[r.p:r.p+10]; r.p+=10
        m=int.from_bytes(raw[:8],'little'); e=int.from_bytes(raw[8:],'little')
        sg=-1 if e&0x8000 else 1; e&=0x7fff
        return sg*m*2.0**(e-16383-63) if m else 0.0
    if t in (6,7): return repr(r.ss()) if t==6 else r.ss()
    if t==8: return 'False'
    if t==9: return 'True'
    if t==10: n=r.u('<I'); r.p+=n; return f'<binary {n}>'
    if t==11:
        L=[]
        while True:
            s=r.ss()
            if not s: break
            L.append(s)
        return '['+', '.join(L)+']'
    if t==12: n=r.u('<I'); v=r.d[r.p:r.p+n].decode('cp1251'); r.p+=n; return repr(v)
    if t==13: return 'nil'
    if t==14:
        L=[]
        while True:
            t2=r.b()
            if t2==0: break
            idx=None
            if t2 in (2,3,4): idx=val(r,t2); t2=r.b()
            props=[]
            while True:
                n=r.ss()
                if not n: break
                props.append(f'{n}={val(r)}')
            L.append('item{'+'; '.join(props)+'}')
        return '<'+' '.join(L)+'>'
    if t==15: return r.u('<f')
    if t==16: return r.u('<q')/10000
    if t==17: return r.u('<d')
    if t==18: n=r.u('<I'); v=r.d[r.p:r.p+2*n].decode('utf-16le'); r.p+=2*n; return repr(v)
    if t==19: return r.u('<q')
    if t==20: n=r.u('<I'); v=r.d[r.p:r.p+n].decode('utf8'); r.p+=n; return repr(v)
    raise Exception(f'type {t} at {r.p}')
def obj(r,ind,out):
    f=r.d[r.p]
    if f&0xf0==0xf0:
        r.b()
        if f&2: val(r)
    cls=r.ss(); name=r.ss()
    out.append('  '*ind+f'object {name}: {cls}')
    while True:
        n=r.ss()
        if not n: break
        out.append('  '*ind+f'  {n} = {val(r)}')
    while r.d[r.p]!=0: obj(r,ind+1,out)
    r.b()
    out.append('  '*ind+'end')
d=open(sys.argv[1],'rb').read()
for m in re.finditer(b'TPF0',d):
    r=R(d,m.start()+4); out=[]
    try: obj(r,0,out)
    except Exception as e: out.append(f'!! {e}')
    print(f'===== @ {hex(m.start())}'); print('\n'.join(out))
