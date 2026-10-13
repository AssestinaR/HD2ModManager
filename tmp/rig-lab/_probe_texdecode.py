# -*- coding: utf-8 -*-
"""READ-ONLY: decode our own Texture assets and report their actual pixels.

Writes only into a temp directory.  Answers "is the base colour map itself
black, or is the model black for some other reason?"
"""
import os
import struct
import subprocess
import sys
import tempfile

import numpy as np

import hd2_asset as A
import hd2_patch as P

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
TEXCONV = os.path.join(SDK, 'deps', 'texconv.exe')
OURS = sys.argv[1] if len(sys.argv) > 1 else \
    'E:/mod/Lunalice-RE2310-Test/9ba626afa44a3aa3.patch_0'

# file ids we care about, from _probe_matcompare.py
LABEL = {
    0xb4948ba8ac86844a: 'shared PBR  (slot 0, B=0)',
    0xbd60d3e909ea3ba8: 'shared PBR  (slot 0, B=255)',
    0xba1a40900756a7f3: 'shared flat normal (slot 2)',
    0xb9f84331454d8282: 'base colour A',
    0xbe569f23c33c3083: 'base colour B',
    0xbb106a56c4d48a2b: 'base colour C',
}


def main():
    entries, raw = P.scan_entries(OURS)
    tmp = tempfile.mkdtemp(prefix='texdec')
    print('%-34s %-16s %-11s  %s' % ('label', 'format', 'size', 'mean RGB(A) 0..255'))
    for e in entries:
        if e['type_id'] != A.TEXTURE_TYPE_ID:
            continue
        pl = P.entry_payload(e, OURS)
        t = A.Texture.read(pl['toc'])
        label = LABEL.get(e['file_id'], '0x%016x' % e['file_id'])
        dds = os.path.join(tmp, '%016x.dds' % e['file_id'])
        with open(dds, 'wb') as f:
            f.write(bytes(t.dds_header) + pl['gpu'])
        p = subprocess.run([TEXCONV, '-nologo', '-y', '-ft', 'png',
                            '-f', 'R8G8B8A8_UNORM', '-m', '1', '-o', tmp, dds],
                           capture_output=True)
        png = os.path.join(tmp, '%016x.png' % e['file_id'])
        if not os.path.exists(png):
            print('%-34s %-16s %-11s  DECODE FAILED %s'
                  % (label, t.format_name, '%dx%d' % (t.width, t.height),
                     p.stdout.decode('utf-8', 'replace')[:60]))
            continue
        from PIL import Image
        im = Image.open(png)
        arr = np.asarray(im.convert('RGBA'), np.float64)
        mean = arr.reshape(-1, 4).mean(axis=0)
        lo = arr.reshape(-1, 4).min(axis=0)
        hi = arr.reshape(-1, 4).max(axis=0)
        print('%-34s %-16s %-11s  mean=(%6.1f,%6.1f,%6.1f,%6.1f)  '
              'min=(%5.0f,%5.0f,%5.0f,%5.0f) max=(%5.0f,%5.0f,%5.0f,%5.0f)'
              % (label, t.format_name, '%dx%d' % (t.width, t.height),
                 mean[0], mean[1], mean[2], mean[3],
                 lo[0], lo[1], lo[2], lo[3],
                 hi[0], hi[1], hi[2], hi[3]))
    import shutil
    shutil.rmtree(tmp, ignore_errors=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
