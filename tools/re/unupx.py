import struct,sys
def nrv2d(src, start):
    out=bytearray(); ip=start; bb=0; bc=0; last=1
    def gb():
        nonlocal ip,bb,bc
        if bc==0:
            bb=struct.unpack_from('<I',src,ip)[0]; ip+=4; bc=32
        bc-=1; return (bb>>bc)&1
    while True:
        while gb():
            out.append(src[ip]); ip+=1
        m=1
        while True:
            m=m*2+gb()
            if gb(): break
            m=(m-1)*2+gb()
        if m==2:
            m=last; ml=gb()
        else:
            m=(m-3)*256+src[ip]; ip+=1
            if m==0xffffffff: break
            ml=(m^0xffffffff)&1; m>>=1; m+=1; last=m
        ml=ml*2+gb()
        if ml==0:
            ml=1
            while True:
                ml=ml*2+gb()
                if gb(): break
            ml+=2
        ml+= (m>0x500)
        p=len(out)-m
        for k in range(ml+1): out.append(out[p+k])
    return out, ip
fn,outfn=sys.argv[1],sys.argv[2]
d=open(fn,'rb').read()
pe=struct.unpack_from('<I',d,0x3c)[0]
ep=struct.unpack_from('<I',d,pe+40)[0]
base=struct.unpack_from('<I',d,pe+52)[0]
so=pe+24+struct.unpack_from('<H',d,pe+20)[0]
secs=[struct.unpack_from('<8sIIII',d,so+40*i) for i in range(3)]
u1=secs[1]; off=ep-u1[2]+u1[4]
stub=d[off:off+0x400]
k=stub.find(b'\x60\xbe'); esi=struct.unpack_from('<I',stub,k+2)[0]
lea=struct.unpack_from('<i',stub,k+8)[0]
src_rva=esi-base; dst_rva=(esi+lea)&0xffffffff
dst_rva-=base
print('src rva',hex(src_rva),'dst rva',hex(dst_rva))
out,ip=nrv2d(d, src_rva-u1[2]+u1[4])
print('decompressed',hex(len(out)),'consumed to file off',hex(ip))
open(outfn,'wb').write(out)
open(outfn+'.stub','wb').write(stub)
