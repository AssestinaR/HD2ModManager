# -*- coding: utf-8 -*-
"""Per-section UV / material / texture census for a shipped unit.

Answers "what artwork does each section actually read, and how much of it".
Prints, for every section of every LOD-0 mesh: triangle count, material asset id,
the slot-5 (base colour) texture id + decoded size, the uv0 bbox (raw and after
wrap), and the fraction of the texture area the section's UV actually touches.

usage: _probe_uvsec.py <patch_0> <unit_hex>
"""
import os
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A
import hd2_patch as P
import hd2_unit as U

SDK = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0')
TEXCONV = SDK / 'deps' / 'texconv.exe'

NAMES = {
    0xb9f84331454d8282: 'shinano_face', 0xbe569f23c33c3083: 'white',
    0xbde6be9e588bc941: '_4', 0xb8a1ba9aed375bed: 'ornament_metal',
    0xb6e760aee076359d: 'shinano_body', 0xb66b54bafd3dd768: 'shinano_hair_ear',
    0xbbd058dd84a87e8c: 'headdress_white01_catears', 0xbea2867f775bc39b: 'lace_white01_catears',
    0xbb106a56c4d48a2b: 'bra_white01_bra_inner', 0xb3ba47e7968a0ab5: 'glove_white01_wristcuffs',
    0xb8f74de60c5f8803: 'dress_white01_dress_corset', 0xbc9d1471d87f8337: 'lower_white01_underwear',
    0xb65a1090f1511be3: 'shoes_white01_highheels', 0xbf29404cde77bd86: 'shoes_02',
}
BASE_SLOT = 5


def tex_size(toc, gpu, tmp, tag):
    t = A.Texture.read(toc, gpu)
    dds = os.path.join(tmp, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(bytes(t.dds_header) + bytes(gpu))
    subprocess.run([str(TEXCONV), '-nologo', '-y', '-ft', 'png',
                    '-f', 'R8G8B8A8_UNORM', '-m', '1', '-o', tmp, dds],
                   capture_output=True)
    png = os.path.join(tmp, tag + '.png')
    if not os.path.exists(png):
        return None
    from PIL import Image
    return Image.open(png).size


def main():
    patch = sys.argv[1]
    unit = int(sys.argv[2], 16)
    tmp = tempfile.mkdtemp(prefix='uvsec')
    entries, _ = P.scan_entries(patch)
    tex = {e['file_id']: e for e in entries if e['type_id'] == A.TEXTURE_TYPE_ID}
    mats = {e['file_id']: e for e in entries if e['type_id'] == A.MATERIAL_TYPE_ID}

    e = next(x for x in entries if x['type_id'] == U.UNIT_ID and x['file_id'] == unit)
    pl = P.entry_payload(e, patch)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    print('unit 0x%016x  streams=%d meshes=%d' % (unit, len(u.stream_infos), len(u.mesh_infos)))

    for si in range(len(u.stream_infos)):
        try:
            vs = u.decode_vertices(si)
        except Exception as ex:
            print('stream %d decode failed: %s' % (si, ex))
            continue
        uv = np.asarray([[v['uv0'][0], v['uv0'][1]] for v in vs], np.float64)
        print('  stream %d  %d verts  uv0 raw u %.3f..%.3f  v %.3f..%.3f'
              % (si, len(vs), uv[:, 0].min(), uv[:, 0].max(), uv[:, 1].min(), uv[:, 1].max()))

    for mi in u.mesh_infos:
        if mi.lod_index != 0:
            continue
        print('mesh lod0  %d sections' % len(mi.sections))
        for si, sec in enumerate(mi.sections):
            if sec.num_indices == 0:
                continue
            sid = mi.material_ids[sec.material_index]
            asset = u.material_ids[u.section_ids.index(sid)]
            me = mats.get(asset)
            nm = '?'
            tsz = None
            tid = 0
            if me is not None:
                m = A.Material.read(P.entry_payload(me, patch)['toc'])
                tid = m.tex_ids[BASE_SLOT] if len(m.tex_ids) > BASE_SLOT else 0
                nm = NAMES.get(tid, 'tex0x%016x' % tid)
                te = tex.get(tid)
                if te is not None:
                    tp = P.entry_payload(te, patch)
                    tsz = tex_size(tp['toc'], tp['gpu'], tmp, '%016x' % tid)
            print('   sec %2d  %6d tris  mat 0x%016x  tex 0x%016x %-32s %s'
                  % (si, sec.num_indices // 3, asset, tid, nm, tsz))
    return 0


if __name__ == '__main__':
    sys.exit(main())
