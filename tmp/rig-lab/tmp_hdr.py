import struct, sys
P = r"E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0"
d = open(P,'rb').read()
print("size", len(d))
for o in range(0, 0x120, 16):
    row = d[o:o+16]
    hx = ' '.join('%02x'%b for b in row)
    asc = ''.join(chr(b) if 32<=b<127 else '.' for b in row)
    print('%04x  %-47s  %s' % (o, hx, asc))
print()
print("u32[0..8]:", [struct.unpack_from('<I',d,4*i)[0] for i in range(9)])
print("magic", struct.unpack_from('<I',d,0)[0])
print("numTypes", struct.unpack_from('<I',d,4)[0], "numFiles", struct.unpack_from('<I',d,8)[0])
