import os, sys, struct
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A, hd2_patch as HP
src = r'E:/mod/Lunalice-RE2310-M6/9ba626afa44a3aa3.patch_0'
entries, _ = HP.scan_entries(src)
for tid in (0xbbd058dd84a87e8c, 0xb8a1ba9aed375bed, 0xba1a40900756a7f3):
    e = [x for x in entries if x['file_id'] == tid]
    if not e: print('%016x MISSING' % tid); continue
    pl = HP.entry_payload(e[0], src)
    t = A.Texture.read(pl['toc'], pl['gpu'])
    h = t.dds_header
    print('%016x  %dx%d mips=%d fmt=%s  flags=%08x pitch=%d linearsize=%d caps=%08x caps2=%08x'
          % (tid, t.width, t.height, t.mip_count, t.format_name,
             struct.unpack_from('<I', h, 8)[0], struct.unpack_from('<I', h, 20)[0],
             struct.unpack_from('<I', h, 24)[0], struct.unpack_from('<I', h, 108)[0],
             struct.unpack_from('<I', h, 112)[0]))
    print('     hdr[0:8]=%s  hdr[84:88]=%s  dxgi=%d array=%d' % (h[0:8].hex(), h[84:88], t.dxgi_format, t.array_size))
