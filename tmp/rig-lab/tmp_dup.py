import struct, hashlib
P = r"E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0"
d = open(P,'rb').read()
numTypes,numFiles = struct.unpack_from('<2I', d, 4)
EO = 72 + numTypes*32
def toc(i):
    o = EO+i*80
    fid,tid,toff,soff,goff,u1,u2 = struct.unpack_from('<7Q', d, o)
    tsz,ssz,gsz,u3,u4,idx = struct.unpack_from('<6I', d, o+56)
    return fid,toff,tsz,u1,u2,d[toff:toff+tsz]
for i in (0,1,6,8):
    fid,toff,tsz,u1,u2,b = toc(i)
    print("[%d] fid=0x%016x toc=%d size=%d u1=%d u2=%d sha=%s" % (i,fid,toff,tsz,u1,u2,hashlib.sha256(b).hexdigest()[:16]))
print("0 == 6 ?", toc(0)[5]==toc(6)[5])
print("1 == 8 ?", toc(1)[5]==toc(8)[5])
