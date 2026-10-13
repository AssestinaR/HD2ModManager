import struct
P = r"E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0"
d = open(P,'rb').read()
for o in range(0x118, 0x200, 16):
    row = d[o:o+16]
    print('%04x  %s' % (o, ' '.join('%02x'%b for b in row)))
print()
known = {
 0x17da843a4908511e:'Hip',
 0x0d04d273fa5a4646:'Mat0',
 0x0228cdef42ee1e6e:'Mat1',
 0xbd0a5814b24e724c:'Tex0',
 0xf5e1460e04204ebd:'Tex1',
 0x0ebe0e2ff39187bb:'Tex2',
 0x4d1c334d294dfa97:'avatar',
}
print("--- search FileIDs ---")
for fid,nm in known.items():
    pat = struct.pack('<Q', fid)
    i = d.find(pat)
    while i >= 0 and i < 0x2000:
        print('  %-8s 0x%016x found @ 0x%x   (mod80=%d, mod80_from_C8=%d)' % (nm, fid, i, i%80, (i-0xC8)%80))
        i = d.find(pat, i+1)
