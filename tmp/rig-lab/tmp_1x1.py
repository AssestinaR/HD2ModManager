import os, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_source as S
import hd2_vtf as V
packs = S.Packs()
for stem in ['Ornament_metal_Underwear', '_4']:
    raw = packs.vtf_bytes(stem)
    v = V.VTF.read(raw)
    m0 = v.mips[0]
    print('%-30s fmt=%s %dx%d mips=%d hi=%d  bytes=%s  hex=%s'
          % (stem, v.format_name, v.width, v.height, v.mip_count, v.hi_format,
             list(m0[:8]), m0[:8].hex()))
