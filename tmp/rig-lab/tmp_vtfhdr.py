import sys, struct, os
sys.path.insert(0,'.')
import hd2_source as S
p = S.Packs()
for path in ['materials\\matchacheese\\lunalice\\shinano_body.vtf',
             'materials\\matchacheese\\lunalice\\shinano_face_e.vtf',
             'materials\\matchacheese\\lunalice\\_4.vtf',
             'materials\\matchacheese\\lunalice\\shinano_face_outline_mask.vtf']:
    blob = p.vtf.get(path)
    if blob is None: print("missing", path); continue
    sig, major, minor, hdr = struct.unpack_from('<4sIII', blob, 0)
    w, h, flags, frames, first = struct.unpack_from('<HHIHH', blob, 16)
    print("=== %s  size=%d sig=%r v%d.%d hdrSize=%d w=%d h=%d flags=0x%x frames=%d" % (path, len(blob), sig, major, minor, hdr, w, h, flags, frames))
    for o in range(0, 112, 16):
        print("   %04x  %s" % (o, ' '.join('%02x'%b for b in blob[o:o+16])))
    print("   u32@48,52,56..68:", [struct.unpack_from('<I', blob, 48)[0], struct.unpack_from('<I', blob, 52)[0]] + [struct.unpack_from('<I', blob, 56+4*i)[0] for i in range(4)])
