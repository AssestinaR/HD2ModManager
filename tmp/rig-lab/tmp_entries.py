import struct
P = r"E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0"
d = open(P,'rb').read()
magic,numTypes,numFiles = struct.unpack_from('<3I', d, 0)
TYPE_OFF = 72
ENTRY_OFF = TYPE_OFF + numTypes*32
print("magic=%d numTypes=%d numFiles=%d typeOff=0x%x entryOff=0x%x" % (magic,numTypes,numFiles,TYPE_OFF,ENTRY_OFF))
TN = {0xE0A48D0BE9A7453F:'Unit',0x18DEAD01056B72E9:'Bone',0xEAC0B497876ADEDF:'Material',0xCD4238C6A0C69E32:'Texture'}
print("--- types ---")
for i in range(numTypes):
    o = TYPE_OFF + i*32
    unk, tid, cnt, a, b = struct.unpack_from('<QQQII', d, o)
    print("  [%d] unk=0x%x type=0x%016x(%s) count=%d  %d %d" % (i, unk, tid, TN.get(tid,'?'), cnt, a, b))
print("--- entries ---")
for i in range(numFiles):
    o = ENTRY_OFF + i*80
    fid, tid, toff, soff, goff, u1, u2 = struct.unpack_from('<7Q', d, o)
    tsz, ssz, gsz, u3, u4, idx = struct.unpack_from('<6I', d, o+56)
    print("  [%2d] %-9s fid=0x%016x toc=%d(%d) strm=%d(%d) gpu=%d(%d) u1=%d u2=%d u3=%d u4=%d idx=%d"
          % (i, TN.get(tid,'?'), fid, toff, tsz, soff, ssz, goff, gsz, u1, u2, u3, u4, idx))
print("entry table end = 0x%x ; file size %d" % (ENTRY_OFF+numFiles*80, len(d)))
