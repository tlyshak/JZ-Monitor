import struct,sys
def mkpe(data, rva, base, out):
    data=bytes(data)+b'\0'*((-len(data))%0x200)
    h=bytearray(0x400)
    h[0:2]=b'MZ'; struct.pack_into('<I',h,0x3c,0x80)
    h[0x80:0x84]=b'PE\0\0'
    struct.pack_into('<HHIIIHH',h,0x84,0x14c,1,0,0,0,0xe0,0x0102)
    o=0x98
    struct.pack_into('<HBBIIIIIII',h,o,0x10b,0,0,len(data),0,0,rva,rva,0,base)
    struct.pack_into('<IIHHHHHHIIIIHHIIIIII',h,o+32,0x1000,0x200,4,0,0,0,4,0,0,(rva+len(data)+0xfff)&~0xfff,0x400,0,2,0,0x100000,0x1000,0x100000,0x1000,0,16)
    so=o+0xe0
    struct.pack_into('<8sIIIIIIHHI',h,so,b'.text',len(data),rva,len(data),0x400,0,0,0,0,0x60000020)
    open(out,'wb').write(h+data)
if __name__=='__main__':
    src,rva,base,out=sys.argv[1],int(sys.argv[2],16),int(sys.argv[3],16),sys.argv[4]
    off=int(sys.argv[5],16) if len(sys.argv)>5 else 0
    ln=int(sys.argv[6],16) if len(sys.argv)>6 else None
    d=open(src,'rb').read()[off:]
    if ln: d=d[:ln]
    mkpe(d,rva,base,out)
