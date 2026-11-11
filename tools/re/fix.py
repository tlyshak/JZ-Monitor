import struct,sys,re
sys.path.insert(0,'.')
from mkpe import mkpe
img,cnt,impoff,dllnameoff,out=sys.argv[1],int(sys.argv[2],16),int(sys.argv[3],16),int(sys.argv[4],16),sys.argv[5]
base=0x401000
d=bytearray(open(img,'rb').read())
# unfilter
i=0; n=cnt
def nxt(i):
    while True:
        al=d[i]; i+=1
        if (al-0xe8)&0xff<=1 and d[i]==0x28: return i
pos=0
while n>0:
    while True:
        al=(d[pos]-0xe8)&0xff; pos+=1
        if al<=1 and d[pos]==0x28: break
    while True:
        b1,b2,b3=d[pos+1],d[pos+2],d[pos+3]; bl=(d[pos+4]-0xe8)&0xff
        v=(b1<<16|b2<<8|b3)
        r=(v-(base+pos)+base)&0xffffffff
        struct.pack_into('<I',d,pos,r); pos+=5; n-=1
        if n==0 or not (bl<=1 and d[pos]==0x28): break
# imports
imp={}
p=impoff
while True:
    nameoff,iat=struct.unpack_from('<II',d,p)
    if nameoff==0: break
    dll=d[dllnameoff-0x1000+nameoff+0x1000-0x1000:].split(b'\0')[0] if False else None
    p+=8; slot=iat
    while True:
        al=d[p]; p+=1
        if al==0: break
        name=d[p:].split(b'\0')[0].decode(); p+=len(name)+1
        imp[0x401000+slot-0x1000+0x1000-0x1000+0x0]=name; slot+=4
open(out+'.imports','w').write('\n'.join(f'{k:08X} {v}' for k,v in sorted(imp.items())))
open(out+'.img','wb').write(d)
mkpe(d,0x1000,0x400000,out)
print(len(imp),'imports')
