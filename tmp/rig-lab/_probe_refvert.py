"""Decode a few real vertices from a reference Unit so we know exactly what a
shipping vertex looks like (colour, uv0/1/2, normal encoding, weight encoding).

usage: _probe_refvert.py [unit_hex]
"""
import glob
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as HP
import hd2_unit as HU

cand = glob.glob(r'E:\mod\*\9ba626afa44a3aa3.patch_0')
REF = [c for c in cand if 'Chiffon' in c or '4.28' in c][0]
WANT = int(sys.argv[1], 16) if len(sys.argv) > 1 else 0x9841b16544c8fbae


def main():
    entries, _ = HP.scan_entries(REF)
    e = [x for x in entries if x['type_id'] == HP.UNIT_TYPE_ID and x['file_id'] == WANT][0]
    pay = HP.entry_payload(e, REF)
    u = HU.Unit.read(pay['toc'], pay['gpu'])
    print('unit 0x%016x  nStream=%d' % (WANT, len(u.stream_infos)))
    for si, s in enumerate(u.stream_infos):
        print('  stream %d: nv=%d stride=%d comps=%s'
              % (si, s.num_vertices, s.vertex_stride,
                 [(c.type, c.format, c.index) for c in s.components]))
    # decode first 6 vertices of stream 0
    for sidx in range(len(u.stream_infos)):
        vs = u.decode_vertices(sidx)
        print('  --- stream %d first 6 ---' % sidx)
        for i in range(min(6, len(vs))):
            print('    %2d %s' % (i, vs[i]))
    # weight/index statistics on stream 0
    vs = u.decode_vertices(0)
    import collections
    idxset = collections.Counter()
    wsum_bad = 0
    for r in vs:
        for k in r.get('bone_index0', []):
            idxset[k] += 1
        s = sum(r.get('bone_weight', []))
        if abs(s - 1.0) > 1e-6:
            wsum_bad += 1
    print('  distinct fake bone indices: %s' % sorted(idxset)[:40])
    print('  vertices whose half weights do not sum to 1: %d / %d' % (wsum_bad, len(vs)))
    # how many bones does the unit actually reference
    print('  -> real bones = %d' % len(u.bone_infos[0].real_indices))


if __name__ == '__main__':
    main()
