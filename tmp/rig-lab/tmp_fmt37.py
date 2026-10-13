import sys, struct
sys.path.insert(0,'.')
import hd2_source as S
p = S.Packs()
for path in ['materials\\matchacheese\\lunalice\\_4_n.vtf',
             'materials\\matchacheese\\lunalice\\white_n.vtf',
             'materials\\matchacheese\\lunalice\\shinano_body_n.vtf',
             'materials\\matchacheese\\lunalice\\shinano_face.vtf']:
    blob = p.vtf.get(path)
    if blob is None: print("MISSING", path); continue
    sig,major,minor,hdr = struct.unpack_from('<4sIII', blob,0)
    w,h,flags,frames,first = struct.unpack_from('<HHIHH', blob,16)
    hf, = struct.unpack_from('<I', blob, 52)
    mip = blob[56]; lof = blob[57]; lw, lh = blob[61], blob[62]
    depth, = struct.unpack_from('<H', blob, 63)
    print("%-40s %d.%d hdr=%d %dx%d flags=0x%x frames=%d fmt=%d mip=%d lo=%d/%dx%d depth=%d size=%d" %
          (path.split('\\')[-1], major, minor, hdr, w, h, flags, frames, hf, mip, lof, lw, lh, depth, len(blob)))
    # guess payload
    rest = len(blob) - hdr
    print("     rest=%d  /w/h=%.3f  blocks8=%d blocks16=%d" % (rest, rest/(w*h), rest/((w//4)*(h//4)), rest/((w//4)*(h//4))))
