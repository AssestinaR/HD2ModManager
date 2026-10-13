import struct, glob, os
TN = {0xE0A48D0BE9A7453F:'Unit',0x18DEAD01056B72E9:'Bone',0xEAC0B497876ADEDF:'Material',0xCD4238C6A0C69E32:'Texture'}
def scan(P, show=6):
    d = open(P,'rb').read()
    magic,numTypes,numFiles = struct.unpack_from('<3I', d, 0)
    if magic != 4026531857: 
        print("  !! bad magic"); return
    # try both layouts
    for TO in (72, 60):
        EO = TO + numTypes*32
        if EO + numFiles*80 > len(d): continue
        # score
        s = 0
        types=set()
        for i in range(numTypes):
            o=TO+i*32
            s += 1000 if struct.unpack_from('<Q',d,o+16)[0]==numFiles else 0
            types.add(struct.unpack_from('<Q',d,o+8)[0])
        for i in range(numFiles):
            o=EO+i*80
            if struct.unpack_from('<Q',d,o)[0]: s+=1
            if struct.unpack_from('<Q',d,o+8)[0] in types: s+=10
        if s < 100: continue
        print("  typeOff=%d entryOff=0x%x score=%d" % (TO, EO, s))
        n=0
        for i in range(numFiles):
            o=EO+i*80
            fid,tid,toff,soff,goff,u1,u2 = struct.unpack_from('<7Q',d,o)
            tsz,ssz,gsz,u3,u4,idx = struct.unpack_from('<6I',d,o+56)
            print("    [%2d] %-9s fid=0x%016x toc=%d(%d) gpu=%d(%d) u1=%d u2=%d u3=%d u4=%d idx=%d" %
                  (i,TN.get(tid,'?'),fid,toff,tsz,goff,gsz,u1,u2,u3,u4,idx))
            n+=1
            if n>=show and i<numFiles-1: 
                print("    ...")
                break
        break
for P in ["/e/mod/VRchat_Milltina_Asmodeus 可脱穿 材质球-衣服-白黑/9ba626afa44a3aa3.patch_0",
          "/e/mod/VRchat_Milltina_Asmodeus-上衣/9ba626afa44a3aa3.patch_0",
          "/e/mod/ccc91a6f4a42408390e953da1f3c64e9/9ba626afa44a3aa3.patch_0",
          "/d/SteamLibrary/steamapps/common/Helldivers 2/data/9ba626afa44a3aa3.patch_1"]:
    print("=== %s (%d B)" % (P, os.path.getsize(P) if os.path.exists(P) else -1))
    try: scan(P)
    except Exception as ex: print("  err", ex)
