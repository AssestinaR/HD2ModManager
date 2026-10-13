# -*- coding: utf-8 -*-
"""texture pixel stats by id, from a patch."""
import os, sys, subprocess, tempfile
from pathlib import Path
import numpy as np
sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A, hd2_patch as HP
from PIL import Image
SDK = r'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
TEXCONV = os.path.join(SDK, 'deps', 'texconv.exe')

def stats(patch, tid):
    entries, _ = HP.scan_entries(patch)
    e = [x for x in entries if x['type_id'] == A.TEXTURE_TYPE_ID and x['file_id'] == tid]
    if not e:
        return 'MISSING %016x' % tid
    e = e[0]
    pl = HP.entry_payload(e, patch)
    t = A.Texture.read(pl['toc'], pl['gpu'])
    d = tempfile.mkdtemp(prefix='ts')
    dds = os.path.join(d, 'a.dds')
    open(dds, 'wb').write(t.to_dds())
    subprocess.run([TEXCONV, '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM', '-m', '1', '-o', d, dds], capture_output=True)
    png = os.path.join(d, 'a.png')
    if not os.path.exists(png):
        return '%s %dx%d (decode failed)' % (t.format_name, t.width, t.height)
    a = np.asarray(Image.open(png).convert('RGBA'), np.int32)
    return ('%s %dx%d  RGBmean=%s  Rmin/max=%d/%d  A min/max/mean=%d/%d/%.1f'
            % (t.format_name, t.width, t.height,
               a[:,:,:3].reshape(-1,3).mean(axis=0).round(1).tolist(),
               a[:,:,0].min(), a[:,:,0].max(),
               a[:,:,3].min(), a[:,:,3].max(), a[:,:,3].mean()))

REF = r'E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310-20260928-175115-436d5b/9ba626afa44a3aa3.patch_0'
M6 = r'E:/mod/Lunalice-RE2310-M6/9ba626afa44a3aa3.patch_0'
print('--- reference advanced (0x0d04d273fa5a4646) slots ---')
for i, tid in enumerate([0xe2245ea44a9d5bc6, 0x38775d670507376c, 0xbd0a5814b24e724c,
                         0xf5e1460e04204ebd, 0x0ebe0e2ff39187bb]):
    print('  slot %d %016x  %s' % (i, tid, stats(REF, tid)))
print('--- ours M6 ---')
for name, tid in [('shared_flat_normal', 0xba1a40900756a7f3), ('ornament color', 0xb8a1ba9aed375bed)]:
    print('  %-20s %016x  %s' % (name, tid, stats(M6, tid)))
