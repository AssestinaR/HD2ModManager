import struct, os, glob
TN = {0xE0A48D0BE9A7453F:'Unit',0x18DEAD01056B72E9:'Bone',0xEAC0B497876ADEDF:'Material',0xCD4238C6A0C69E32:'Texture'}
def scan(P, show=8):
    d = open(P,'rb').read()
    magic,numTypes,numFiles = struct.unpack_from('<3I', d, 0)
    if magic != 4026531857:
        print("  !! bad magic 0x%x" % magic); return
    for TO in (72, 60):
        EO = TO + numTypes*32
        if EO + numFiles*80 > len(d): continue
        s=0; types=set()
        for i in range(numTypes):
            o=TO+i*32
            s += 1000 if struct.unpack_from('<Q',d,o+16)[0]==numFiles else 0
            types.add(struct.unpack_from('<Q',d,o+8)[0])
        for i in range(numFiles):
            o=EO+i*80
            if struct.unpack_from('<Q',d,o)[0]: s+=1
            if struct.unpack_from('<Q',d,o+8)[0] in types: s+=10
        if s < 100: continue
        print("  typeOff=%d entryOff=0x%x numTypes=%d numFiles=%d score=%d" % (TO,EO,numTypes,numFiles,s))
        n=0
        for i in range(numFiles):
            o=EO+i*80
            fid,tid,toff,soff,goff,u1,u2 = struct.unpack_from('<7Q',d,o)
            tsz,ssz,gsz,u3,u4,idx = struct.unpack_from('<6I',d,o+56)
            print("    [%2d] %-9s toc=%d(%d) gpu=%d(%d) u1=%d u2=%d u3=%d u4=%d idx=%d" %
                  (i,TN.get(tid,'?'),toff,tsz,goff,gsz,u1,u2,u3,u4,idx))
            n+=1
            if n>=show and i<numFiles-1:
                print("    ..."); break
        break
for pat in [r"E:/mod/ccc91a6f4a42408390e953da1f3c64e9/9ba626afa44a3aa3.patch_0",
            r"E:/mod/Lunalice-Visible-Virtual-Model/9ba626afa44a3aa3.patch_0",
            r"D:/SteamLibrary/steamapps/common/Helldivers 2/data/9ba626afa44a3aa3.patch_1"]:
    print("=== %s (%d B)" % (pat, os.path.getsize(pat) if os.path.exists(pat) else -1))
    try: scan(pat)
    except Exception as ex: print("  err", type(ex).__name__, ex)
# glob the vrc ones
for pat in glob.glob(r"E:/mod/VRchat*/*.patch_0")[:3]:
    print("=== %s (%d B)" % (pat, os.path.getsize(pat)))
    try: scan(pat, 4)
    except Exception as ex: print("  err", type(ex).__name__, ex)
