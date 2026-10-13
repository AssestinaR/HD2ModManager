import struct, os, glob
paths = [r"E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0",
         r"E:/mod/Lunalice-Visible-Virtual-Model/9ba626afa44a3aa3.patch_0",
         r"E:/mod/ccc91a6f4a42408390e953da1f3c64e9/9ba626afa44a3aa3.patch_0",
         r"D:/SteamLibrary/steamapps/common/Helldivers 2/data/9ba626afa44a3aa3.patch_1",
         r"E:/mod/fdf7fe609dea4fb0892bf3a3cd378145/9ba626afa44a3aa3.patch_0"]
for P in paths:
    if not os.path.exists(P):
        print("MISSING", P); continue
    d = open(P,'rb').read(80)
    magic,numTypes,numFiles,unk = struct.unpack_from('<4I', d, 0)
    print("%-60s size=%d magic=%d nt=%d nf=%d unk=%d" % (os.path.basename(os.path.dirname(P)), os.path.getsize(P), magic, numTypes, numFiles, unk))
    print("   unk4Data:", ' '.join('%02x'%b for b in d[16:72]))
