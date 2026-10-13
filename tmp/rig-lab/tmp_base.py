import struct, os
P = r"D:/SteamLibrary/steamapps/common/Helldivers 2/data/02582f3da1f8daf5"
d = open(P,'rb').read()
print("size", len(d))
magic,numTypes,numFiles,unk = struct.unpack_from('<4I', d, 0)
print("magic=%d nt=%d nf=%d unk=%d" % (magic,numTypes,numFiles,unk))
print("hdr 0x00-0x50:", ' '.join('%02x'%b for b in d[:0x50]))
TN = {0xE0A48D0BE9A7453F:'Unit',0x18DEAD01056B72E9:'Bone',0xEAC0B497876ADEDF:'Material',0xCD4238C6A0C69E32:'Texture'}
for TO in (60, 72):
    EO = TO + numTypes*32
    if EO + numFiles*80 > len(d): 
        print("  TO=%d too big" % TO); continue
    print("--- layout typeOff=%d entryOff=0x%x ---" % (TO, EO))
    for i in range(min(numTypes,6)):
        o = TO+i*32
        a,b,c,e,f = struct.unpack_from('<QQQII', d, o)
        print("   type[%d] unk1=0x%x type=0x%016x(%s) n=%d %d %d" % (i,a,b,TN.get(b,'?'),c,e,f))
    for i in range(min(numFiles,4)):
        o = EO+i*80
        fid,tid,toff,soff,goff,u1,u2 = struct.unpack_from('<7Q', d, o)
        tsz,ssz,gsz,u3,u4,idx = struct.unpack_from('<6I', d, o+56)
        print("   ent[%d] %-9s toc=%d(%d) strm=%d(%d) gpu=%d(%d) u1=%d u2=%d idx=%d" % (i,TN.get(tid,'?'),toff,tsz,soff,ssz,goff,gsz,u1,u2,idx))
